/// <summary>
/// Shared default constants used by both CLI and GUI projects.
/// </summary>
public static class AppConstants
{
    public const int DefaultChatFontSize = 14;
    public const string DefaultAssistantName = "LLM";

    /// <summary>Tools that ask for manual approval when toolsRequiringApproval is unset.</summary>
    public const string DefaultToolsRequiringApproval = "run_shell_command,download_file";

    /// <summary>Tool approval mode used when approvalmode is unset: ask, auto or config.</summary>
    public const string DefaultApprovalMode = "config";

    /// <summary>Default comma-separated knowledge-file extensions when ragAllowedExtensions is unset.</summary>
    public const string DefaultRagAllowedExtensions =
        ".md,.markdown,.txt,.rst,"
        + ".csv,.json,.xml,.yaml,.yml,.toml,.ini,"
        + ".html,.htm,.css,"
        + ".cs,.py,.js,.ts,.tsx,.jsx,.java,.go,.rs,"
        + ".c,.h,.cpp,.hpp,.rb,.php,.sql,.sh,.ps1,.bat";
}
