namespace FoundryGate.Cli.Helpers;

/// <summary>Console yes/no confirmation for commands that mutate Azure resources.</summary>
internal static class ConsolePrompts
{
    /// <summary>
    /// Asks <paramref name="question"/> and returns the answer (Enter = yes). Throws instead of hanging when
    /// stdin is not interactive — an unattended caller (CI) must pass <c>--yes</c> deliberately.
    /// </summary>
    public static bool Confirm(string question)
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException("stdin is not a terminal, so the confirmation prompt cannot be shown. Pass --yes to proceed without confirming.");
        }

        Console.Write($"{question} [Y/n] ");
        var answer = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(answer) || answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
