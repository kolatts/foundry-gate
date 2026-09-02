using FoundryGate.Cli.Commands.Db.Compare;

namespace FoundryGate.Tests.Predeployment.Cli;

/// <summary>
/// Regression coverage for a real bug the live proof for #103 caught: DacFx's
/// <c>PublishChangesToProject</c> rewrites a changed <c>CREATE TABLE</c> batch with the primary key
/// declared inline, but leaves this repo's original, separate <c>ALTER TABLE ... ADD CONSTRAINT [PK_x]
/// PRIMARY KEY ...</c> batch untouched below it — two declarations of the same constraint, which fails
/// to deploy even though it still parses. <see cref="SqlTableFileNormalizer"/> is what
/// <c>DacFxSchemaComparer.Publish</c> runs over every file DacFx reports as changed/added to fix that up.
/// </summary>
public class SqlTableFileNormalizerTests
{
    private const string TableWithDuplicatePrimaryKey =
        "CREATE TABLE [dbo].[Users] (\r\n" +
        "    [UserId]      INT            IDENTITY (1, 1) NOT NULL,\r\n" +
        "    [DisplayName] NVARCHAR (250) NOT NULL,\r\n" +
        "    CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED ([UserId] ASC)\r\n" +
        ");\r\n" +
        "GO\r\n" +
        "\r\n" +
        "ALTER TABLE [dbo].[Users]\r\n" +
        "    ADD CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED ([UserId] ASC);\r\n" +
        "GO\r\n" +
        "\r\n" +
        "CREATE UNIQUE NONCLUSTERED INDEX [IX_Users_EntraObjectId]\r\n" +
        "    ON [dbo].[Users]([EntraObjectId] ASC);\r\n" +
        "GO\r\n";

    private const string TableWithoutDuplication =
        "CREATE TABLE [dbo].[Users] (\r\n" +
        "    [UserId]      INT            IDENTITY (1, 1) NOT NULL,\r\n" +
        "    [DisplayName] NVARCHAR (250) NOT NULL,\r\n" +
        "    CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED ([UserId] ASC)\r\n" +
        ");\r\n" +
        "GO\r\n" +
        "\r\n" +
        "CREATE UNIQUE NONCLUSTERED INDEX [IX_Users_EntraObjectId]\r\n" +
        "    ON [dbo].[Users]([EntraObjectId] ASC);\r\n" +
        "GO\r\n";

    [Fact]
    public void Removes_the_redundant_ALTER_TABLE_batch_when_the_primary_key_is_already_inline()
    {
        var result = SqlTableFileNormalizer.RemoveDuplicatePrimaryKeyAlterStatement(TableWithDuplicatePrimaryKey);

        Assert.Equal(TableWithoutDuplication, result);
        Assert.Equal(1, CountOccurrences(result, "PK_Users"));
    }

    [Fact]
    public void Is_a_no_op_when_the_primary_key_is_only_declared_once()
    {
        var result = SqlTableFileNormalizer.RemoveDuplicatePrimaryKeyAlterStatement(TableWithoutDuplication);

        Assert.Equal(TableWithoutDuplication, result);
    }

    [Fact]
    public void Is_a_no_op_when_the_primary_key_is_only_the_separate_ALTER_TABLE_style_this_repo_normally_uses()
    {
        // This repo's own hand-authored style (no inline PK in CREATE TABLE) — nothing for the
        // normalizer to do, and it must never touch the single ALTER TABLE ADD CONSTRAINT it finds.
        const string Sql =
            "CREATE TABLE [dbo].[Users] (\r\n" +
            "    [UserId] INT IDENTITY (1, 1) NOT NULL\r\n" +
            ");\r\n" +
            "GO\r\n" +
            "\r\n" +
            "ALTER TABLE [dbo].[Users]\r\n" +
            "    ADD CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED ([UserId] ASC);\r\n" +
            "GO\r\n";

        var result = SqlTableFileNormalizer.RemoveDuplicatePrimaryKeyAlterStatement(Sql);

        Assert.Equal(Sql, result);
    }

    [Fact]
    public void Throws_for_a_null_input()
    {
        Assert.Throws<ArgumentNullException>(() => SqlTableFileNormalizer.RemoveDuplicatePrimaryKeyAlterStatement(null!));
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
