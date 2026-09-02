namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// A <see cref="TimeProvider"/> whose <see cref="LocalTimeZone"/> is whatever the test says, so a
/// component that converts wall-clock input to an instant can be checked from somewhere other than
/// the build agent's own zone. Everything else defers to <see cref="TimeProvider.System"/>.
/// </summary>
/// <remarks>
/// A fixed offset rather than a named zone: CI runners and developer machines do not agree on which
/// IANA/Windows zone ids are installed, and the behaviour under test is "apply the reader's offset",
/// which an offset expresses exactly.
/// </remarks>
public sealed class FixedZoneTimeProvider(TimeSpan offset) : TimeProvider
{
    /// <summary>A zone permanently at the constructed offset — no daylight saving, no history.</summary>
    public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone(
        $"FoundryGate.Test.UTC{offset:hh\\:mm}",
        offset,
        $"UTC{offset:hh\\:mm}",
        $"UTC{offset:hh\\:mm}");

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow();
}
