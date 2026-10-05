using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Plan mode: a fourth tool-approval mode alongside ask, auto and config.
///
/// While plan mode is active the model may read files, search the web, inspect
/// windows and query memory/skills, but it may not change anything. The single
/// exception is the plan file itself, which the model writes and then reports
/// back to the user. Enforcement is a path check in ToolRegistry.ExecuteToolCall
/// that runs before the tool process starts, so a denied write never reaches the
/// filesystem regardless of what the model asked for.
/// </summary>
public static class PlanMode
{
    /// <summary>Tool-approval mode name for plan mode.</summary>
    public const string ModePlan = "plan";

    /// <summary>File name of the plan, relative to the plans directory.</summary>
    public const string PlanFileName = "plan.md";

    /// <summary>Subdirectory holding the plan, relative to the executable.</summary>
    public const string PlansDirectoryName = "plans";

    /// <summary>
    /// Tools that only read. Everything not listed here and not writing the plan
    /// file is refused in plan mode.
    /// </summary>
    private static readonly string[] ReadOnlyTools =
    {
        // FileTools
        "read_file",
        "list_directory",
        "grep_search",
        // WebTools
        "read_website",
        "run_web_search",
        // SearchTools
        "everything_search",
        // DesktopTools: inspection only, never navigate / set_control_text / send_keys
        "list_windows",
        "list_controls",
        "screenshot",
        // MemoryTools
        "recall_memory",
        "list_memories",
        "search_memories",
        // SkillTools
        "view_skill",
    };

    /// <summary>
    /// Tools that write the plan file. Their target path is compared against
    /// <see cref="GetPlanFilePath"/>; any other destination is refused.
    /// </summary>
    private static readonly string[] PlanWriterTools =
    {
        "write_file",
        "edit_file",
    };

    /// <summary>Argument name carrying the target path for the plan writer tools.</summary>
    public const string PlanWriterPathArgument = "filename";

