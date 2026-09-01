using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Data.Conventions;

/// <summary>
/// Drift alarm between the EF entity model (schema source of truth per CONVENTIONS.md's "Schema
/// pipeline") and the checked-in <c>FoundryGate.Database/dbo/Tables/*.sql</c> files DacFx builds
/// into the deployed dacpac.
/// </summary>
/// <remarks>
/// <para>
/// The "real" way to keep these in sync is DacFx schema-compare (<c>foundrygate db compare</c>,
/// plans/23-database-tooling.md), but that API is Windows-only native tooling and isn't available
/// in every environment these tests run in. This is a deliberately pragmatic substitute: a
/// regex-level parser over the .sql files, not a real T-SQL parser, checked against
/// <see cref="Microsoft.EntityFrameworkCore.Metadata.IModel"/> reflection (via the SQLite in-memory
/// context <see cref="InMemoryDatabaseTest"/> already builds — table/column/index/FK *structure* in
/// that model is provider-agnostic; only physical SQL Server types like
/// <c>nvarchar</c>/<c>uniqueidentifier</c> are SQLite-incompatible and therefore out of scope here).
/// </para>
/// <para>
/// Documented gaps below are tracked in
/// <see href="https://github.com/kolatts/foundry-gate/issues/100">#100</see>, not left as inline
/// TODOs.
/// </para>
/// <para><b>Deliberate limits (documented, not accidental gaps):</b></para>
/// <list type="bullet">
/// <item>Checks table existence, the column name set, and column nullability only — NOT SQL data
/// types, lengths, or precision. A column that silently shrinks from <c>nvarchar(200)</c> to
/// <c>nvarchar(50)</c> without a matching entity change will NOT be caught.</item>
/// <item>For single-column foreign keys, checks that a same-named <c>FK_*</c> constraint exists and
/// that its <c>ON DELETE CASCADE</c> presence matches the configured <c>DeleteBehavior</c>. Does not
/// otherwise validate FK shape (principal column, etc.) or handle composite FKs (none exist in this
/// model today).</item>
/// <item>Checks that every index EF's model declares (explicit <c>[Index]</c> attributes and EF's
/// own implicit per-FK indexes) has a same-named <c>CREATE INDEX</c> statement with a matching
/// <c>UNIQUE</c> flag. Does not validate index column order/composition beyond that, and does not
/// flag an extra index present in the .sql file but absent from the model.</item>
/// <item>Parses via regex over raw file text, relying on the repo's existing SQL style (one
/// statement per <c>GO</c> batch, bracket-quoted identifiers, one table per file named after the
/// table). Hand-authored .sql that strays from that style can produce false positives/negatives
/// here instead of a clean parser error — this is a drift *alarm*, not a schema validator.</item>
/// </list>
/// </remarks>
public class SchemaParityTests : InMemoryDatabaseTest
{
    private static readonly Regex ColumnLineRegex = new(
        @"^\s*\[(?<name>[A-Za-z0-9_]+)\]\s+.+?\b(?<null>NOT NULL|NULL)\b",
        RegexOptions.Compiled);

    private static readonly Regex ForeignKeyRegex = new(
        @"ADD CONSTRAINT \[(?<name>FK_[A-Za-z0-9_]+)\] FOREIGN KEY \(\[(?<col>[A-Za-z0-9_]+)\]\) REFERENCES \[dbo\]\.\[[A-Za-z0-9_]+\] \(\[[A-Za-z0-9_]+\]\)(?<cascade> ON DELETE CASCADE)?",
        RegexOptions.Compiled);

    private static readonly Regex IndexRegex = new(
        @"CREATE (?<unique>UNIQUE )?(NONCLUSTERED|CLUSTERED) INDEX \[(?<name>IX_[A-Za-z0-9_]+)\]",
        RegexOptions.Compiled);

    [Fact]
    public void CheckedInSqlFiles_ShouldMatchEfModel()
    {
        string tablesDirectory = FindTablesDirectory();
        var violations = new List<string>();

        foreach (var entityType in Context.Model.GetEntityTypes())
        {
            string tableName = entityType.GetTableName()
                ?? throw new InvalidOperationException($"{entityType.ClrType.Name} has no table name.");

            string sqlPath = Path.Combine(tablesDirectory, $"{tableName}.sql");
            if (!File.Exists(sqlPath))
            {
                violations.Add($"{tableName}: no dbo/Tables/{tableName}.sql file found (expected at {sqlPath})");
                continue;
            }

            string sql = File.ReadAllText(sqlPath);
            CheckColumns(entityType, tableName, sql, violations);
            CheckForeignKeys(entityType, tableName, sql, violations);
            CheckIndexes(entityType, tableName, sql, violations);
        }

        Assert.True(
            violations.Count == 0,
            $"Found {violations.Count} schema drift violation(s) between the EF model and " +
            $"FoundryGate.Database/dbo/Tables/*.sql:\n  - {string.Join("\n  - ", violations)}");
    }

