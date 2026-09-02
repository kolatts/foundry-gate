using System.Text;
using System.Text.RegularExpressions;

namespace FoundryGate.Cli.Commands.Db.Compare;

/// <summary>
/// Fixes up a quirk in DacFx's <c>PublishChangesToProject</c> output: when any column changes, DacFx
/// rewrites the table's whole <c>CREATE TABLE</c> batch from scratch and inlines every table-level
/// constraint into it — primary key, foreign keys, <c>UNIQUE</c>, <c>CHECK</c>, and column-level
/// <c>DEFAULT</c> constraints all get a <c>CONSTRAINT [name] ...</c> clause inside the column list — a
/// different, equally valid SSDT style from this repo's own convention of a separate
/// <c>ALTER TABLE ... ADD CONSTRAINT [name] ...</c> batch per constraint. Publish only rewrites the
/// batch that actually differed, so a file that already had those constraints as separate batches ends
/// up with <b>both</b>: the new inline copy and the old, untouched <c>ALTER TABLE</c> batch declaring
/// the same constraint name. That still parses as valid-looking SQL — and still passes
/// <c>SchemaParityTests</c>' first-match-wins regex, since it only ever looks at the first occurrence of
/// each constraint name — but fails <c>dotnet build src/FoundryGate.Database</c> outright
/// (<c>SQL71508: The model already has an element that has the same name</c>) for anything but a
/// single-column primary key with no foreign keys.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RemoveDuplicateInlineConstraints"/> treats this repo's separate <c>ALTER TABLE ... ADD
/// CONSTRAINT</c> batches as canonical and strips the newly-inlined duplicate out of the <c>CREATE
/// TABLE</c> body instead of the other way around (rewriting/removing the <c>ALTER TABLE</c> batch to
/// match DacFx's inline style). That direction is both simpler and self-reinforcing: the file lands back
/// in exactly the same shape this repo's other hand-authored tables already use — columns only inside
/// <c>CREATE TABLE</c>, every constraint as its own trailing <c>ALTER TABLE</c> batch — rather than DacFx's
/// alternative "constraints inline, indexes separate" style leaking into one table and not its neighbors.
/// </para>
/// <para>
/// A constraint whose name has <i>no</i> matching <c>ALTER TABLE ... ADD CONSTRAINT</c> batch anywhere
/// in the file — i.e. a genuinely new constraint, not a duplicate — is left inline untouched. That is a
/// real, valid SQL file (it still builds and deploys); it just does not match this repo's usual style
/// for that one constraint, and — for a foreign key specifically — will not be recognised as present by
/// <c>SchemaParityTests</c>' regex, which only understands the separate-<c>ALTER TABLE</c> form. That
/// residual gap only applies to a constraint appearing for the first time (e.g. a brand-new table, which
/// DacFx generates wholesale rather than incrementally) and is out of scope for the duplication bug this
/// class fixes; see #103's PR discussion.
/// </para>
/// </remarks>
public static class SqlTableFileNormalizer
{
    private static readonly Regex TableBodyRegex = new(
        @"CREATE TABLE \[dbo\]\.\[[A-Za-z0-9_]+\]\s*\((?<body>.*?)\)\s*;",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Every constraint name this file already declares via a separate <c>ALTER TABLE ... ADD CONSTRAINT [name]</c> batch, of any kind (PK/FK/UNIQUE/CHECK/DEFAULT).</summary>
    private static readonly Regex AlterTableAddConstraintNameRegex = new(
        @"ALTER TABLE \[dbo\]\.\[[A-Za-z0-9_]+\]\s*\r?\n\s*ADD CONSTRAINT \[(?<name>[A-Za-z0-9_]+)\]",
        RegexOptions.Compiled);

    /// <summary>
    /// A table-level constraint clause DacFx can inline into a <c>CREATE TABLE</c> column list:
    /// <c>PRIMARY KEY</c>, <c>UNIQUE</c>, <c>FOREIGN KEY ... REFERENCES ...</c>, or <c>CHECK</c>.
    /// Deliberately excludes <c>DEFAULT</c> — that one is never a standalone list item, only ever
    /// embedded inside a column definition (see <see cref="InlineDefaultConstraintRegex"/>).
    /// </summary>
    private static readonly Regex InlineTableConstraintRegex = new(
        @"CONSTRAINT \[(?<name>[A-Za-z0-9_]+)\]\s+(?:" +
        @"PRIMARY KEY\s+(?:CLUSTERED|NONCLUSTERED)\s*" + SqlPatterns.BalancedParens +
        @"|UNIQUE(?:\s+(?:CLUSTERED|NONCLUSTERED))?\s*" + SqlPatterns.BalancedParens +
        @"|FOREIGN KEY\s*" + SqlPatterns.BalancedParens + @"\s*REFERENCES\s*\[dbo\]\.\[[A-Za-z0-9_]+\]\s*" + SqlPatterns.BalancedParens +
        @"(?:\s*ON DELETE (?:CASCADE|SET NULL|SET DEFAULT|NO ACTION))?(?:\s*ON UPDATE (?:CASCADE|SET NULL|SET DEFAULT|NO ACTION))?" +
        @"|CHECK\s*" + SqlPatterns.BalancedParens +
        @")",
        RegexOptions.Compiled);

    /// <summary>A named <c>DEFAULT</c> clause embedded inside a column definition (the leading whitespace is part of the match, so removing it leaves clean spacing behind).</summary>
    private static readonly Regex InlineDefaultConstraintRegex = new(
        @"\s+CONSTRAINT \[(?<name>[A-Za-z0-9_]+)\]\s+DEFAULT\s*" + SqlPatterns.BalancedParens,
        RegexOptions.Compiled);

    /// <summary>
    /// Returns <paramref name="sql"/> with every table-level or column-level constraint clause DacFx
    /// inlined into its <c>CREATE TABLE</c> body removed, for every constraint name that also has a
    /// separate <c>ALTER TABLE ... ADD CONSTRAINT</c> batch elsewhere in the file (the repo's own,
    /// canonical declaration). A no-op when there is no such duplication.
    /// </summary>
    /// <param name="sql">The full contents of one <c>dbo/Tables/*.sql</c> file.</param>
    /// <param name="filePath">
    /// The file's path, used only to name it in the exception thrown when de-duplication cannot be
    /// verified to have fully succeeded — never silently no-ops.
    /// </param>
    public static string RemoveDuplicateInlineConstraints(string sql, string filePath)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var knownNames = CollectAlterTableConstraintNames(sql);
        if (knownNames.Count == 0)
        {
            return sql;
        }

        var result = TryGetTableBody(sql, out var bodyMatch)
            ? RemoveInlineDuplicatesFromBody(sql, bodyMatch!, knownNames)
            : sql;

        // Fail loudly rather than silently shipping a file that still has DacFx's duplication bug: if a
        // constraint name that already has a separate ALTER TABLE batch still appears as an inline
        // clause after this pass — because the CREATE TABLE body could not even be located, or because
        // the removal logic above did not recognise the exact shape DacFx wrote — something about this
        // file didn't match what this method expects, and a human needs to look at it before it's
        // committed. SchemaParityTests can pass a file that still fails `dotnet build
        // src/FoundryGate.Database` (see this class's own remarks), so that test is not a safety net here.
        if (HasInlineDuplicate(result, knownNames))
        {
            throw new InvalidOperationException(
                $"{filePath}: DacFx inlined a constraint into CREATE TABLE that also has a separate " +
                "ALTER TABLE ... ADD CONSTRAINT batch elsewhere in the file, and SqlTableFileNormalizer " +
                "could not safely remove the duplicate — the file's shape didn't match what it expected. " +
                "Inspect the file by hand before committing.");
        }

        return result;
    }

