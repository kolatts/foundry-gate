using Azure;
using Azure.Identity;

namespace FoundryGate.Cli.Helpers;

/// <summary>
/// The shared shape of "this went wrong in a way the operator can act on": which exceptions a command's
/// top-level filter turns into a one-line <c>&lt;command&gt; failed: ...</c> message plus exit code 1,
/// and how each of them is worded. Anything not listed here is a bug and keeps its stack trace.
/// </summary>
public static class CliErrors
{
    /// <summary>
    /// True for the failures a correctly-written command can still hit at runtime: bad input
    /// (<see cref="ArgumentException"/>), an unmet precondition (<see cref="InvalidOperationException"/>),
    /// an ARM refusal (<see cref="RequestFailedException"/>) and — the single most likely one in practice —
    /// no or expired Azure credential (<see cref="AuthenticationFailedException"/>, which derives straight
    /// from <see cref="Exception"/> rather than <see cref="RequestFailedException"/>, so it needs naming).
    /// </summary>
    public static bool IsExpected(Exception exception) =>
        exception is InvalidOperationException or ArgumentException or RequestFailedException or AuthenticationFailedException;

    /// <summary>The printable message, with sign-in guidance appended when the failure was the credential chain.</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is AuthenticationFailedException
            ? $"{exception.Message} No usable Azure credential was found — run `az login` locally, or make sure the job ran azure/login@v2 before this step in CI."
            : exception.Message;
    }
}
