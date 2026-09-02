namespace FoundryGate.Cli.Commands.Db.Compare;

/// <summary>
/// Small regex fragments for parsing this repo's SSDT-style <c>dbo/Tables/*.sql</c> scripts. Shared
/// between <see cref="SqlTableFileNormalizer"/> (which rewrites them) and
/// <c>FoundryGate.Tests.Predeployment.Data.Conventions.SchemaParityTests</c> (which parses them
/// read-only) — both solve the same "match a nested-paren SQL clause" problem, and letting the pattern
/// drift between the two would mean the normalizer and the parity test quietly disagree about what a
/// column/constraint clause looks like. <c>FoundryGate.Tests.Predeployment</c> already has a
/// <c>ProjectReference</c> on <c>FoundryGate.Cli</c>, so referencing this constant directly (rather than
/// keeping two textually-identical copies in sync by hand) is a same-solution, not a duplicate.
/// </summary>
public static class SqlPatterns
{
    /// <summary>
    /// A parenthesised expression with arbitrarily nested parens (.NET balancing groups), e.g. the
    /// column list of a <c>PRIMARY KEY (...)</c>/<c>FOREIGN KEY (...)</c>/<c>UNIQUE (...)</c>/
    /// <c>CHECK (...)</c> clause, or the <c>((1))</c>/<c>(getutcdate())</c> DacFx emits for default
    /// constraints.
    /// </summary>
    public const string BalancedParens = @"\((?>[^()]+|\((?<depth>)|\)(?<-depth>))*(?(depth)(?!))\)";
}