    /// <summary>Absolute path of the plan file, next to the executable.</summary>
    public static string GetPlanFilePath()
    {
        return Path.Combine(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PlansDirectoryName),
            PlanFileName);
    }

    /// <summary>True when the tool only reads and is safe in plan mode.</summary>
    public static bool IsReadOnlyTool(string toolName)
    {
        return Contains(ReadOnlyTools, toolName);
    }

    /// <summary>True when the tool may write, but only to the plan file.</summary>
    public static bool IsPlanWriterTool(string toolName)
    {
        return Contains(PlanWriterTools, toolName);
    }

    /// <summary>
    /// True when plan mode must refuse the call outright: the tool is neither
    /// read-only nor a plan writer.
    /// </summary>
    public static bool IsForbiddenTool(string toolName)
    {
        return !IsReadOnlyTool(toolName) && !IsPlanWriterTool(toolName);
    }

    /// <summary>
    /// True when a plan writer targets a path other than the plan file. An empty
    /// or absent target counts as an escape attempt, because the plan writer
    /// tools always require an explicit filename.
    /// </summary>
    /// <param name="toolName">Tool being called.</param>
    /// <param name="targetPath">Raw target path straight from the tool arguments.</param>
    /// <param name="toolWorkingDirectory">
    /// Working directory the tool process will use. FileTools resolves relative
    /// paths against it (ProcessStartInfo.WorkingDirectory is the tool's own
    /// folder), so the check must resolve the same way or "..\\..\\plan.md"
    /// would slip past.
    /// </param>
    public static bool IsPlanWriteOutsidePlanFile(string toolName, string targetPath, string toolWorkingDirectory)
    {
        if (!IsPlanWriterTool(toolName))
            return false;

        if (string.IsNullOrEmpty(targetPath))
            return true;

        string resolved = ResolveToolPath(targetPath, toolWorkingDirectory);
        return !PathsEqual(resolved, GetPlanFilePath());
    }

    /// <summary>
    /// Resolves a model-supplied path the way the file tools would: environment
    /// variables first, then relative paths against the tool's working directory.
    /// </summary>
    public static string ResolveToolPath(string targetPath, string toolWorkingDirectory)
    {
        if (string.IsNullOrEmpty(targetPath))
            return targetPath;

        string expanded = Environment.ExpandEnvironmentVariables(targetPath);

        try
        {
            if (Path.IsPathRooted(expanded))
                return Path.GetFullPath(expanded);

            string baseDirectory = string.IsNullOrEmpty(toolWorkingDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : toolWorkingDirectory;
            return Path.GetFullPath(Path.Combine(baseDirectory, expanded));
        }
        catch (Exception)
        {
            return expanded;
        }
    }

    /// <summary>
    /// Refusal text handed back to the model as the tool result, matching the
    /// wording OpenCode uses for a denied permission.
    /// </summary>
    public static string FormatRefusal(string toolName, string reason)
    {
        return "error: " + toolName + " is not available in plan mode. " + reason
            + " Plan mode is read-only: you may read files, search the web and inspect"
            + " windows, and you may only write to " + PlanFileName
            + " in the " + PlansDirectoryName + " directory. Present the finished plan to the"
            + " user and stop this turn; they will approve it before any changes are made.";
    }

    /// <summary>
    /// Synthetic user message appended to the conversation when plan mode is turned
    /// on. OpenCode uses the same trick (a synthetic text part on mode change),
    /// because the system prompt is rebuilt every turn and therefore carries no
    /// record of when the mode last changed: without an in-history marker the model
    /// has to guess what it was doing a turn ago.
    /// </summary>
    public static string EnterPlanMessage()
    {
        return
            "<system-reminder>\r\n"
            + "You have switched to PLAN MODE. You are now in READ-ONLY mode: you may read files,"
            + " search the web, inspect windows, and query memory or skills, but you may only write"
            + " to " + GetPlanFilePath() + ". Present the plan to the user and stop this turn; they"
            + " will approve it before any changes are made.\r\n"
            + "</system-reminder>";
    }

    /// <summary>Synthetic user message appended when plan mode is turned off.</summary>
    public static string ExitPlanMessage()
    {
        return
            "<system-reminder>\r\n"
            + "You have switched out of PLAN MODE. You are no longer in read-only mode: you may now"
            + " modify files and run commands as normal.\r\n"
            + "</system-reminder>";
    }

    /// <summary>Instruction block appended to the system prompt while plan mode is on.</summary>
    public static string BuildPromptBlock()
    {
        return
            "PLAN MODE IS ACTIVE. You are in a read-only phase.\r\n"
            + "\r\n"
            + "Investigate and analyse, but change nothing. Concretely:\r\n"
            + "- Reading files, searching the web, inspecting windows and querying memory or\r\n"
            + "  skills are allowed and encouraged.\r\n"
            + "- Do not attempt to modify, delete, move or create any file, and do not run shell\r\n"
            + "  commands or scripts. Those tools are unavailable to you in this mode and an\r\n"
            + "  attempt will be refused.\r\n"
            + "- Your one permitted write is the plan file at " + GetPlanFilePath() + ".\r\n"
            + "  Write the plan there.\r\n"
            + "\r\n"
            + "A good plan states the goal, the files or components involved, the concrete steps\r\n"
            + "in the order they must happen, and the risks or unknowns. Ground every step in\r\n"
            + "what you actually read: if you could not verify something, say so.\r\n"
            + "\r\n"
            + "Finish the turn by presenting the plan to the user. Do not start implementing it.\r\n"
            + "The user reads the plan and approves it before any change is made, so a plan that\r\n"
            + "leaves out a step, a file path or a risk costs a whole extra round trip.";
    }

    private static bool Contains(string[] items, string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        for (int i = 0; i < items.Length; i++)
        {
            if (string.Equals(items[i], value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Compares two paths ignoring case and separator style, so a model asking for
    /// "PLAN.md" or "plans\\plan.md" is not refused for spelling.
    /// </summary>
    private static bool PathsEqual(string left, string right)
    {
        try
        {
            string normalizedLeft = Path.GetFullPath(left);
            string normalizedRight = Path.GetFullPath(right);
            return string.Equals(
                normalizedLeft.TrimEnd('\\', '/'),
                normalizedRight.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

}