    private static HashSet<string> CollectAlterTableConstraintNames(string sql) =>
        AlterTableAddConstraintNameRegex.Matches(sql)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static bool HasInlineDuplicate(string sql, HashSet<string> knownNames)
    {
        // Deliberately falls back to scanning the whole file (not just the body) when the body cannot
        // be reliably isolated: a legitimate ALTER TABLE ... ADD CONSTRAINT batch's own clause text also
        // matches InlineTableConstraintRegex/InlineDefaultConstraintRegex (they don't care whether
        // "ADD " precedes "CONSTRAINT"), so this always finds *something* in that fallback case — which
        // is exactly the conservative "fail loudly rather than guess" behavior this check exists for.
        var body = TryGetTableBody(sql, out var bodyMatch) ? bodyMatch!.Groups["body"].Value : sql;

        return InlineTableConstraintRegex.Matches(body).Any(match => knownNames.Contains(match.Groups["name"].Value))
            || InlineDefaultConstraintRegex.Matches(body).Any(match => knownNames.Contains(match.Groups["name"].Value));
    }

    /// <summary>
    /// Locates the <c>CREATE TABLE (...)</c> body, rejecting a match whose captured body text itself
    /// contains <c>ALTER TABLE</c> or a second <c>CREATE TABLE</c> — a reliable sign that
    /// <see cref="TableBodyRegex"/>'s "first <c>);</c> after the opening paren" heuristic ran past the
    /// table's real closing paren (e.g. a file missing the semicolon <see cref="TableBodyRegex"/>
    /// expects) and swallowed unrelated batches into what it thinks is the column list. Proceeding to
    /// remove "duplicate" clauses out of that over-matched body risks corrupting the genuine batches it
    /// swallowed instead of just failing to fix the real duplicate — worse than a no-op, so this is
    /// treated the same as not finding a body at all.
    /// </summary>
    private static bool TryGetTableBody(string sql, out Match? bodyMatch)
    {
        var match = TableBodyRegex.Match(sql);
        if (!match.Success)
        {
            bodyMatch = null;
            return false;
        }

        var body = match.Groups["body"].Value;
        if (body.Contains("ALTER TABLE", StringComparison.Ordinal) || body.Contains("CREATE TABLE", StringComparison.Ordinal))
        {
            bodyMatch = null;
            return false;
        }

        bodyMatch = match;
        return true;
    }

