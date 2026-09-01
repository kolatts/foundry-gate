using Bogus;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Requests;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Data.Seeding;

/// <summary>
/// Seeds realistic-looking demo data with Bogus: developers with the same varied-quota shape as
/// the public landing page's "Developers · September" demo panel (docs-site/src/pages/index.astro)
/// — a mix of comfortably-under-budget, near-limit, fully-consumed, unlimited, and
/// pending-increase-request users. Not reference data: never run in production, only local/dev/CI.
/// </summary>
public static class TestDataSeeder
{
    private static readonly long?[] QuotaTiers =
    [
        500_000,
        1_000_000,
        1_000_000,
        2_000_000,
        2_000_000,
        null, // unlimited tier (index 5) — paired with User.IsUnlimited = true below
        1_000_000,
        2_000_000
    ];

    /// <summary>
    /// Seeds <paramref name="developerCount"/> demo users (plus one demo group and one pending
    /// quota increase request). No-ops if any <see cref="User"/> already exists, so it is safe to
    /// call on every local/dev startup without piling up duplicate demo data.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="timeProvider">
    /// Clock used for the seeded rows' period/reset dates — CONVENTIONS.md bans naked
    /// <c>DateTimeOffset.UtcNow</c> outside <see cref="Interceptors.TimestampInterceptor"/>, and
    /// that rule doesn't get a dev-only exemption.
    /// </param>
    /// <param name="developerCount">How many demo users to create.</param>
    /// <param name="randomSeed">
    /// Seed for this call's own <see cref="Faker{T}"/> instance (via <c>UseSeed</c>), not
    /// <see cref="Randomizer.Seed"/> — that field is a process-global static, and mutating it here
    /// would make every other Bogus user in the process (including anything running concurrently,
    /// e.g. parallel test collections) implicitly reseed too.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task SeedAsync(
        AppDbContext context,
        TimeProvider timeProvider,
        int developerCount = 8,
        int randomSeed = 20260901,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (await context.Users.AnyAsync(cancellationToken))
        {
            return;
        }

        var userFaker = new Faker<User>()
            .UseSeed(randomSeed)
            .RuleFor(u => u.DisplayName, f => f.Name.FullName())
            .RuleFor(u => u.Email, (f, u) => f.Internet.Email(u.DisplayName, provider: "contoso.com").ToLowerInvariant())
            .RuleFor(u => u.EntraObjectId, _ => Guid.NewGuid().ToString())
            .RuleFor(u => u.IsActive, _ => true);

        var users = userFaker.Generate(developerCount);
        for (var i = 0; i < users.Count; i++)
        {
            var quota = QuotaTiers[i % QuotaTiers.Length];
            users[i].MonthlyTokenQuota = quota;
            users[i].IsUnlimited = quota is null;
        }

        context.Users.AddRange(users);
        await context.SaveChangesAsync(cancellationToken);

        var demoGroup = new Group
        {
            Name = "Platform Engineering",
            Description = "Demo group seeded for local/dev environments.",
            MonthlyTokenQuota = 2_000_000
        };
        context.Groups.Add(demoGroup);
        await context.SaveChangesAsync(cancellationToken);

        context.GroupMembers.AddRange(users.Take(3).Select(u => new GroupMember
        {
            GroupId = demoGroup.GroupId,
            UserId = u.UserId
        }));

        var now = timeProvider.GetUtcNow();
        var allocations = new List<QuotaAllocation>();
        for (var i = 0; i < users.Count; i++)
        {
            var user = users[i];
            var allocated = user.MonthlyTokenQuota;

            // Vary usage across the roster the way the landing page demo panel does: mostly
            // comfortably under budget, one user near their limit, one fully consumed, and a
            // couple of barely-touched/average users to round it out. Index 5 (QuotaTiers[5] is
            // null) is the unlimited user — its fraction below is unused since the ternary a few
            // lines down always routes null-allocated users to the flat 42,000 fallback instead.
            var usedFraction = i switch
            {
                0 => 0.62,
                1 => 0.38,
                2 => 0.91, // near limit
                3 => 1.00, // fully consumed
                4 => 0.55,
                5 => 0.0, // unlimited — see comment above
                6 => 0.12, // barely touched
                _ => 0.77
            };

            allocations.Add(new QuotaAllocation
            {
                UserId = user.UserId,
                PeriodYear = now.Year,
                PeriodMonth = now.Month,
                AllocatedTokens = allocated,
                TokensUsed = allocated is null ? 42_000 : (long)(allocated.Value * usedFraction),
                ResetDate = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero)
            });
        }

        context.QuotaAllocations.AddRange(allocations);

        // One pending increase request, mirroring the landing page's "Asked for more" row.
        var requester = users[5 % users.Count];
        context.QuotaIncreaseRequests.Add(new QuotaIncreaseRequest
        {
            UserId = requester.UserId,
            RequestedByUserId = requester.UserId,
            PeriodYear = now.Year,
            PeriodMonth = now.Month,
            CurrentQuota = requester.MonthlyTokenQuota,
            RequestedQuota = (requester.MonthlyTokenQuota ?? 1_000_000) * 2,
            Justification = "Running a batch evaluation this sprint that needs more headroom than the standard tier.",
            StatusType = QuotaRequestStatusType.Pending
        });

        await context.SaveChangesAsync(cancellationToken);
    }
}
