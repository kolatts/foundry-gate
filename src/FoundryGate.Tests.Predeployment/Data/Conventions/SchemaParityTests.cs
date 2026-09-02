using System.Text.RegularExpressions;
using FoundryGate.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

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
/// regex-level parser over the .sql files, not a real T-SQL parser, checked against the relational
/// metadata of an <see cref="AppDbContext"/> model built with the <b>SQL Server</b> provider. No
/// connection is ever opened — the design-time <see cref="IModel"/> is computed purely from the
/// entity classes plus the provider's type mappings, which is exactly what makes physical store types
/// (<c>nvarchar(200)</c>, <c>uniqueidentifier</c>, <c>IDENTITY</c>) available here without a
/// database and without the SQLite in-memory harness the other convention tests use.
/// </para>
/// <para><b>What is checked (per table, aggregated into one bullet-list assertion):</b></para>
/// <list type="bullet">
/// <item>A <c>dbo/Tables/{TableName}.sql</c> file exists for every entity, and every .sql file in
/// that folder has a matching entity (no orphaned table scripts).</item>
/// <item>Column set (both directions), <c>NULL</c>/<c>NOT NULL</c>, and the SQL store type
/// including length/precision/scale — <c>nvarchar(200)</c> vs <c>nvarchar(50)</c>,
/// <c>nvarchar(max)</c>, <c>decimal(p,s)</c>, <c>bigint</c> vs <c>int</c>, etc. Comparison is
/// case- and whitespace-insensitive, and SQL Server's implicit default precision is normalised
/// (<c>DATETIMEOFFSET (7)</c> in the script equals EF's bare <c>datetimeoffset</c>). A column
/// line that does not parse is reported as unparseable (quoting the line), never as
/// "missing".</item>
/// <item><c>DEFAULT</c> constraint <i>presence</i> per column matches the model
/// (<c>HasDefaultValue</c>/<c>HasDefaultValueSql</c>); both the DacFx/SSDT placement
/// (<c>CONSTRAINT [DF_x] DEFAULT ((1)) NOT NULL</c>) and EF's (<c>NOT NULL DEFAULT (...)</c>) are
/// accepted. The default <i>expression</i> is not compared.</item>
/// <item><c>IDENTITY</c> presence per column matches the model's SQL Server value-generation
/// strategy (int <c>{Entity}Id</c> PKs are identity; composite/natural keys are not).</item>
/// <item>Primary key constraint name, column composition and order, and clustering — declared
/// either as <c>ALTER TABLE ... ADD CONSTRAINT [PK_*]</c> (this repo's style) or inline inside
/// <c>CREATE TABLE</c> (what DacFx/SSDT scripting emits).</item>
/// <item>Every foreign key (single- or multi-column) by constraint name: dependent columns,
/// principal table and columns, and the <c>ON DELETE</c> action implied by its configured
/// <see cref="DeleteBehavior"/>. Also flags FK constraints in the .sql file that the model does not
/// declare.</item>
/// <item>Every index EF's model declares (explicit <c>[Index]</c> attributes and EF's own implicit
/// per-FK indexes) by database name: <c>UNIQUE</c> flag, <c>CLUSTERED</c>/<c>NONCLUSTERED</c>,
/// column composition, column order, sort direction, and the <c>WHERE</c> filter of a filtered index
/// (<c>HasFilter</c>) — compared as normalised <i>text</i>, not semantically, so the model's filter
/// string and the .sql one must be written the same way modulo whitespace and outer parentheses.
/// Also flags indexes in the .sql file that the model does not declare.</item>
/// </list>
/// <para><b>Deliberate remaining limits (documented, not accidental gaps):</b></para>
/// <list type="bullet">
/// <item>Parses via regex over raw file text, relying on the repo's existing SQL style (one
/// statement per <c>GO</c> batch, bracket-quoted identifiers, one table per file named after the
/// table, one column per line). Hand-authored .sql that strays from that style can produce false
/// positives/negatives here instead of a clean parser error — this is a drift <i>alarm</i>, not a
/// schema validator.</item>
/// <item>Does not check schema features the model does not use today: default-constraint
/// <i>expressions</i> (only presence is compared), check constraints, computed columns,
/// collations, <c>INCLUDE</c> columns, or fill factor. Adding any of those to an entity means
/// extending this test in the same PR — as the filtered index on <c>Groups.EntraGroupId</c> did.</item>
/// <item>Compares the model to the checked-in scripts only, never to a live database. DacFx
/// schema-compare against a deployed database remains the authoritative (Windows-only) tool if
/// that is ever needed.</item>
/// </list>
/// </remarks>
public class SchemaParityTests
{
    /// <summary>
    /// A parenthesised expression with arbitrarily nested parens (.NET balancing groups), e.g. the
    /// <c>((1))</c> or <c>(getutcdate())</c> DacFx emits for default constraints.
    /// </summary>
    private const string BalancedParens = @"\((?>[^()]+|\((?<depth>)|\)(?<-depth>))*(?(depth)(?!))\)";

