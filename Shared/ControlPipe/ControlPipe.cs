using System.Globalization;

/// <summary>
/// Named-pipe control channel: the GUI connects to SimpleLLMChat.Control.{cliPid}
/// and sends command lines. Commands (UTF-8, one per line):
///   STOP — abort the current generation.
/// </summary>
public static class ControlPipe
{
    public const string PipeNamePrefix = "SimpleLLMChat.Control.";

    public const string StopCommand = "STOP";

    public static string GetPipeName(int processId)
    {
        return PipeNamePrefix + processId.ToString(CultureInfo.InvariantCulture);
    }
}