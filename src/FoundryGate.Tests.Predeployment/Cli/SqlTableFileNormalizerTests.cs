using FoundryGate.Cli.Commands.Db.Compare;

namespace FoundryGate.Tests.Predeployment.Cli;

/// <summary>
/// Regression coverage for a real bug PR #202's review caught: DacFx's <c>PublishChangesToProject</c>
/// rewrites a changed <c>CREATE TABLE</c> batch with every table-level constraint declared inline —
/// not just the primary key the first cut of this fix handled, but foreign keys, <c>UNIQUE</c>,
/// <c>CHECK</c>, and column-level <c>DEFAULT</c> too — leaving this repo's original, separate
/// <c>ALTER TABLE ... ADD CONSTRAINT</c> batches for those same constraints untouched below it. The
/// reviewer reproduced this live on <c>GroupMembers</c> (composite PK, 3 FKs): the regenerated file
/// declared all three FKs twice and <c>dotnet build src/FoundryGate.Database</c> failed outright
/// (<c>SQL71508</c>) — not a hypothetical, and not something <c>SchemaParityTests</c> catches either,
/// since it only ever looks at the first occurrence of each constraint name.
/// <see cref="SqlTableFileNormalizer"/> is what <c>DacFxSchemaComparer.Publish</c> runs over every file
/// DacFx reports as changed/added to fix that up, for every constraint kind.
/// </summary>
public class SqlTableFileNormalizerTests
{
    private const string FilePath = @"C:\repo\src\FoundryGate.Database\dbo\Tables\Widgets.sql";

    [Fact]
    public void Primary_key_Removes_the_inline_duplicate_and_keeps_the_ALTER_TABLE_batch_as_canonical()
    {
        // The original Users.sql-based fixture from this fix's first cut — kept, but note the direction
        // flipped: earlier this method stripped the ALTER TABLE batch and kept the inline PK; it now
        // does the opposite (keeps ALTER TABLE, strips inline), so the file lands back in this repo's
        // own separate-batch style instead of DacFx's alternative "constraints inline" style.
        const string Duplicated =
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

        const string Expected =
            "CREATE TABLE [dbo].[Users] (\r\n" +
            "    [UserId]      INT            IDENTITY (1, 1) NOT NULL,\r\n" +
            "    [DisplayName] NVARCHAR (250) NOT NULL\r\n" +
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

        var result = SqlTableFileNormalizer.RemoveDuplicateInlineConstraints(Duplicated, FilePath);

        Assert.Equal(Expected, result);
        Assert.Equal(1, CountOccurrences(result, "PK_Users"));
    }