    /// <summary>Optional named <c>CONSTRAINT [DF_x]</c> prefix, then <c>DEFAULT (expr)</c>.</summary>
    private const string DefaultClause = @"(?:CONSTRAINT \[[A-Za-z0-9_]+\]\s+)?DEFAULT\s*" + BalancedParens;

    /// <summary>
    /// One column definition line inside the <c>CREATE TABLE (...)</c> body: name, store type
    /// (with its optional parenthesised length/precision), optional <c>IDENTITY (...)</c>, an
    /// optional default constraint either before the nullability keyword (DacFx/SSDT placement) or
    /// after it (EF's own DDL placement), and the nullability keyword itself.
    /// </summary>
    private static readonly Regex ColumnLineRegex = new(
        @"^\s*\[(?<name>[A-Za-z0-9_]+)\]\s+(?<type>[A-Za-z0-9_]+(?:\s*\([^)]*\))?)" +
        @"(?:\s+(?<identity>IDENTITY(?:\s*\([^)]*\))?))?" +
        @"(?:\s+(?<default>" + DefaultClause + @"))?" +
        @"\s+(?<null>NOT NULL|NULL)\b" +
        @"(?:\s+(?<default>" + DefaultClause + @"))?",
        RegexOptions.Compiled);

    /// <summary>
    /// Anything that starts like a column definition. Lines matching this but not
    /// <see cref="ColumnLineRegex"/> are reported as unparseable rather than silently skipped
    /// (which would otherwise surface as a misleading "column missing").
    /// </summary>
    private static readonly Regex ColumnLineStartRegex = new(
        @"^\s*\[(?<name>[A-Za-z0-9_]+)\]",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches both this repo's <c>ALTER TABLE ... ADD CONSTRAINT [PK_*]</c> style and an inline
    /// <c>CONSTRAINT [PK_*] PRIMARY KEY</c> table constraint inside <c>CREATE TABLE</c>.
    /// </summary>
    private static readonly Regex PrimaryKeyRegex = new(
        @"(?:ADD\s+)?CONSTRAINT \[(?<name>PK_[A-Za-z0-9_]+)\] PRIMARY KEY (?<clustered>CLUSTERED|NONCLUSTERED)\s*\((?<cols>[^)]*)\)",
        RegexOptions.Compiled);

    private static readonly Regex ForeignKeyRegex = new(
        @"ADD CONSTRAINT \[(?<name>FK_[A-Za-z0-9_]+)\] FOREIGN KEY\s*\((?<cols>[^)]*)\) REFERENCES \[dbo\]\.\[(?<ptable>[A-Za-z0-9_]+)\]\s*\((?<pcols>[^)]*)\)(?:\s+ON DELETE (?<ondelete>CASCADE|SET NULL|SET DEFAULT|NO ACTION))?",
        RegexOptions.Compiled);

    private static readonly Regex IndexRegex = new(
        @"CREATE (?<unique>UNIQUE )?(?<clustered>NONCLUSTERED|CLUSTERED) INDEX \[(?<name>IX_[A-Za-z0-9_]+)\]\s+ON \[dbo\]\.\[[A-Za-z0-9_]+\]\s*\((?<cols>[^)]*)\)(?:\s*WHERE\s*(?<filter>[^;]*?))?\s*;",
        RegexOptions.Compiled);

    /// <summary>One entry in a bracket-quoted column list, e.g. <c>[UserId] ASC</c>.</summary>
    private static readonly Regex ColumnListEntryRegex = new(
        @"\[(?<col>[A-Za-z0-9_]+)\](?:\s+(?<dir>ASC|DESC))?",
        RegexOptions.Compiled);

    private static readonly Regex TableBodyRegex = new(
        @"CREATE TABLE \[dbo\]\.\[[A-Za-z0-9_]+\]\s*\((?<body>.*?)\)\s*;",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    [Fact]
    public void CheckedInSqlFiles_ShouldMatchEfModel()
    {
        string tablesDirectory = FindTablesDirectory();
        IModel model = BuildSqlServerModel();
        var violations = new List<string>();
        var modelTableNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entityType in model.GetEntityTypes())
        {
            string tableName = entityType.GetTableName()
                ?? throw new InvalidOperationException($"{entityType.ClrType.Name} has no table name.");
            modelTableNames.Add(tableName);

            string sqlPath = Path.Combine(tablesDirectory, $"{tableName}.sql");
            if (!File.Exists(sqlPath))
            {
                violations.Add($"{tableName}: no dbo/Tables/{tableName}.sql file found (expected at {sqlPath})");
                continue;
            }

            string sql = File.ReadAllText(sqlPath);
            CheckColumns(entityType, tableName, sql, violations);
            CheckPrimaryKey(entityType, tableName, sql, violations);
            CheckForeignKeys(entityType, tableName, sql, violations);
            CheckIndexes(entityType, tableName, sql, violations);
        }

        foreach (string orphanFile in Directory.EnumerateFiles(tablesDirectory, "*.sql")
            .Select(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty)
            .Where(name => name.Length > 0 && !modelTableNames.Contains(name))
            .Order(StringComparer.Ordinal))
        {
            violations.Add($"{orphanFile}.sql exists in dbo/Tables but no EF entity maps to table [{orphanFile}]");
        }

        Assert.True(
            violations.Count == 0,
            $"Found {violations.Count} schema drift violation(s) between the EF model and " +
            $"FoundryGate.Database/dbo/Tables/*.sql:\n  - {string.Join("\n  - ", violations)}");
    }

    /// <summary>
    /// Builds the SQL Server-provider model without touching a database: model construction only
    /// runs entity discovery and the provider's type mapping, so the connection string is never
    /// used. The host name is deliberately unresolvable to make that unmissable if it ever changes.
    /// The <i>design-time</i> model is used (not <see cref="DbContext.Model"/>) because the runtime
    /// model is read-optimised and strips the provider annotations this test reads, e.g.
    /// <see cref="SqlServerKeyExtensions.IsClustered(IReadOnlyKey)"/>.
    /// </summary>
    private static IModel BuildSqlServerModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=schema-parity-test.invalid;Database=FoundryGate;Encrypt=False")
            .Options;

        using var context = new AppDbContext(options);
        return context.GetService<IDesignTimeModel>().Model;
    }

