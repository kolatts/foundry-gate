using System.Globalization;
using System.Linq.Expressions;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Api.Services.Keys;
using FoundryGate.Api.Services.Lifecycle;
using FoundryGate.Api.Services.Quota;
using FoundryGate.Core.Configuration;
using FoundryGate.Core.Quota;
using FoundryGate.Data;
using FoundryGate.Data.Entities;
using FoundryGate.Data.Extensions;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Config.Contracts;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Users.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Api.Services.Users;

/// <summary>
/// Default <see cref="IUserService"/>. Scoped: shares the request's <see cref="AppDbContext"/> with the
/// lifecycle orchestrator, the quota services and the audit path, so a profile read that provisions a
/// user is one unit of work rather than three.
/// </summary>
public sealed class UserService(
    AppDbContext dbContext,
    IUserLifecycleService lifecycle,
    IQuotaAllocationService quotaAllocations,
    IQuotaResolutionService quotaResolution,
    IApimKeyService keys,
    GatewayTierMapper tierMapper,
    GatewayOptions gateway,
    IAuditService audit,
    ICurrentUserAccessor currentUser,
    TimeProvider timeProvider,
    ILogger<UserService> logger) : IUserService
{
    /// <summary><c>User.DisplayName</c> is <c>[StringLength(200)]</c>; a token's <c>name</c> claim can be longer.</summary>
    private const int DisplayNameMaxLength = 200;

    /// <summary>
    /// The one user projection, so the list rows and the detail's <c>user</c> block are the same shape
    /// built from the same expression. <see cref="Map"/> is its compiled form for the paths that already
    /// hold the entity (detail, and the actions that return the row they just changed).
    /// </summary>
    private static readonly Expression<Func<User, UserResponse>> Projection = user => new UserResponse(
        user.UserId,
        user.UserUnique,
        user.DisplayName,
        user.Email,
        user.EmployeeId,
        user.IsActive,
        user.IsUnlimited,
        user.MonthlyTokenQuota,
        user.ApimSubscriptionId != "",
        user.CreatedDate,
        user.LastSyncedDate);

    private static readonly Func<User, UserResponse> Map = Projection.Compile();

    /// <inheritdoc />
    public async Task<UserProfileResponse> GetMyProfileAsync(CancellationToken cancellationToken)
    {
        var user = await currentUser.TryGetUserAsync(cancellationToken);

        if (user is null)
        {
            // First login: the pipeline creates the row, this month's allocation and the APIM
            // subscription, or leaves nothing behind (plan 21).
            user = await lifecycle.ProvisionAsync(ProvisionTrigger.FirstLogin, ProvisionContext.FirstLogin(), cancellationToken);
        }
        else
        {
            if (!user.IsActive)
            {
                // Same class of answer as "no User row" — 403, not 404: the caller is known, just not
                // entitled (CONVENTIONS.md). Nothing is refreshed or resolved for a deactivated account.
                throw new UnauthorizedAccessException(
                    $"Your FoundryGate account (user {user.UserId}) is deactivated, so it has no profile, quota or key. Ask an administrator to re-activate it.");
            }

            // Only when a claim actually differs: otherwise every profile load would be a row UPDATE on
            // Users — the hottest table in the app, and the one the Entra sync writes to (#156 review).
            if (RefreshFromClaims(user))
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        // Resolves and creates the row on the first call of a new month, and returns the existing one
        // otherwise — including the one the provision pipeline just committed.
        var quota = await quotaAllocations.GetMyAllocationAsync(cancellationToken);

        return new UserProfileResponse(
            user.UserId,
            user.UserUnique,
            user.DisplayName,
            user.Email,
            user.IsActive,
            user.IsUnlimited,
            quota,
            keys.GetMasked(user),
            BuildCliConfig());
    }

    /// <inheritdoc />
    public Task<PagedResult<UserResponse>> ListAsync(UserListQuery filter, PagedRequest paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(paging);

        IQueryable<User> query = dbContext.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // Translates to LIKE '%term%'; case sensitivity follows the database collation (SQL Server's
            // default is case-insensitive), which is the behaviour an admin typing a name expects.
            var search = filter.Search.Trim();
            query = query.Where(user => user.DisplayName.Contains(search) || user.Email.Contains(search));
        }

        if (filter.IsActive is { } isActive)
        {
            query = query.Where(user => user.IsActive == isActive);
        }

        return query
            .OrderBy(user => user.DisplayName)
            .ThenBy(user => user.UserId)
            .Select(Projection)
            .ToPagedAsync(paging, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<UserDetailResponse> GetAsync(int userId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException($"User {userId} was not found.");

        var groups = await dbContext.GroupMembers.AsNoTracking()
            .Where(member => member.UserId == userId)
            .OrderBy(member => member.Group.Name)
            .ThenBy(member => member.GroupId)
            .Select(member => new UserGroupMembershipResponse(
                member.GroupId,
                member.Group.GroupUnique,
                member.Group.Name,
                member.AddedDate))
            .ToListAsync(cancellationToken);

        var allocation = await quotaAllocations.FindUserAllocationAsync(userId, cancellationToken);

        return new UserDetailResponse(Map(user), allocation, keys.GetMasked(user), groups);
    }

    /// <inheritdoc />
    public async Task<UserResponse> UpdateQuotaAsync(int userId, UpdateUserQuotaRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A monthly budget IS a tier (D-013): validate before anything is written, so the 400 names the
        // allowed values instead of the gateway silently enforcing a different number later.
        var monthlyTokenQuota = request.IsUnlimited ? null : request.MonthlyTokenQuota;
        tierMapper.EnsureValidQuota(monthlyTokenQuota, nameof(request.MonthlyTokenQuota));

        var user = await dbContext.Users.FindAsync([userId], cancellationToken)
            ?? throw new KeyNotFoundException($"User {userId} was not found.");

        var before = new { user.IsUnlimited, user.MonthlyTokenQuota };

        user.IsUnlimited = request.IsUnlimited;

        // Nulled rather than kept when unlimited: the DTO documents the number as ignored, and a stored
        // value nothing reads is a trap for the next admin who turns unlimited back off.
        user.MonthlyTokenQuota = monthlyTokenQuota;

        // Re-resolve now, in this unit of work: resolution is what moves the APIM subscription onto the
        // new tier product (IGatewayTierSync), and it does so before the save below — so a gateway that
        // refuses the move fails the request rather than leaving the database claiming a tier nobody
        // enforces. The reverse residue (gateway moved, save failed) is the accepted direction: a retry
        // is idempotent at the gateway.
        var resolution = await quotaResolution.ResolveAsync(userId, BillingPeriod.Current(timeProvider), cancellationToken);

        // Past the commit point when resolution moved the subscription: the gateway is already enforcing
        // the new tier, so the row that records it and the audit row that explains it must not be
        // abandoned because the client hung up (CONVENTIONS.md; #156 review). Every refusal happened above.
        var completionToken = resolution.TierSyncRequested ? CancellationToken.None : cancellationToken;

        try
        {
            _ = await audit.LogAsync(
                AuditActions.UserQuotaChanged,
                AuditTargetTypes.User,
                userId.ToString(CultureInfo.InvariantCulture),
                new
                {
                    before,
                    after = new { user.IsUnlimited, user.MonthlyTokenQuota },
                    resolved = new
                    {
                        resolution.Allocation.AllocatedTokens,
                        resolution.Allocation.ResolvedLevelType,
                        resolution.Allocation.TierProductId,
                    },
                    tierSyncRequested = resolution.TierSyncRequested,
                },
                completionToken);

            await dbContext.SaveChangesAsync(completionToken);
        }
        catch (Exception exception) when (resolution.TierSyncRequested)
        {
            logger.LogError(
                exception,
                "The gateway moved user {UserId}'s subscription to tier {TierProductId} but the quota change could not be saved; the database still shows the old budget. Re-apply it with PUT /users/{UserId}/quota — the move is idempotent.",
                userId,
                resolution.Allocation.TierProductId,
                userId);
            throw;
        }

        logger.LogInformation(
            "Quota for user {UserId} set to {Quota} (unlimited: {IsUnlimited}); resolved to {AllocatedTokens} on tier {TierProductId}.",
            userId,
            monthlyTokenQuota,
            request.IsUnlimited,
            resolution.Allocation.AllocatedTokens,
            resolution.Allocation.TierProductId);

        return Map(user);
    }

    /// <inheritdoc />
    public async Task<UserResponse> ActivateAsync(int userId, CancellationToken cancellationToken) =>
        Map(await lifecycle.ProvisionAsync(ProvisionTrigger.Reactivate, ProvisionContext.ForUser(userId), cancellationToken));

    /// <inheritdoc />
    public async Task<UserResponse> DeactivateAsync(int userId, CancellationToken cancellationToken)
    {
        await lifecycle.DeprovisionAsync(DeprovisionTrigger.AdminDeactivation, userId, cancellationToken);

        // FindAsync returns the instance the pipeline just mutated (same context, no second query).
        var user = await dbContext.Users.FindAsync([userId], cancellationToken)
            ?? throw new KeyNotFoundException($"User {userId} was not found.");

        return Map(user);
    }

    /// <summary>
    /// Keeps the display fields in step with the token (issue #28), so a rename in Entra shows up without
    /// waiting for the next <c>POST /users/sync</c>. Only non-empty claims overwrite — a token that omits
    /// a claim must not blank a value the directory sync filled in — and only a real difference counts,
    /// so the common case (nothing changed) is a pure read.
    /// </summary>
    /// <remarks>
    /// <c>LastSyncedDate</c> is deliberately <b>not</b> stamped here: it means "when an Entra sync last
    /// touched this row", which is what <c>UserContracts</c> documents and what an admin reads it as. A
    /// separate <c>LastLoginDate</c> is the honest column for "this account is in use" — tracked as #167.
    /// </remarks>
    /// <returns><see langword="true"/> when a field changed and the row needs saving.</returns>
    private bool RefreshFromClaims(User user)
    {
        var changed = false;

        if (currentUser.DisplayName is { } displayName && !string.IsNullOrWhiteSpace(displayName))
        {
            var clamped = displayName.Length > DisplayNameMaxLength ? displayName[..DisplayNameMaxLength] : displayName;
            if (!string.Equals(user.DisplayName, clamped, StringComparison.Ordinal))
            {
                user.DisplayName = clamped;
                changed = true;
            }
        }

        if (currentUser.Email is { } email && !string.IsNullOrWhiteSpace(email) && !string.Equals(user.Email, email, StringComparison.Ordinal))
        {
            user.Email = email;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// What a developer needs to point a CLI at this fork's gateway. Every field is sourced honestly:
    /// the origin comes from <c>Gateway:ApimGatewayUrl</c> (infra sets it — empty locally, where there is
    /// no gateway), and the two paths are the ones <c>infra/modules/ai-gateway.bicep</c> creates. The
    /// alias list is empty because the alias map lives only in bicep today; #153 exposes it to the
    /// control plane, and until then the docs' CLI setup page is the source developers use for model
    /// names.
    /// </summary>
    private GatewayConnectionInfo BuildCliConfig() =>
        new(
            GatewayBaseUrl: string.IsNullOrWhiteSpace(gateway.ApimGatewayUrl) ? string.Empty : gateway.ApimGatewayUrl.TrimEnd('/'),
            AnthropicBasePath: GatewayOptions.AnthropicBasePath,
            OpenAiBasePath: GatewayOptions.OpenAiBasePath,
            ModelAliases: []);
}
