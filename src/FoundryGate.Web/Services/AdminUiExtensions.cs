using FoundryGate.Domain.Common;
using FoundryGate.Web.Shared;
using MudBlazor;

namespace FoundryGate.Web.Services;

/// <summary>
/// The two shapes every admin page repeated: "ask before you do it" and "turn one paged API
/// result into a grid page". Both were copy-pasted three or more times, comment included.
/// </summary>
public static class AdminUiExtensions
{
    /// <summary>
    /// Shows <see cref="ConfirmDialog"/> and answers whether the caller went through with it.
    /// A dismissed dialog (escape, backdrop, Cancel) is a "no", never a silent yes.
    /// </summary>
    /// <param name="dialogService">MudBlazor's dialog service.</param>
    /// <param name="title">The dialog's title bar — the action, e.g. "Delete group".</param>
    /// <param name="message">The question, e.g. "Delete Platform?".</param>
    /// <param name="detail">What will actually happen. Say the consequence before sending it, not after.</param>
    /// <param name="confirmText">Label on the confirming button — the verb, not "OK".</param>
    /// <param name="confirmColor"><see cref="Color.Error"/> for anything destructive.</param>
    public static async Task<bool> ConfirmAsync(
        this IDialogService dialogService,
        string title,
        string message,
        string detail,
        string confirmText,
        Color confirmColor)
    {
        ArgumentNullException.ThrowIfNull(dialogService);

        var parameters = new DialogParameters<ConfirmDialog>
        {
            { x => x.Message, message },
            { x => x.Detail, detail },
            { x => x.ConfirmText, confirmText },
            { x => x.ConfirmColor, confirmColor },
        };

        var dialog = await dialogService.ShowAsync<ConfirmDialog>(title, parameters);
        var result = await dialog.Result;
        return result is { Canceled: false };
    }

    /// <summary>
    /// Maps one paged API result onto <see cref="MudDataGrid{T}"/>'s <c>ServerData</c> contract.
    /// </summary>
    /// <remarks>
    /// <c>ServerData</c> is a callback from the grid rather than a lifecycle method, so a page that
    /// swaps itself for <c>AccessDenied</c> has to ask for its own re-render — hence
    /// <paramref name="onForbidden"/> rather than a bool the caller reads afterwards. An empty grid
    /// is the honest answer to every failure here; the page says what went wrong through the
    /// snackbar, and its own <c>NoRecordsContent</c> covers the genuinely-empty case.
    /// </remarks>
    /// <param name="result">What the client returned.</param>
    /// <param name="onForbidden">Invoked on a 403 — the page renders <c>AccessDenied</c> and re-renders itself.</param>
    /// <param name="snackbar">Where any other failure is reported.</param>
    /// <param name="failureMessage">Fallback wording when the API sent no message of its own.</param>
    public static GridData<T> ToGridData<T>(
        this ApiCallResult<PagedResult<T>> result,
        Action onForbidden,
        ISnackbar snackbar,
        string failureMessage)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onForbidden);
        ArgumentNullException.ThrowIfNull(snackbar);

        if (result.Status == ApiCallStatus.Forbidden)
        {
            onForbidden();
            return new GridData<T> { Items = [], TotalItems = 0 };
        }

        if (!result.IsSuccess || result.Value is null)
        {
            snackbar.Add(result.Message ?? failureMessage, Severity.Error);
            return new GridData<T> { Items = [], TotalItems = 0 };
        }

        return new GridData<T> { Items = result.Value.Items, TotalItems = result.Value.TotalCount };
    }

    /// <summary>
    /// Reloads a grid from page one after a filter change, with exactly one fetch.
    /// </summary>
    /// <remarks>
    /// <c>NavigateTo</c> reloads server data itself when <c>ServerData</c> is set, so calling it and
    /// then <c>ReloadServerData</c> fires two identical requests whenever the grid was not already
    /// on page one — two responses racing to render the same grid.
    /// </remarks>
    public static Task ReloadFromFirstPageAsync<T>(this MudDataGrid<T> grid)
    {
        ArgumentNullException.ThrowIfNull(grid);

        if (grid.CurrentPage != 0)
        {
            grid.NavigateTo(Page.First);
            return Task.CompletedTask;
        }

        return grid.ReloadServerData();
    }
}