    private static void CheckColumns(
        IEntityType entityType,
        string tableName,
        string sql,
        List<string> violations)
    {
        (var sqlColumns, var unparsedLines) = ParseColumns(sql);

        foreach ((string unparsedName, string unparsedLine) in unparsedLines)
        {
            violations.Add(
                $"{tableName}.sql: column line for [{unparsedName}] could not be parsed by the parity regex " +
                $"(expected '[Name] TYPE [IDENTITY (..)] [CONSTRAINT [DF_x] DEFAULT (..)] NULL|NOT NULL'): '{unparsedLine}'");
        }

        foreach (var property in entityType.GetProperties())
        {
            string columnName = property.GetColumnName();
            if (!sqlColumns.TryGetValue(columnName, out SqlColumn? sqlColumn))
            {
                if (!unparsedLines.ContainsKey(columnName))
                {
                    // Already reported above as unparseable when the line exists but didn't parse.
                    violations.Add($"{tableName}.{columnName}: column missing from {tableName}.sql");
                }

                continue;
            }

            bool notNullInModel = !property.IsNullable;
            if (notNullInModel != sqlColumn.NotNull)
            {
                violations.Add(
                    $"{tableName}.{columnName}: model says {(notNullInModel ? "NOT NULL" : "NULL")} " +
                    $"but {tableName}.sql says {(sqlColumn.NotNull ? "NOT NULL" : "NULL")}");
            }

            string modelType = NormalizeSqlType(property.GetColumnType());
            string sqlType = NormalizeSqlType(sqlColumn.Type);
            if (!string.Equals(modelType, sqlType, StringComparison.Ordinal))
            {
                violations.Add(
                    $"{tableName}.{columnName}: model type is {modelType} but {tableName}.sql declares {sqlType}");
            }

            // Fully qualified: the test project also references the SQLite provider, whose same-named
            // extension would otherwise make this call ambiguous.
            bool identityInModel = SqlServerPropertyExtensions.GetValueGenerationStrategy(property)
                == SqlServerValueGenerationStrategy.IdentityColumn;
            if (identityInModel != sqlColumn.IsIdentity)
            {
                violations.Add(
                    $"{tableName}.{columnName}: model {(identityInModel ? "is" : "is not")} an IDENTITY column " +
                    $"but {tableName}.sql {(sqlColumn.IsIdentity ? "declares" : "does not declare")} IDENTITY");
            }

            // Annotation check, not GetDefaultValue(): that API falls back to the CLR default for every
            // non-nullable value type, which would flag every int/bool/Guid column as "has default".
            bool defaultInModel = property.FindAnnotation(RelationalAnnotationNames.DefaultValue) is not null
                || property.GetDefaultValueSql() is not null;
            if (defaultInModel != sqlColumn.HasDefault)
            {
                violations.Add(
                    $"{tableName}.{columnName}: model {(defaultInModel ? "has" : "has no")} default value " +
                    $"but {tableName}.sql {(sqlColumn.HasDefault ? "declares" : "does not declare")} a DEFAULT constraint");
            }
        }

        var modelColumnNames = entityType.GetProperties().Select(p => p.GetColumnName()).ToHashSet(StringComparer.Ordinal);
        foreach (string sqlColumnName in sqlColumns.Keys.Where(c => !modelColumnNames.Contains(c)))
        {
            violations.Add($"{tableName}.sql has column [{sqlColumnName}] with no matching EF model property");
        }
    }

