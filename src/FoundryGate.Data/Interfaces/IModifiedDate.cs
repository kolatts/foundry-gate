namespace FoundryGate.Data.Interfaces;

/// <summary>
/// Opt-in interface for entities whose <c>ModifiedDate</c> is set automatically by
/// <see cref="Interceptors.TimestampInterceptor"/> on insert and update. Never
/// set this property manually — inline <c>DateTimeOffset.UtcNow</c> is banned outside the
/// interceptor.
/// </summary>
public interface IModifiedDate
{
    /// <summary>When the row was last inserted or updated, in UTC.</summary>
    DateTimeOffset ModifiedDate { get; set; }
}