    [Fact]
    public void Foreign_keys_Removes_every_inline_duplicate_reproducing_the_real_GroupMembers_failure()
    {
        // This is the reviewer's exact repro shape: a composite PK plus three FKs, all three inlined
        // by DacFx alongside the PK. The expected output is byte-for-byte this repo's actual, hand
        // -authored GroupMembers.sql (src/FoundryGate.Database/dbo/Tables/GroupMembers.sql) — proof
        // that de-duplicating lands the file back in the exact style it started in, not just "some
        // valid SQL".
        const string Duplicated =
            "CREATE TABLE [dbo].[GroupMembers] (\r\n" +
            "    [GroupId]        INT               NOT NULL,\r\n" +
            "    [UserId]         INT               NOT NULL,\r\n" +
            "    [AddedDate]      DATETIMEOFFSET (7) NOT NULL,\r\n" +
            "    [AddedByUserId]  INT               NULL,\r\n" +
            "    CONSTRAINT [PK_GroupMembers] PRIMARY KEY CLUSTERED ([GroupId] ASC, [UserId] ASC),\r\n" +
            "    CONSTRAINT [FK_GroupMembers_Groups_GroupId] FOREIGN KEY ([GroupId]) REFERENCES [dbo].[Groups] ([GroupId]) ON DELETE CASCADE,\r\n" +
            "    CONSTRAINT [FK_GroupMembers_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId]),\r\n" +
            "    CONSTRAINT [FK_GroupMembers_Users_AddedByUserId] FOREIGN KEY ([AddedByUserId]) REFERENCES [dbo].[Users] ([UserId])\r\n" +
            ");\r\n" +
            "GO\r\n" +
            "\r\n" +
            "ALTER TABLE [dbo].[GroupMembers]\r\n" +
            "    ADD CONSTRAINT [PK_GroupMembers] PRIMARY KEY CLUSTERED ([GroupId] ASC, [UserId] ASC);\r\n" +
            "GO\r\n" +
            "\r\n" +
            "ALTER TABLE [dbo].[GroupMembers]\r\n" +
            "    ADD CONSTRAINT [FK_GroupMembers_Groups_GroupId] FOREIGN KEY ([GroupId]) REFERENCES [dbo].[Groups] ([GroupId]) ON DELETE CASCADE;\r\n" +
            "GO\r\n" +
            "\r\n" +
            "ALTER TABLE [dbo].[GroupMembers]\r\n" +
            "    ADD CONSTRAINT [FK_GroupMembers_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId]);\r\n" +
            "GO\r\n" +
            "\r\n" +
            "ALTER TABLE [dbo].[GroupMembers]\r\n" +
            "    ADD CONSTRAINT [FK_GroupMembers_Users_AddedByUserId] FOREIGN KEY ([AddedByUserId]) REFERENCES [dbo].[Users] ([UserId]);\r\n" +
            "GO\r\n" +
            "\r\n" +
            "-- GroupId is the leading column of the clustered PK above, so it does not need its own\r\n" +
            "-- index; UserId and AddedByUserId are not covered by that prefix and need theirs.\r\n" +
            "CREATE NONCLUSTERED INDEX [IX_GroupMembers_UserId]\r\n" +
            "    ON [dbo].[GroupMembers]([UserId] ASC);\r\n" +
            "GO\r\n" +
            "\r\n" +
            "CREATE NONCLUSTERED INDEX [IX_GroupMembers_AddedByUserId]\r\n" +
            "    ON [dbo].[GroupMembers]([AddedByUserId] ASC);\r\n" +
            "GO\r\n";

        const string Expected =
            "CREATE TABLE [dbo].[GroupMembers] (\r\n" +
            "    [GroupId]        INT               NOT NULL,\r\n" +
            "    [UserId]         INT               NOT NULL,\r\n" +
            "    [AddedDate]      DATETIMEOFFSET (7) NOT NULL,\r\n" +
            "    [AddedByUserId]  INT               NULL\r\n" +
            ");\r\n" +
            "GO\r\n" +
            "\r\n" +
            "ALTER TABLE [dbo].[GroupMembers]\r\n" +
            "    ADD CONSTRAINT [PK_GroupMembers] PRIMARY KEY CLUSTERED ([GroupId] ASC, [UserId] ASC);\r\n" +
            "GO\r\n" +
            "\r\n" +
            "ALTER TABLE [dbo].[GroupMembers]\r\n" +
            "    ADD CONSTRAINT [FK_GroupMembers_Groups_GroupId] FOREIGN KEY ([GroupId]) REFERENCES [dbo].[Groups] ([GroupId]) ON DELETE CASCADE;\r\n" +
            "GO\r\n" +
            "\r\n" +
            "ALTER TABLE [dbo].[GroupMembers]\r\n" +
            "    ADD CONSTRAINT [FK_GroupMembers_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId]);\r\n" +
            "GO\r\n" +
            "\r\n" +
            "ALTER TABLE [dbo].[GroupMembers]\r\n" +
            "    ADD CONSTRAINT [FK_GroupMembers_Users_AddedByUserId] FOREIGN KEY ([AddedByUserId]) REFERENCES [dbo].[Users] ([UserId]);\r\n" +
            "GO\r\n" +
            "\r\n" +
            "-- GroupId is the leading column of the clustered PK above, so it does not need its own\r\n" +
            "-- index; UserId and AddedByUserId are not covered by that prefix and need theirs.\r\n" +
            "CREATE NONCLUSTERED INDEX [IX_GroupMembers_UserId]\r\n" +
            "    ON [dbo].[GroupMembers]([UserId] ASC);\r\n" +
            "GO\r\n" +
            "\r\n" +
            "CREATE NONCLUSTERED INDEX [IX_GroupMembers_AddedByUserId]\r\n" +
            "    ON [dbo].[GroupMembers]([AddedByUserId] ASC);\r\n" +
            "GO\r\n";

        var result = SqlTableFileNormalizer.RemoveDuplicateInlineConstraints(Duplicated, FilePath);

        Assert.Equal(Expected, result);
        Assert.Equal(1, CountOccurrences(result, "FK_GroupMembers_Groups_GroupId"));
        Assert.Equal(1, CountOccurrences(result, "FK_GroupMembers_Users_UserId"));
        Assert.Equal(1, CountOccurrences(result, "FK_GroupMembers_Users_AddedByUserId"));
        Assert.Equal(1, CountOccurrences(result, "PK_GroupMembers"));
    }