    private static void CheckPrimaryKey(
        IEntityType entityType,
        string tableName,
        string sql,
        List<string> violations)
    {
        IKey? key = entityType.FindPrimaryKey();
        if (key is null)
        {
            // Keyless entities have no PK to compare; none exist in this model today.
            return;
        }

        string keyName = key.GetName() ?? "(unnamed)";
        Match match = PrimaryKeyRegex.Match(sql);
        if (!match.Success)
        {
            violations.Add(
                $"{tableName}: no PRIMARY KEY constraint found in {tableName}.sql (neither " +
                "'ALTER TABLE ... ADD CONSTRAINT [PK_*] PRIMARY KEY' nor an inline 'CONSTRAINT [PK_*] PRIMARY KEY' in CREATE TABLE)");
            return;
        }

        string sqlKeyName = match.Groups["name"].Value;
        if (!string.Equals(keyName, sqlKeyName, StringComparison.Ordinal))
        {
            violations.Add($"{tableName}: model primary key is named {keyName} but {tableName}.sql declares {sqlKeyName}");
        }

        var modelColumns = key.Properties.Select(p => p.GetColumnName()).ToList();
        var sqlColumns = ParseColumnList(match.Groups["cols"].Value).Select(c => c.Name).ToList();
        if (!modelColumns.SequenceEqual(sqlColumns, StringComparer.Ordinal))
        {
            violations.Add(
                $"{tableName}: primary key {sqlKeyName} columns are {FormatColumns(modelColumns)} in the model but " +
                $"{FormatColumns(sqlColumns)} in {tableName}.sql");
        }

        // SQL Server's default for a PK is clustered; EF leaves IsClustered() null unless overridden.
        bool clusteredInModel = key.IsClustered() ?? true;
        bool clusteredInSql = match.Groups["clustered"].Value == "CLUSTERED";
        if (clusteredInModel != clusteredInSql)
        {
            violations.Add(
                $"{tableName}: primary key {sqlKeyName} is {(clusteredInModel ? "CLUSTERED" : "NONCLUSTERED")} in the model but " +
                $"{(clusteredInSql ? "CLUSTERED" : "NONCLUSTERED")} in {tableName}.sql");
        }
    }

