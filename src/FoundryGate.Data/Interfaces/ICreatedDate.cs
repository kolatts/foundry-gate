namespace FoundryGate.Data.Interfaces;

/// <summary>
/// Opt-in interface for entities whose <c>CreatedDate</c> is set automatically by
/// <see cref="Interceptors.TimestampInterceptor"/> on insert. Never set this
/// property manually — inline <c>DateTimeOffset.UtcNow</c> is banned outside the interceptor.
/// </summary>
public interface ICreatedDate
{
    /// <summary>When the row was first inserted, in UTC.</summary>
    DateTimeOffset CreatedDate { get; set; }
}