    private static void CheckColumns(
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entityType,
        string tableName,
        string sql,
        List<string> violations)
    {
        var sqlColumns = ParseColumns(sql);

        foreach (var property in entityType.GetProperties())
        {
            string columnName = property.GetColumnName();
            if (!sqlColumns.TryGetValue(columnName, out bool notNullInSql))
            {
                violations.Add($"{tableName}.{columnName}: column missing from {tableName}.sql");
                continue;
            }

            bool notNullInModel = !property.IsNullable;
            if (notNullInModel != notNullInSql)
            {
                violations.Add(
                    $"{tableName}.{columnName}: model says {(notNullInModel ? "NOT NULL" : "NULL")} " +
                    $"but {tableName}.sql says {(notNullInSql ? "NOT NULL" : "NULL")}");
            }
        }

        var modelColumnNames = entityType.GetProperties().Select(p => p.GetColumnName()).ToHashSet();
        foreach (string sqlColumnName in sqlColumns.Keys.Where(c => !modelColumnNames.Contains(c)))
        {
            violations.Add($"{tableName}.sql has column [{sqlColumnName}] with no matching EF model property");
        }
    }

    private static void CheckForeignKeys(
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entityType,
        string tableName,
        string sql,
        List<string> violations)
    {
        var sqlForeignKeys = ParseForeignKeys(sql);

        foreach (var fk in entityType.GetForeignKeys())
        {
            if (fk.Properties.Count != 1)
            {
                // No composite FKs exist in this model today; skip defensively rather than guess a format.
                continue;
            }

            string? fkName = fk.GetConstraintName();
            if (fkName is null || !sqlForeignKeys.TryGetValue(fkName, out bool hasCascadeInSql))
            {
                violations.Add($"{tableName}: foreign key '{fkName ?? "(unnamed)"}' missing from {tableName}.sql");
                continue;
            }

            bool expectCascade = fk.DeleteBehavior == DeleteBehavior.Cascade;
            if (expectCascade != hasCascadeInSql)
            {
                violations.Add(
                    $"{tableName}: FK {fkName} DeleteBehavior is {fk.DeleteBehavior} but {tableName}.sql " +
                    $"{(hasCascadeInSql ? "has" : "is missing")} ON DELETE CASCADE");
            }
        }
    }

    private static void CheckIndexes(
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entityType,
        string tableName,
        string sql,
        List<string> violations)
    {
        var sqlIndexes = ParseIndexes(sql);

        foreach (var index in entityType.GetIndexes())
        {
            string? indexName = index.GetDatabaseName();
            if (indexName is null || !sqlIndexes.TryGetValue(indexName, out bool isUniqueInSql))
            {
                violations.Add($"{tableName}: index '{indexName ?? "(unnamed)"}' missing from {tableName}.sql");
                continue;
            }

            if (index.IsUnique != isUniqueInSql)
            {
                violations.Add(
                    $"{tableName}: index {indexName} IsUnique is {index.IsUnique} in the model but " +
                    $"{isUniqueInSql} in {tableName}.sql");
            }
        }
    }

    /// <summary>Column name -> whether the .sql file marks it NOT NULL (false = nullable).</summary>
    private static Dictionary<string, bool> ParseColumns(string sql)
    {
        var columns = new Dictionary<string, bool>(StringComparer.Ordinal);

        // Table body is everything between "CREATE TABLE [dbo].[...] (" and the matching ");" —
        // column type parens (e.g. "IDENTITY (1, 1)") never appear immediately before a semicolon
        // in this repo's SQL style, so a simple first-match works.
        var tableMatch = Regex.Match(sql, @"CREATE TABLE \[dbo\]\.\[[A-Za-z0-9_]+\]\s*\((?<body>.*?)\)\s*;", RegexOptions.Singleline);
        if (!tableMatch.Success)
        {
            return columns;
        }

        foreach (string line in tableMatch.Groups["body"].Value.Split('\n'))
        {
            Match match = ColumnLineRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            columns[match.Groups["name"].Value] = match.Groups["null"].Value == "NOT NULL";
        }

        return columns;
    }

    /// <summary>FK constraint name -> whether it carries ON DELETE CASCADE.</summary>
    private static Dictionary<string, bool> ParseForeignKeys(string sql)
    {
        var foreignKeys = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (Match match in ForeignKeyRegex.Matches(sql))
        {
            foreignKeys[match.Groups["name"].Value] = match.Groups["cascade"].Success;
        }

        return foreignKeys;
    }

    /// <summary>Index name -> whether it is declared UNIQUE.</summary>
    private static Dictionary<string, bool> ParseIndexes(string sql)
    {
        var indexes = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (Match match in IndexRegex.Matches(sql))
        {
            indexes[match.Groups["name"].Value] = match.Groups["unique"].Success;
        }

        return indexes;
    }

    /// <summary>Walks up from the test assembly's output directory to find the repo root (marked by
    /// <c>FoundryGate.sln</c>), then returns <c>src/FoundryGate.Database/dbo/Tables</c> under it.</summary>
    private static string FindTablesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FoundryGate.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"Could not find FoundryGate.sln by walking up from {AppContext.BaseDirectory}.");
        }

        return Path.Combine(directory.FullName, "src", "FoundryGate.Database", "dbo", "Tables");
    }
}
