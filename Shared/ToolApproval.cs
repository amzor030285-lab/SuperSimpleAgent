using System;

/// <summary>
/// Shared tool-approval prompt text used by CLI output and GUI parsing.
/// </summary>
public static class ToolApproval
{
    public const string RunToolPrefix = "Run tool: ";
    public const string ArgumentsPrefix = "With arguments:";
    public const string ApprovalPrompt = "Approve? (Y/N): ";

    public static string UnescapeArguments(string arguments)
    {
        if (string.IsNullOrEmpty(arguments))
            return string.Empty;

        // Collapse literal backslashes first so path fragments like \test
        // are not mistaken for \t / \n / \r escapes.
        return arguments
            .Replace("\\\\", "\u0000")
            .Replace("\\n", "\n")
            .Replace("\\r", "\r")
            .Replace("\\t", "\t")
            .Replace("\\\"", "\"")
            .Replace("\\'", "'")
            .Replace("\u0000", "\\");
    }

    public static string FormatApprovalMessage(string toolName, string arguments)
    {
        string formattedArguments = UnescapeArguments(arguments);
        return RunToolPrefix + toolName + "\n" + formattedArguments;
    }

    public static bool TryParseApprovalPrompt(string text, out string toolName, out string arguments)
    {
        toolName = null;
        arguments = null;

        if (string.IsNullOrEmpty(text))
            return false;

        int promptIndex = text.LastIndexOf(ApprovalPrompt, StringComparison.Ordinal);
        if (promptIndex < 0)
            return false;

        string block = text.Substring(0, promptIndex);
        int runIndex = block.LastIndexOf(RunToolPrefix, StringComparison.Ordinal);
        if (runIndex < 0)
            return false;

        int nameStart = runIndex + RunToolPrefix.Length;
        int nameEnd = block.IndexOf('\n', nameStart);
        if (nameEnd < 0)
            return false;

        toolName = block.Substring(nameStart, nameEnd - nameStart).Trim();

        string rest = block.Substring(nameEnd + 1);

        // Older format inserted a "With arguments:" label before the payload.
        if (rest.StartsWith(ArgumentsPrefix, StringComparison.Ordinal))
        {
            int argsLineEnd = rest.IndexOf('\n');
            if (argsLineEnd < 0)
                return false;
            rest = rest.Substring(argsLineEnd + 1);
        }

        arguments = rest.TrimEnd('\r', '\n', ' ');
        return true;
    }

    public const string ModeAsk = "ask";
    public const string ModeAuto = "auto";
    public const string ModeConfig = "config";

    /// <summary>
    /// Validates a tool-approval mode name: ask = confirm every tool call,
    /// auto = never confirm, config = confirm only the toolsRequiringApproval tools.
    /// </summary>
    public static bool IsValidMode(string mode)
    {
        return string.Equals(mode, ModeAsk, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, ModeAuto, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, ModeConfig, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Canonical lowercase mode name, or null when the input is not a mode.</summary>
    public static string NormalizeMode(string mode)
    {
        return IsValidMode(mode) ? mode.ToLowerInvariant() : null;
    }
}
