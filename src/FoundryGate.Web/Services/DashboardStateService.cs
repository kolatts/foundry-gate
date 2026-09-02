namespace FoundryGate.Web.Services;

/// <summary>
/// The one piece of dashboard state the shell needs outside the dashboard page: how many quota
/// increase requests are waiting for a reviewer, so <c>Layout/NavMenu</c> can badge the admin
/// "All Requests" link without fetching <c>GET /dashboard</c> itself (#54).
/// </summary>
/// <remarks>
/// Registered <b>scoped</b>, not singleton. In a Blazor WebAssembly host the two lifetimes are the
/// same object for the life of the tab, but scoped is what the shell means: this is per-user state
/// derived from an authorized API response, and if the app ever gains a server-rendered host a
/// singleton would leak one admin's pending count into another admin's circuit.
/// <para>
/// Publishers (the dashboard page) call <see cref="SetPendingRequestCount"/> after every load;
/// subscribers (the nav menu) handle <see cref="Changed"/> and re-render. The event fires only on
/// an actual change, so a 60-second refresh that finds the same number costs no renders.
/// </para>
/// </remarks>
public sealed class DashboardStateService
{
    /// <summary>Raised when <see cref="PendingRequestCount"/> changes to a different value.</summary>
    public event Action? Changed;

    /// <summary>
    /// Quota increase requests currently in <c>Pending</c>, as of the last dashboard load. Zero
    /// until an admin has visited <c>/dashboard</c> at least once this session — the nav badge is a
    /// nicety, not a source of truth.
    /// </summary>
    public int PendingRequestCount { get; private set; }

    /// <summary>Publishes a new pending count; raises <see cref="Changed"/> only if it actually moved.</summary>
    /// <param name="count">The count from the latest <c>GET /dashboard</c>. Negative values are treated as zero.</param>
    public void SetPendingRequestCount(int count)
    {
        var normalized = count < 0 ? 0 : count;
        if (normalized == PendingRequestCount)
        {
            return;
        }

        PendingRequestCount = normalized;
        Changed?.Invoke();
    }
}
