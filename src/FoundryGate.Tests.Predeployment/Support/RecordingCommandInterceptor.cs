using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// Records the SQL text of every command an <c>AppDbContext</c> executes, so a test can assert on
/// query <em>shape and count</em> — "this loop reads the whole table once for the run, not once per
/// iteration" is a behavioural claim that no amount of asserting on the returned rows can prove.
/// Installed by <see cref="Data.InMemoryDatabaseTest"/> on every service-level test context;
/// recording a few dozen strings costs nothing and nothing reads the list unless a test asks.
/// </summary>
public sealed class RecordingCommandInterceptor : DbCommandInterceptor
{
    private readonly List<string> _commands = [];

    /// <summary>Every statement executed against this context, in order.</summary>
    public IReadOnlyList<string> Commands => _commands;

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        ArgumentNullException.ThrowIfNull(command);
        _commands.Add(command.CommandText);

        return base.ReaderExecuting(command, eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _commands.Add(command.CommandText);

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
