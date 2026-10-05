using System;

/// <summary>
/// Shared tool-approval prompt text used by CLI output and GUI parsing.
/// </summary>
public static class ToolApproval
{
    public const string RunToolPrefix = "Run tool: ";
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

        arguments = block.Substring(nameEnd + 1).TrimEnd('\r', '\n', ' ');
        return true;
    }

    public const string ModeAsk = "ask";
    public const string ModeAuto = "auto";
    public const string ModeConfig = "config";

    /// <summary>
    /// The four tool-approval modes in GUI cycle order. Plan mode is last, so
    /// leaving plan lands on ask - the most conservative approval mode.
    /// </summary>
    private static readonly string[] ModeCycle =
    {
        ModeAsk,
        ModeAuto,
        ModeConfig,
        PlanMode.ModePlan,
    };

    /// <summary>
    /// Validates a tool-approval mode name: ask = confirm every tool call,
    /// auto = never confirm, config = confirm only the toolsRequiringApproval tools,
    /// plan = read-only except for the plan file.
    /// </summary>
    public static bool IsValidMode(string mode)
    {
        return string.Equals(mode, ModeAsk, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, ModeAuto, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, ModeConfig, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, PlanMode.ModePlan, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The mode following <paramref name="mode"/> in the GUI cycle.</summary>
    public static string NextModeInCycle(string mode)
    {
        string normalized = NormalizeMode(mode);
        if (normalized == null)
            return ModeAsk;

        for (int i = 0; i < ModeCycle.Length; i++)
        {
            if (string.Equals(ModeCycle[i], normalized, StringComparison.Ordinal))
                return ModeCycle[(i + 1) % ModeCycle.Length];
        }

        return ModeAsk;
    }

    /// <summary>True while the read-only plan mode is selected.</summary>
    public static bool IsPlanMode(string mode)
    {
        return string.Equals(mode, PlanMode.ModePlan, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Canonical lowercase mode name, or null when the input is not a mode.</summary>
    public static string NormalizeMode(string mode)
    {
        return IsValidMode(mode) ? mode.ToLowerInvariant() : null;
    }
}