    private static void CheckForeignKeys(
        IEntityType entityType,
        string tableName,
        string sql,
        List<string> violations)
    {
        var sqlForeignKeys = ParseForeignKeys(sql);
        var modelForeignKeyNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fk in entityType.GetForeignKeys())
        {
            string? fkName = fk.GetConstraintName();
            if (fkName is null || !sqlForeignKeys.TryGetValue(fkName, out SqlForeignKey? sqlFk))
            {
                violations.Add($"{tableName}: foreign key '{fkName ?? "(unnamed)"}' missing from {tableName}.sql");
                continue;
            }

            modelForeignKeyNames.Add(fkName);

            var modelColumns = fk.Properties.Select(p => p.GetColumnName()).ToList();
            if (!modelColumns.SequenceEqual(sqlFk.Columns, StringComparer.Ordinal))
            {
                violations.Add(
                    $"{tableName}: FK {fkName} columns are {FormatColumns(modelColumns)} in the model but " +
                    $"{FormatColumns(sqlFk.Columns)} in {tableName}.sql");
            }

            string principalTable = fk.PrincipalEntityType.GetTableName() ?? "(no table)";
            var principalColumns = fk.PrincipalKey.Properties.Select(p => p.GetColumnName()).ToList();
            if (!string.Equals(principalTable, sqlFk.PrincipalTable, StringComparison.Ordinal)
                || !principalColumns.SequenceEqual(sqlFk.PrincipalColumns, StringComparer.Ordinal))
            {
                violations.Add(
                    $"{tableName}: FK {fkName} references [{principalTable}] {FormatColumns(principalColumns)} in the model but " +
                    $"[{sqlFk.PrincipalTable}] {FormatColumns(sqlFk.PrincipalColumns)} in {tableName}.sql");
            }

            string expectedOnDelete = ToSqlOnDeleteAction(fk.DeleteBehavior);
            if (!string.Equals(expectedOnDelete, sqlFk.OnDelete, StringComparison.Ordinal))
            {
                violations.Add(
                    $"{tableName}: FK {fkName} DeleteBehavior is {fk.DeleteBehavior} (ON DELETE {expectedOnDelete}) but " +
                    $"{tableName}.sql declares ON DELETE {sqlFk.OnDelete}");
            }
        }

