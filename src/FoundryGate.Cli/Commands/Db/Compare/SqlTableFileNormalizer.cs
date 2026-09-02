using System.Text.RegularExpressions;

namespace FoundryGate.Cli.Commands.Db.Compare;

/// <summary>
/// Fixes up one quirk in DacFx's <c>PublishChangesToProject</c> output: when a column change forces it
/// to rewrite a table's <c>CREATE TABLE</c> batch from scratch, it writes the primary key <i>inline</i>
/// (<c>CONSTRAINT [PK_x] PRIMARY KEY CLUSTERED (...)</c> as the last column-list entry) — a different,
/// equally valid SSDT style from this repo's own convention of a separate
/// <c>ALTER TABLE ... ADD CONSTRAINT [PK_x] PRIMARY KEY ...</c> batch. Publish only rewrites the batch
/// that actually differed, so the file ends up with <b>both</b>: the new inline definition and the old,
/// untouched <c>ALTER TABLE</c> batch declaring the same constraint name — which fails to deploy
/// (<c>PK_x already exists</c>) even though it still parses fine as valid-looking SQL and still passes
/// <c>SchemaParityTests</c>' first-match regex. <see cref="RemoveDuplicatePrimaryKeyAlterStatement"/>
/// strips the redundant <c>ALTER TABLE</c> batch whenever the <c>CREATE TABLE</c> body already declares
/// that same primary key inline.
/// </summary>
public static class SqlTableFileNormalizer
{
    /// <summary>A parenthesised expression with arbitrarily nested parens (.NET balancing groups).</summary>
    private const string BalancedParens = @"\((?>[^()]+|\((?<depth>)|\)(?<-depth>))*(?(depth)(?!))\)";

    private static readonly Regex InlinePrimaryKeyRegex = new(
        @"CONSTRAINT \[(?<name>PK_[A-Za-z0-9_]+)\] PRIMARY KEY (?:CLUSTERED|NONCLUSTERED)\s*" + BalancedParens,
        RegexOptions.Compiled);

    /// <summary>Matches this repo's separate <c>ALTER TABLE ... ADD CONSTRAINT [PK_x] PRIMARY KEY (...);</c> batch, including its trailing <c>GO</c> and surrounding blank lines.</summary>
    private static Regex AlterTablePrimaryKeyBatchRegex(string constraintName) => new(
        @"(\r?\n)*ALTER TABLE \[dbo\]\.\[[A-Za-z0-9_]+\]\s*\r?\n\s*ADD CONSTRAINT \[" + Regex.Escape(constraintName) +
        @"\] PRIMARY KEY (?:CLUSTERED|NONCLUSTERED)\s*" + BalancedParens + @"\s*;\s*\r?\nGO\r?\n(\r?\n)*",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns <paramref name="sql"/> with any redundant standalone <c>ALTER TABLE ... ADD CONSTRAINT
    /// [PK_x] PRIMARY KEY</c> batch removed, for every primary key <paramref name="sql"/>'s
    /// <c>CREATE TABLE</c> body already declares inline. A no-op when there is no such duplication.
    /// </summary>
    public static string RemoveDuplicatePrimaryKeyAlterStatement(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var inlineMatch = InlinePrimaryKeyRegex.Match(sql);
        if (!inlineMatch.Success)
        {
            return sql;
        }

        var constraintName = inlineMatch.Groups["name"].Value;
        var alterRegex = AlterTablePrimaryKeyBatchRegex(constraintName);

        // Only the ALTER TABLE batch after the CREATE TABLE statement is the leftover duplicate; replace
        // it (plus the blank lines the match consumes on both sides) with a single blank-line separator
        // so surrounding spacing still matches the repo's style (a blank line between GO batches).
        return alterRegex.Replace(sql, "\r\n\r\n", count: 1, startat: inlineMatch.Index + inlineMatch.Length);
    }
}
