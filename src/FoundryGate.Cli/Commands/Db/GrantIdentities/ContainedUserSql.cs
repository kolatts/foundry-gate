using System.Text;

namespace FoundryGate.Cli.Commands.Db.GrantIdentities;

/// <summary>An Entra principal (managed identity) that needs a contained database user.</summary>
/// <param name="Name">Display name of the identity — the database user name, e.g. <c>id-foundrygate-api-dev</c>.</param>
/// <param name="ClientId">
/// The identity's client (application) id, and the expected input: the user is then created
/// <c>WITH SID = ..., TYPE = E</c> from that id, which needs no directory lookup. When
/// <see langword="null"/> the user can only be created <c>FROM EXTERNAL PROVIDER</c>, which asks Azure SQL
/// to resolve the name in Entra and therefore requires the logical server's own managed identity to hold
/// the Directory Readers role - which <c>infra/modules/sql.bicep</c> does not grant, so
/// <see cref="GrantIdentitiesRunner"/> refuses it without <c>--allow-external-provider</c> (#106, #142).
/// </param>
public sealed record ContainedUserGrant(string Name, Guid? ClientId);

/// <summary>
/// Generates the idempotent T-SQL that gives an Entra managed identity access to the FoundryGate
/// database. Roles are fixed at <c>db_datareader</c> + <c>db_datawriter</c>: the API and Functions read
/// and write rows only — the dacpac owns every schema object and EF never migrates at runtime — so
/// <c>db_ddladmin</c> is deliberately not granted (it would let a compromised host alter the schema).
/// </summary>
public static class ContainedUserSql
{
    /// <summary>Database roles every FoundryGate host identity is a member of.</summary>
    public static readonly IReadOnlyList<string> Roles = ["db_datareader", "db_datawriter"];

    /// <summary><c>sysname</c> limit for a database principal name.</summary>
    public const int MaxPrincipalNameLength = 128;

    /// <summary>
    /// One self-contained batch for <paramref name="grant"/>: create the user if it does not exist, then add it
    /// to each role it is not yet in. Re-running against a fully provisioned database executes no DDL. The
    /// principal name — and every role name — is bound to a <c>sysname</c> variable and only ever reaches
    /// DDL through <c>QUOTENAME</c>, so a name containing <c>]</c> or <c>'</c> cannot break out of its identifier.
    /// </summary>
    public static string Build(ContainedUserGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ValidatePrincipalName(grant.Name);

        var literal = ToUnicodeLiteral(grant.Name);
        var sql = new StringBuilder();
        sql.AppendLine($"DECLARE @principal sysname = {literal};");
        // EXEC(...) accepts only literals/variables joined with +, never a function call (verified against
        // SQL Server 2022: "Incorrect syntax near 'QUOTENAME'"), so QUOTENAME is evaluated once into a
        // variable and each statement is assembled into @sql before sp_executesql runs it.
        sql.AppendLine("DECLARE @quoted nvarchar(258) = QUOTENAME(@principal);");
        sql.AppendLine("DECLARE @role sysname;");
        sql.AppendLine("DECLARE @quotedRole nvarchar(258);");
        sql.AppendLine("DECLARE @sql nvarchar(max);");

        if (grant.ClientId is { } clientId)
        {
            // Service-principal SID = the client id as a 16-byte binary (SQL Server's own uniqueidentifier
            // byte order, so the conversion is done in T-SQL rather than reimplemented here).
            sql.AppendLine($"DECLARE @sid varbinary(16) = CONVERT(varbinary(16), CONVERT(uniqueidentifier, N'{clientId:D}'));");
            sql.AppendLine("IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @principal)");
            sql.AppendLine("BEGIN");
            sql.AppendLine("    SET @sql = N'CREATE USER ' + @quoted + N' WITH SID = ' + CONVERT(nvarchar(34), @sid, 1) + N', TYPE = E;';");
            sql.AppendLine("    EXEC sp_executesql @sql;");
            sql.AppendLine("END;");
        }
        else
        {
            sql.AppendLine("IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @principal)");
            sql.AppendLine("BEGIN");
            sql.AppendLine("    SET @sql = N'CREATE USER ' + @quoted + N' FROM EXTERNAL PROVIDER;';");
            sql.AppendLine("    EXEC sp_executesql @sql;");
            sql.AppendLine("END;");
        }

        // The role names are a private const list today, but they reach DDL the same way the principal
        // name does — bound to a sysname variable and quoted by QUOTENAME — so making them configurable
        // later cannot open an injection point in the one file whose whole argument is that it never does that.
        foreach (var role in Roles)
        {
            sql.AppendLine($"SET @role = {ToUnicodeLiteral(role)};");
            sql.AppendLine("SET @quotedRole = QUOTENAME(@role);");
            sql.AppendLine("IF ISNULL(IS_ROLEMEMBER(@role, @principal), 0) = 0");
            sql.AppendLine("BEGIN");
            sql.AppendLine("    SET @sql = N'ALTER ROLE ' + @quotedRole + N' ADD MEMBER ' + @quoted + N';';");
            sql.AppendLine("    EXEC sp_executesql @sql;");
            sql.AppendLine("END;");
        }

        return sql.ToString();
    }

    /// <summary>Rejects names SQL Server would refuse anyway, with a message that names the offending value.</summary>
    public static void ValidatePrincipalName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Identity name must not be empty.", nameof(name));
        }

        if (name.Length > MaxPrincipalNameLength)
        {
            throw new ArgumentException($"Identity name '{name}' exceeds the {MaxPrincipalNameLength}-character sysname limit.", nameof(name));
        }
    }

    /// <summary><c>N'...'</c> with embedded single quotes doubled.</summary>
    private static string ToUnicodeLiteral(string value) => "N'" + value.Replace("'", "''") + "'";
}
