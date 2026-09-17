using System.Globalization;

/// <summary>
/// Named-pipe control channel: the GUI connects to SimpleLLMChat.Control.{cliPid}
/// and sends command lines. Commands (UTF-8, one per line):
///   STOP — abort the current generation.
///   STOPTOOL — cancel the currently running tool call.
/// </summary>
public static class ControlPipe
{
    public const string PipeNamePrefix = "SimpleLLMChat.Control.";

    public const string StopCommand = "STOP";
    public const string StopToolCommand = "STOPTOOL";

    public static string GetPipeName(int processId)
    {
        return PipeNamePrefix + processId.ToString(CultureInfo.InvariantCulture);
    }
}