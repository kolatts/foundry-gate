using Microsoft.Data.SqlClient;

namespace FoundryGate.Cli.Commands.Db.GrantIdentities;

/// <summary>Executes one T-SQL batch against the target database — the seam that keeps <see cref="GrantIdentitiesRunner"/> testable without SQL Server.</summary>
public interface ISqlBatchExecutor
{
    /// <summary>Runs <paramref name="sql"/> as a single batch (no result set).</summary>
    Task ExecuteAsync(string sql, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ISqlBatchExecutor"/> over <see cref="SqlConnection"/>. The connection string carries the
/// authentication (<c>Authentication=Active Directory Default</c> for Azure SQL, per CONVENTIONS.md), so
/// nothing credential-shaped lives here — the same arrangement <c>db deploy</c> relies on.
/// </summary>
public sealed class SqlClientBatchExecutor(string connectionString) : ISqlBatchExecutor
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string must not be empty.", nameof(connectionString))
        : connectionString;

    /// <inheritdoc />
    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
