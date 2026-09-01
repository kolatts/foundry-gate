using FoundryGate.Data.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FoundryGate.Data.Interceptors;

/// <summary>
/// Sets <see cref="ICreatedDate.CreatedDate"/> and <see cref="IModifiedDate.ModifiedDate"/> on
/// every save, using an injected <see cref="TimeProvider"/> so tests can control "now" instead of
/// entities/services reading <c>DateTimeOffset.UtcNow</c> directly.
/// </summary>
public sealed class TimestampInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        ApplyTimestamps(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        ApplyTimestamps(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void ApplyTimestamps(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();

        foreach (EntityEntry<ICreatedDate> entry in context.ChangeTracker.Entries<ICreatedDate>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedDate = now;
            }
        }

        foreach (EntityEntry<IModifiedDate> entry in context.ChangeTracker.Entries<IModifiedDate>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.ModifiedDate = now;
            }
        }
    }
}
