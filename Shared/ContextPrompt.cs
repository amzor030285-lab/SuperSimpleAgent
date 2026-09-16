using System;

/// <summary>
/// Shared "context usage high" ask prompt text used by CLI output and GUI parsing.
/// When the estimated context usage crosses AskUserThreshold the CLI blocks on stdin
/// with a truncated / summarized / continue-anyway question. The GUI recognizes the
/// block the same way it recognizes tool approval prompts, shows a matchbox dialog
/// and replies with a single key.
/// </summary>
public static class ContextPrompt
{
    public const string NoticePrefix = "[Context usage high -";
    public const string AskPrompt = "[Context usage high - truncate/summarize/continue? (T/S/C)]: ";

    public const char TruncateKey = 'T';
    public const char SummarizeKey = 'S';
    public const char ContinueKey = 'C';

    /// <summary>
    /// First line of the ask block: reports the estimated usage percentage.
    /// The GUI holds the whole block back so it is not rendered to the chat log.
    /// </summary>
    public static string FormatNotice(double usagePercentage)
    {
        return NoticePrefix + " an estimated " + usagePercentage.ToString("F1")
            + "% of the context window is in use]\r\n"
            + "  T = Truncate oldest messages\r\n"
            + "  S = Summarize into a compact summary\r\n"
            + "  C = Continue anyway (no changes)\r\n";
    }

    /// <summary>
    /// Parses a single-line answer from prompt input. Returns the chosen action
    /// ("truncate", "summarize", "continue") or null when input is empty / garbage.
    /// </summary>
    public static string TryParseAnswer(string line)
    {
        if (string.IsNullOrEmpty(line))
            return null;

        char key = char.ToLowerInvariant(line[0]);
        switch (key)
        {
            case 't': return "truncate";
            case 's': return "summarize";
            case 'c': return "continue";
            default: return null;
        }
    }
}