    private static string RemoveInlineDuplicatesFromBody(string sql, Match bodyMatch, HashSet<string> knownNames)
    {
        var body = bodyMatch.Groups["body"];
        var bodyStart = body.Index;
        var bodyText = body.Value;

        var spans = new List<(int Start, int End)>();

        foreach (Match match in InlineTableConstraintRegex.Matches(bodyText))
        {
            if (!knownNames.Contains(match.Groups["name"].Value))
            {
                continue;
            }

            var absoluteStart = bodyStart + match.Index;
            var absoluteEnd = absoluteStart + match.Length;
            spans.Add(ExpandListItemRemovalSpan(sql, absoluteStart, absoluteEnd));
        }

        foreach (Match match in InlineDefaultConstraintRegex.Matches(bodyText))
        {
            if (!knownNames.Contains(match.Groups["name"].Value))
            {
                continue;
            }

            var absoluteStart = bodyStart + match.Index;
            spans.Add((absoluteStart, absoluteStart + match.Length));
        }

        return spans.Count == 0 ? sql : RemoveSpans(sql, spans);
    }

    /// <summary>
    /// A table-level constraint clause is one comma-separated item in <c>CREATE TABLE</c>'s column
    /// list; removing it cleanly also means removing the comma that used to separate it from its
    /// neighbor. Always consumes the comma that <i>precedes</i> the clause, never the one that follows
    /// — a constraint clause is never the first item in DacFx's own output (there is always at least one
    /// column before it), so a preceding comma always exists, and every item's "preceding comma" is a
    /// distinct character position from every other item's (the gap immediately before it in the
    /// original list). That makes spans for any subset of targeted items — adjacent or not, however many
    /// — provably non-overlapping without needing to know which of their neighbors are also being
    /// removed. (An earlier version of this method tried to prefer the <i>following</i> comma instead,
    /// which meant two adjacent removed items disagreed about who owned the comma between them — see
    /// #202's review discussion, caught by <see cref="RemoveDuplicateInlineConstraints"/>'s own
    /// post-condition check rather than shipped silently.)
    /// </summary>
    private static (int Start, int End) ExpandListItemRemovalSpan(string sql, int matchStart, int matchEnd)
    {
        var beforeWhitespace = matchStart;
        while (beforeWhitespace > 0 && char.IsWhiteSpace(sql[beforeWhitespace - 1]))
        {
            beforeWhitespace--;
        }

        if (beforeWhitespace > 0 && sql[beforeWhitespace - 1] == ',')
        {
            return (beforeWhitespace - 1, matchEnd);
        }

        // No preceding comma: this constraint is the first (or only) item in the list. Fall back to
        // consuming a following comma instead, so removal still leaves a syntactically valid list.
        var afterWhitespace = matchEnd;
        while (afterWhitespace < sql.Length && char.IsWhiteSpace(sql[afterWhitespace]))
        {
            afterWhitespace++;
        }

        return afterWhitespace < sql.Length && sql[afterWhitespace] == ','
            ? (matchStart, afterWhitespace + 1)
            : (matchStart, matchEnd);
    }

    private static string RemoveSpans(string sql, List<(int Start, int End)> spans)
    {
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        var builder = new StringBuilder(sql.Length);
        var cursor = 0;
        foreach (var (start, end) in spans)
        {
            if (start < cursor)
            {
                // Overlapping spans should never happen for well-formed DacFx output; skip rather than
                // corrupt the text — RemoveDuplicateInlineConstraints' post-condition check will still
                // catch the leftover duplicate this leaves behind and fail loudly.
                continue;
            }

            builder.Append(sql, cursor, start - cursor);
            cursor = end;
        }

        builder.Append(sql, cursor, sql.Length - cursor);
        return builder.ToString();
    }
}