        foreach (string extraFk in sqlForeignKeys.Keys.Where(name => !modelForeignKeyNames.Contains(name)))
        {
            violations.Add($"{tableName}.sql declares foreign key {extraFk} that the EF model does not have");
        }
    }

    private static void CheckIndexes(
        IEntityType entityType,
        string tableName,
        string sql,
        List<string> violations)
    {
        var sqlIndexes = ParseIndexes(sql);
        var modelIndexNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var index in entityType.GetIndexes())
        {
            string? indexName = index.GetDatabaseName();
            if (indexName is null || !sqlIndexes.TryGetValue(indexName, out SqlIndex? sqlIndex))
            {
                violations.Add($"{tableName}: index '{indexName ?? "(unnamed)"}' missing from {tableName}.sql");
                continue;
            }

            modelIndexNames.Add(indexName);

            if (index.IsUnique != sqlIndex.IsUnique)
            {
                violations.Add(
                    $"{tableName}: index {indexName} IsUnique is {index.IsUnique} in the model but " +
                    $"{sqlIndex.IsUnique} in {tableName}.sql");
            }

            // SQL Server's default for a secondary index is nonclustered; EF leaves IsClustered() null unless overridden.
            bool clusteredInModel = index.IsClustered() ?? false;
            if (clusteredInModel != sqlIndex.IsClustered)
            {
                violations.Add(
                    $"{tableName}: index {indexName} is {(clusteredInModel ? "CLUSTERED" : "NONCLUSTERED")} in the model but " +
                    $"{(sqlIndex.IsClustered ? "CLUSTERED" : "NONCLUSTERED")} in {tableName}.sql");
            }

            var modelColumns = index.Properties
                .Select((p, i) => new SqlIndexColumn(p.GetColumnName(), IsDescending(index, i)))
                .ToList();
            if (!modelColumns.SequenceEqual(sqlIndex.Columns))
            {
                violations.Add(
                    $"{tableName}: index {indexName} columns are {FormatColumns(modelColumns)} in the model but " +
                    $"{FormatColumns(sqlIndex.Columns)} in {tableName}.sql");
            }

            string modelFilter = NormalizeFilter(index.GetFilter());
            string sqlFilter = NormalizeFilter(sqlIndex.Filter);
            if (!string.Equals(modelFilter, sqlFilter, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(
                    $"{tableName}: index {indexName} filter is {Describe(modelFilter)} in the model but " +
                    $"{Describe(sqlFilter)} in {tableName}.sql");
            }
        }

        foreach (string extraIndex in sqlIndexes.Keys.Where(name => !modelIndexNames.Contains(name)))
        {
            violations.Add($"{tableName}.sql declares index {extraIndex} that the EF model does not have");
        }
    }

    /// <summary>
    /// EF encodes sort direction as: <see langword="null"/> = all ascending, empty list = all
    /// descending, otherwise one flag per column.
    /// </summary>
    private static bool IsDescending(IIndex index, int columnOrdinal) =>
        index.IsDescending switch
        {
            null => false,
            { Count: 0 } => true,
            var flags => flags[columnOrdinal],
        };

    private static string ToSqlOnDeleteAction(DeleteBehavior behavior) =>
        behavior switch
        {
            DeleteBehavior.Cascade => "CASCADE",
            DeleteBehavior.SetNull => "SET NULL",
            // ClientSetNull/ClientCascade/ClientNoAction/NoAction/Restrict all map to the database
            // doing nothing: SQL Server's default (and the script omitting ON DELETE) is NO ACTION.
            _ => "NO ACTION",
        };

    /// <summary>
    /// Lower-cases, strips whitespace, and folds SQL Server's implicit default precision so
    /// EF's <c>datetimeoffset</c> equals the script's <c>DATETIMEOFFSET (7)</c>. Length/precision
    /// that is NOT a server default (<c>nvarchar(200)</c>, <c>decimal(18,2)</c>) is preserved so
    /// it is compared.
    /// </summary>
    private static string NormalizeSqlType(string type)
    {
        string normalized = WhitespaceRegex.Replace(type, string.Empty).ToLowerInvariant();
        return normalized switch
        {
            "datetimeoffset(7)" => "datetimeoffset",
            "datetime2(7)" => "datetime2",
            "time(7)" => "time",
            _ => normalized,
        };
    }

    private static string FormatColumns(IEnumerable<string> columns) =>
        $"({string.Join(", ", columns.Select(c => $"[{c}]"))})";

    private static string FormatColumns(IEnumerable<SqlIndexColumn> columns) =>
        $"({string.Join(", ", columns.Select(c => $"[{c.Name}] {(c.IsDescending ? "DESC" : "ASC")}"))})";

    /// <summary>
    /// Column name -> parsed definition from the CREATE TABLE body, plus column name -> raw line
    /// for any line that starts like a column definition but did not parse.
    /// </summary>
    private static (Dictionary<string, SqlColumn> Columns, Dictionary<string, string> UnparsedLines) ParseColumns(string sql)
    {
        var columns = new Dictionary<string, SqlColumn>(StringComparer.Ordinal);
        var unparsedLines = new Dictionary<string, string>(StringComparer.Ordinal);

        // Table body is everything between "CREATE TABLE [dbo].[...] (" and the matching ");" —
        // column type parens (e.g. "IDENTITY (1, 1)") never appear immediately before a semicolon
        // in this repo's SQL style, so a simple first-match works.
        Match tableMatch = TableBodyRegex.Match(sql);
        if (!tableMatch.Success)
        {
            return (columns, unparsedLines);
        }

        foreach (string line in tableMatch.Groups["body"].Value.Split('\n'))
        {
            Match match = ColumnLineRegex.Match(line);
            if (!match.Success)
            {
                Match start = ColumnLineStartRegex.Match(line);
                if (start.Success)
                {
                    unparsedLines[start.Groups["name"].Value] = line.Trim();
                }

                continue;
            }

            columns[match.Groups["name"].Value] = new SqlColumn(
                match.Groups["type"].Value,
                match.Groups["null"].Value == "NOT NULL",
                match.Groups["identity"].Success,
                match.Groups["default"].Success);
        }

        return (columns, unparsedLines);
    }

    /// <summary>FK constraint name -> parsed shape.</summary>
    private static Dictionary<string, SqlForeignKey> ParseForeignKeys(string sql)
    {
        var foreignKeys = new Dictionary<string, SqlForeignKey>(StringComparer.Ordinal);

        foreach (Match match in ForeignKeyRegex.Matches(sql))
        {
            foreignKeys[match.Groups["name"].Value] = new SqlForeignKey(
                ParseColumnList(match.Groups["cols"].Value).Select(c => c.Name).ToList(),
                match.Groups["ptable"].Value,
                ParseColumnList(match.Groups["pcols"].Value).Select(c => c.Name).ToList(),
                match.Groups["ondelete"].Success ? match.Groups["ondelete"].Value : "NO ACTION");
        }

        return foreignKeys;
    }

    /// <summary>Index name -> parsed shape.</summary>
    private static Dictionary<string, SqlIndex> ParseIndexes(string sql)
    {
        var indexes = new Dictionary<string, SqlIndex>(StringComparer.Ordinal);

        foreach (Match match in IndexRegex.Matches(sql))
        {
            indexes[match.Groups["name"].Value] = new SqlIndex(
                match.Groups["unique"].Success,
                match.Groups["clustered"].Value == "CLUSTERED",
                ParseColumnList(match.Groups["cols"].Value),
                match.Groups["filter"].Success ? match.Groups["filter"].Value : null);
        }

        return indexes;
    }

    /// <summary>Parses <c>[A] ASC, [B] DESC</c> (direction optional; SQL Server defaults to ASC).</summary>
    private static List<SqlIndexColumn> ParseColumnList(string columnList) =>
        ColumnListEntryRegex.Matches(columnList)
            .Select(m => new SqlIndexColumn(m.Groups["col"].Value, m.Groups["dir"].Value == "DESC"))
            .ToList();

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

    private sealed record SqlColumn(string Type, bool NotNull, bool IsIdentity, bool HasDefault);

    private sealed record SqlForeignKey(
        List<string> Columns,
        string PrincipalTable,
        List<string> PrincipalColumns,
        string OnDelete);

    private sealed record SqlIndex(bool IsUnique, bool IsClustered, List<SqlIndexColumn> Columns, string? Filter);

    /// <summary>
    /// Strips whitespace and any balanced outer parentheses so DacFx's <c>([EntraGroupId]&lt;&gt;'')</c>
    /// and the model's <c>[EntraGroupId] &lt;&gt; ''</c> compare equal. Text-level only: this test does not
    /// understand T-SQL, so two semantically identical filters written differently still differ here.
    /// </summary>
    private static string NormalizeFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return string.Empty;
        }

        string normalized = string.Concat(filter.Where(c => !char.IsWhiteSpace(c)));
        while (normalized.StartsWith('(') && normalized.EndsWith(')') && IsBalancedWithoutOuterParens(normalized))
        {
            normalized = normalized[1..^1];
        }

        return normalized;
    }

    /// <summary>True when the outermost parentheses of <paramref name="text"/> wrap the whole expression (so they can be stripped).</summary>
    private static bool IsBalancedWithoutOuterParens(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0 && i != text.Length - 1)
                {
                    return false;
                }
            }
        }

        return depth == 0;
    }

    private static string Describe(string filter) => filter.Length == 0 ? "(none)" : $"'{filter}'";

    private sealed record SqlIndexColumn(string Name, bool IsDescending);
}
