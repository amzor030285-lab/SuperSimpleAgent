using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace SimpleLLMChatGUI
{
    /// <summary>
    /// Sends stop requests to a CLI's control pipe (SimpleLLMChat.Control.{cliPid}).
    /// Connects with a few retries so a stop still arrives if the server is busy
    /// re-accepting a connection; fails fast if the CLI is gone.
    /// </summary>
    internal static class ControlPipeClient
    {
        public static bool SendStop(int processId)
        {
            string pipeName = ControlPipe.GetPipeName(processId);

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
                    {
                        pipe.Connect(500);

                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false)))
                        {
                            writer.WriteLine(ControlPipe.StopCommand);
                            writer.Flush();
                        }
                    }
                    return true;
                }
                catch
                {
                    Thread.Sleep(50);
                }
            }

            return false;
        }
    }
}