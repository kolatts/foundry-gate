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

    /// <summary>
    /// When set, any statement whose SQL satisfies this throws instead of executing — the way a
    /// dropped connection or a constraint violation arrives at a service. Defaults to failing nothing.
    /// Lets a test break <c>SaveChangesAsync</c> at a chosen moment, which is the only way to reach the
    /// "the external system already accepted the change and the write did not land" branch.
    /// </summary>
    public Func<string, bool> FailWhen { get; set; } = _ => false;

    /// <summary>The exception thrown for a matching statement.</summary>
    public Exception Failure { get; set; } = new InvalidOperationException("The database connection dropped.");

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Record(command);

        return base.ReaderExecuting(command, eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Record(command);

        return base.NonQueryExecuting(command, eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);

        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Record(DbCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _commands.Add(command.CommandText);

        if (FailWhen(command.CommandText))
        {
            throw Failure;
        }
    }
}