    [Fact]
    public void Unique_Removes_the_inline_duplicate_and_keeps_the_ALTER_TABLE_batch_as_canonical()
    {
        const string Duplicated =
            "CREATE TABLE [dbo].[Widgets] (\r\n" +
            "    [WidgetId] INT           NOT NULL,\r\n" +
            "    [Code]     NVARCHAR (50) NOT NULL,\r\n" +
            "    CONSTRAINT [UQ_Widgets_Code] UNIQUE ([Code] ASC)\r\n" +
            ");\r\n" +
            "GO\r\n" +
            "\r\n" +
            "ALTER TABLE [dbo].[Widgets]\r\n" +
            "    ADD CONSTRAINT [UQ_Widgets_Code] UNIQUE ([Code] ASC);\r\n" +
            "GO\r\n";

        const string Expected =
            "CREATE TABLE [dbo].[Widgets] (\r\n" +
            "    [WidgetId] INT           NOT NULL,\r\n" +
            "    [Code]     NVARCHAR (50) NOT NULL\r\n" +
            ");\r\n" +
            "GO\r\n" +
            "\r\n" +
            "ALTER TABLE [dbo].[Widgets]\r\n" +
            "    ADD CONSTRAINT [UQ_Widgets_Code] UNIQUE ([Code] ASC);\r\n" +
            "GO\r\n";

        var result = SqlTableFileNormalizer.RemoveDuplicateInlineConstraints(Duplicated, FilePath);

        Assert.Equal(Expected, result);
        Assert.Equal(1, CountOccurrences(result, "UQ_Widgets_Code"));
    }

    [Fact]
    public void Check_Removes_the_inline_duplicate_and_keeps_the_ALTER_TABLE_batch_as_canonical()
    {
        const string Duplicated =
            "CREATE TABLE [dbo].[Widgets] (\r\n" +
            "    [WidgetId] INT NOT NULL,\r\n" +
            "    [Quantity] INT NOT NULL,\r\n" +
            "    CONSTRAINT [CK_Widgets_Quantity] CHECK ([Quantity] >= (0))\r\n" +
            ");\r\n" +
            "GO\r\n" +
            "\r\n" +
            "ALTER TABLE [dbo].[Widgets]\r\n" +
            "    ADD CONSTRAINT [CK_Widgets_Quantity] CHECK ([Quantity] >= (0));\r\n" +
            "GO\r\n";

        const string Expected =
            "CREATE TABLE [dbo].[Widgets] (\r\n" +
            "    [WidgetId] INT NOT NULL,\r\n" +
            "    [Quantity] INT NOT NULL\r\n" +
            ");\r\n" +
            "GO\r\n" +
            "\r\n" +
            "ALTER TABLE [dbo].[Widgets]\r\n" +
            "    ADD CONSTRAINT [CK_Widgets_Quantity] CHECK ([Quantity] >= (0));\r\n" +
            "GO\r\n";

        var result = SqlTableFileNormalizer.RemoveDuplicateInlineConstraints(Duplicated, FilePath);

        Assert.Equal(Expected, result);
        Assert.Equal(1, CountOccurrences(result, "CK_Widgets_Quantity"));
    }

    [Fact]
    public void Default_Removes_the_inline_column_level_duplicate_and_keeps_the_ALTER_TABLE_batch_as_canonical()
    {
        // DEFAULT is the one kind that is never a standalone comma-list item — it's always embedded
        // inside a column definition, so its removal is a plain substring strip, not comma bookkeeping.
        const string Duplicated =
            "CREATE TABLE [dbo].[Widgets] (\r\n" +
            "    [WidgetId] INT NOT NULL,\r\n" +
            "    [IsActive] BIT CONSTRAINT [DF_Widgets_IsActive] DEFAULT ((1)) NOT NULL\r\n" +
            ");\r\n" +
            "GO\r\n" +
            "\r\n" +
            "ALTER TABLE [dbo].[Widgets]\r\n" +
            "    ADD CONSTRAINT [DF_Widgets_IsActive] DEFAULT ((1)) FOR [IsActive];\r\n" +
            "GO\r\n";

        const string Expected =
            "CREATE TABLE [dbo].[Widgets] (\r\n" +
            "    [WidgetId] INT NOT NULL,\r\n" +
            "    [IsActive] BIT NOT NULL\r\n" +
            ");\r\n" +
            "GO\r\n" +
            "\r\n" +
            "ALTER TABLE [dbo].[Widgets]\r\n" +
            "    ADD CONSTRAINT [DF_Widgets_IsActive] DEFAULT ((1)) FOR [IsActive];\r\n" +
            "GO\r\n";

        var result = SqlTableFileNormalizer.RemoveDuplicateInlineConstraints(Duplicated, FilePath);

        Assert.Equal(Expected, result);
        Assert.Equal(1, CountOccurrences(result, "DF_Widgets_IsActive"));
    }

