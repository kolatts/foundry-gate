using FoundryGate.Data.Concurrency;

namespace FoundryGate.Tests.Predeployment.Data;

/// <summary>
/// <see cref="CommitToken"/> is two lines, but it is the two lines every service that touches ARM,
/// APIM or Graph now routes its unit of work through (CONVENTIONS.md "External side effects have a
/// commit point"), so the rule itself is worth stating once in a test rather than only in the probes
/// that exercise it end to end.
/// </summary>
public class CommitTokenTests
{
    [Fact]
    public void Once_the_external_system_has_accepted_a_change_the_callers_cancellation_no_longer_applies()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var token = CommitToken.For(reachedExternal: true, cts.Token);

        // The audit row and the save that record an accepted change must not be abandoned because the
        // client hung up — that is the whole rule.
        Assert.Equal(CancellationToken.None, token);
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void While_nothing_outside_the_database_has_happened_the_callers_token_still_governs()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var token = CommitToken.For(reachedExternal: false, cts.Token);

        // The other half, and the one that is easy to lose: an abandoned request that changed nothing
        // outside the database should stop. "We called something that might have reached the gateway"
        // is not a commit point.
        Assert.Equal(cts.Token, token);
        Assert.True(token.IsCancellationRequested);
    }
}
