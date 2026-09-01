using FoundryGate.Cli.Commands.Db.GrantIdentities;

namespace FoundryGate.Tests.Predeployment.Cli;

public class ContainedUserSqlTests
{
    [Fact]
    public void Roles_are_exactly_reader_and_writer_never_ddladmin()
    {
        Assert.Equal(["db_datareader", "db_datawriter"], ContainedUserSql.Roles);
        Assert.DoesNotContain("db_ddladmin", ContainedUserSql.Roles);
        Assert.DoesNotContain("db_owner", ContainedUserSql.Roles);
    }

    [Fact]
    public void External_provider_batch_creates_the_user_only_if_missing_and_adds_each_role_only_if_not_a_member()
    {
        var sql = ContainedUserSql.Build(new ContainedUserGrant("id-foundrygate-api-dev", null));

        Assert.Equal(
            """
            DECLARE @principal sysname = N'id-foundrygate-api-dev';
            DECLARE @quoted nvarchar(258) = QUOTENAME(@principal);
            DECLARE @sql nvarchar(max);
            IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @principal)
            BEGIN
                SET @sql = N'CREATE USER ' + @quoted + N' FROM EXTERNAL PROVIDER;';
                EXEC sp_executesql @sql;
            END;
            IF ISNULL(IS_ROLEMEMBER(N'db_datareader', @principal), 0) = 0
            BEGIN
                SET @sql = N'ALTER ROLE [db_datareader] ADD MEMBER ' + @quoted + N';';
                EXEC sp_executesql @sql;
            END;
            IF ISNULL(IS_ROLEMEMBER(N'db_datawriter', @principal), 0) = 0
            BEGIN
                SET @sql = N'ALTER ROLE [db_datawriter] ADD MEMBER ' + @quoted + N';';
                EXEC sp_executesql @sql;
            END;

            """.ReplaceLineEndings(),
            sql.ReplaceLineEndings());
    }

    [Fact]
    public void Client_id_batch_creates_the_user_with_an_explicit_SID_instead_of_a_directory_lookup()
    {
        var clientId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var sql = ContainedUserSql.Build(new ContainedUserGrant("id-foundrygate-func-dev", clientId));

        Assert.Contains("DECLARE @sid varbinary(16) = CONVERT(varbinary(16), CONVERT(uniqueidentifier, N'11111111-2222-3333-4444-555555555555'));", sql);
        Assert.Contains("SET @sql = N'CREATE USER ' + @quoted + N' WITH SID = ' + CONVERT(nvarchar(34), @sid, 1) + N', TYPE = E;';", sql);
        Assert.DoesNotContain("FROM EXTERNAL PROVIDER", sql);
        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @principal)", sql);
        Assert.Contains("ALTER ROLE [db_datareader] ADD MEMBER", sql);
        Assert.Contains("ALTER ROLE [db_datawriter] ADD MEMBER", sql);
    }

    [Fact]
    public void The_principal_name_only_appears_as_an_escaped_literal_and_reaches_DDL_through_QUOTENAME()
    {
        var sql = ContainedUserSql.Build(new ContainedUserGrant("odd]name'; DROP TABLE Users; --", null));

        Assert.Contains("DECLARE @principal sysname = N'odd]name''; DROP TABLE Users; --';", sql);
        Assert.Equal(1, CountOccurrences(sql, "DROP TABLE"));
        Assert.DoesNotContain("[odd]name", sql);
        Assert.Equal(1, CountOccurrences(sql, "QUOTENAME(@principal)"));
        Assert.Equal(3, CountOccurrences(sql, "+ @quoted +"));
        // Every dynamic statement goes through sp_executesql, never EXEC(<expression>), which SQL Server
        // rejects when the expression contains a function call.
        Assert.DoesNotContain("EXEC (", sql);
        Assert.Equal(3, CountOccurrences(sql, "EXEC sp_executesql @sql;"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_an_empty_identity_name(string name)
    {
        Assert.Throws<ArgumentException>(() => ContainedUserSql.Build(new ContainedUserGrant(name, null)));
    }

    [Fact]
    public void Rejects_a_name_longer_than_sysname()
    {
        var ex = Assert.Throws<ArgumentException>(() => ContainedUserSql.Build(new ContainedUserGrant(new string('x', 129), null)));

        Assert.Contains("128", ex.Message);
    }

    [Fact]
    public void Accepts_the_longest_legal_name()
    {
        _ = ContainedUserSql.Build(new ContainedUserGrant(new string('x', 128), null));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