    [Fact]
    public void Is_a_no_op_when_every_constraint_is_only_the_separate_ALTER_TABLE_style_this_repo_normally_uses()
    {
        // This repo's own hand-authored style (no inline constraints in CREATE TABLE at all) — nothing
        // for the normalizer to do, and it must never touch the single ALTER TABLE ADD CONSTRAINT it finds.
        const string Sql =
            "CREATE TABLE [dbo].[Widgets] (\r\n" +
            "    [WidgetId] INT NOT NULL\r\n" +
            ");\r\n" +
            "GO\r\n" +
            "\r\n" +
            "ALTER TABLE [dbo].[Widgets]\r\n" +
            "    ADD CONSTRAINT [PK_Widgets] PRIMARY KEY CLUSTERED ([WidgetId] ASC);\r\n" +
            "GO\r\n";

        var result = SqlTableFileNormalizer.RemoveDuplicateInlineConstraints(Sql, FilePath);

        Assert.Equal(Sql, result);
    }

    [Fact]
    public void Leaves_a_genuinely_new_inline_constraint_untouched_when_no_ALTER_TABLE_batch_exists_for_it()
    {
        // A constraint with no matching ALTER TABLE batch anywhere in the file is not a duplicate — it's
        // new (e.g. a brand-new table DacFx generated wholesale). Nothing to de-duplicate against, so
        // the file is returned unchanged even though it has an inline PK.
        const string Sql =
            "CREATE TABLE [dbo].[Widgets] (\r\n" +
            "    [WidgetId] INT NOT NULL,\r\n" +
            "    CONSTRAINT [PK_Widgets] PRIMARY KEY CLUSTERED ([WidgetId] ASC)\r\n" +
            ");\r\n" +
            "GO\r\n";

        var result = SqlTableFileNormalizer.RemoveDuplicateInlineConstraints(Sql, FilePath);

        Assert.Equal(Sql, result);
    }

    [Fact]
    public void Throws_naming_the_file_when_the_CREATE_TABLE_body_cannot_be_reliably_located()
    {
        // Deliberately malformed: the CREATE TABLE's own closing paren has no trailing semicolon, so
        // TableBodyRegex's lazy "first );" heuristic would otherwise run past it and swallow the ALTER
        // TABLE batch below into what it thinks is the column list. TryGetTableBody's ALTER-TABLE-inside
        // -the-body guard rejects that over-match, so this exercises the "could not locate the body"
        // path -- and it must throw (naming the file), not silently ship whatever a bad match would
        // have produced.
        const string Malformed =
            "CREATE TABLE [dbo].[Widgets] (\r\n" +
            "    [WidgetId] INT NOT NULL,\r\n" +
            "    CONSTRAINT [PK_Widgets] PRIMARY KEY CLUSTERED ([WidgetId] ASC)\r\n" +
            ")\r\n" +
            "GO\r\n" +
            "\r\n" +
            "ALTER TABLE [dbo].[Widgets]\r\n" +
            "    ADD CONSTRAINT [PK_Widgets] PRIMARY KEY CLUSTERED ([WidgetId] ASC);\r\n" +
            "GO\r\n";

        var exception = Assert.Throws<InvalidOperationException>(
            () => SqlTableFileNormalizer.RemoveDuplicateInlineConstraints(Malformed, FilePath));

        Assert.Contains(FilePath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Throws_for_a_null_sql_argument()
    {
        Assert.Throws<ArgumentNullException>(() => SqlTableFileNormalizer.RemoveDuplicateInlineConstraints(null!, FilePath));
    }

    [Fact]
    public void Throws_for_a_null_or_empty_file_path_argument()
    {
        Assert.Throws<ArgumentException>(() => SqlTableFileNormalizer.RemoveDuplicateInlineConstraints("CREATE TABLE [dbo].[X] ([A] INT NOT NULL);\r\nGO\r\n", string.Empty));
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
