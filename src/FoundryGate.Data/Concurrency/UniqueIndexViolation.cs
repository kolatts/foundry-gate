namespace FoundryGate.Data.Concurrency;

/// <summary>
/// The one place "did the database reject this insert because of <em>that</em> unique index?" is
/// answered (#204 review). A read-then-write pre-check is only ever the fast path; the index behind it
/// is the guard, and turning its violation into the same <c>409</c> the serial path gives is what makes
/// the two agree under concurrency.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why identifiers, not a re-query.</b> Matching the identifier the provider names in its error is
/// what keeps the answer honest about the index that actually rejected the row: on an
/// accent-insensitive database <c>IX_Groups_Name</c> rejects "Résumé" against "Resume", and a
/// <c>LOWER(Name)</c> re-query would not have found the collision — turning a conflict into a 500.
/// Identifiers are also unaffected by a localized server message, which the surrounding prose is not.
/// </para>
/// <para>
/// <b>Two markers per index</b>, because the two providers name different things:
/// SQL Server names the index ("Cannot insert duplicate key row … with unique index
/// 'IX_Groups_Name'"), SQLite names the columns ("UNIQUE constraint failed: Groups.Name"). Pass both,
/// so the same <c>catch</c> filter works in the test harness and in production.
/// </para>
/// <para>
/// <b>Why it lives in Data</b>, next to <see cref="CommitToken"/>: the markers describe database
/// objects, and every host that saves references this project. It started as two byte-identical copies
/// in <c>GroupService</c> and <c>QuotaRequestService</c>, which is exactly the duplication
/// <see cref="CommitToken"/> was extracted to end.
/// </para>
/// </remarks>
public static class UniqueIndexViolation
{
    /// <summary>
    /// Whether <paramref name="exception"/> — or anything in its inner-exception chain, which is where
    /// the provider's own message lives under EF's <c>DbUpdateException</c> — names one of
    /// <paramref name="markers"/>.
    /// </summary>
    /// <param name="exception">The exception a failed <c>SaveChangesAsync</c> threw.</param>
    /// <param name="markers">Index and column identifiers that identify one index across providers.</param>
    public static bool Mentions(Exception exception, params string[] markers)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(markers);

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (Array.Exists(markers, marker => current.Message.Contains(marker, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}
