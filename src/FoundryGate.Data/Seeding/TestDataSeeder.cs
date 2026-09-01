using Bogus;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Enums;
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
        null, // unlimited tier — paired with User.IsUnlimited = true below
        1_000_000,
        2_000_000
    ];

    /// <summary>
    /// Seeds <paramref name="developerCount"/> demo users (plus one demo group and one pending
    /// quota increase request). No-ops if any <see cref="User"/> already exists, so it is safe to
    /// call on every local/dev startup without piling up duplicate demo data.
    /// </summary>
    public static async Task SeedAsync(
        AppDbContext context,
        int developerCount = 8,
        int randomSeed = 20260901,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (await context.Users.AnyAsync(cancellationToken))
        {
            return;
        }

        Randomizer.Seed = new Random(randomSeed);

        var userFaker = new Faker<User>()
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

        var now = DateTimeOffset.UtcNow;
        var allocations = new List<QuotaAllocation>();
        for (var i = 0; i < users.Count; i++)
        {
            var user = users[i];
            var allocated = user.MonthlyTokenQuota;

            // Match the landing page demo panel's usage story where a tier repeats: comfortably
            // under, comfortably under, near-limit (91%), fully consumed (100%), unlimited,
            // comfortably under, barely touched, comfortably under.
            var usedFraction = i switch
            {
                0 => 0.62,
                1 => 0.38,
                2 => 0.91,
                3 => 1.00,
                4 => 0.0, // unlimited — usage tracked but not meaningful as a fraction
                5 => 0.64,
                6 => 0.12,
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
