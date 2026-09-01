namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// Fake clock whose "now" can be set mid-test. Registered as the <see cref="TimeProvider"/> singleton
/// by <c>ApiTestFactory</c> (so endpoint tests control quota periods, audit timestamps, etc.) and
/// usable directly in service-level tests. Deliberately not <c>Microsoft.Extensions.TimeProvider
/// .Testing</c>'s <c>FakeTimeProvider</c> — that's another package for one method.
/// </summary>
public sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves the clock to <paramref name="value"/> (converted to UTC).</summary>
    public void SetUtcNow(DateTimeOffset value) => _now = value.ToUniversalTime();

    /// <summary>Moves the clock forward by <paramref name="delta"/>.</summary>
    public void Advance(TimeSpan delta) => _now += delta;
}
