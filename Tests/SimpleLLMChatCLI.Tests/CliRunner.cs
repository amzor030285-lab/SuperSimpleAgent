using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace SimpleLLMChatCLI.Tests
{
    /// <summary>
    /// Launches SimpleLLMChatCLI --no-banners with stdin kept open so the test
    /// can send a user message and then request a stop via the GUI control pipe
    /// (SimpleLLMChat.Control.{pid}) while the model is still generating.
    /// </summary>
    internal sealed class CliRunner : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _stdout = new StringBuilder();
        private readonly StringBuilder _stderr = new StringBuilder();
        private readonly object _lock = new object();
        private Thread _outThread;
        private Thread _errThread;

        public int Id { get { return _process.Id; } }
        public bool HasExited { get { try { return _process.HasExited; } catch { return true; } } }

        public string Stdout
        {
            get { lock (_lock) return _stdout.ToString(); }
        }

        public string Stderr
        {
            get { lock (_lock) return _stderr.ToString(); }
        }

        private sealed class StreamState
        {
            public Stream Stream;
            public StringBuilder Target;
            public object Lock;
        }

        public CliRunner(string exePath, string workingDirectory)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--no-banners",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            _process = Process.Start(psi);

            _outThread = new Thread(ReadStream)
            {
                IsBackground = true,
                Name = "cli-runner-stdout"
            };
            _errThread = new Thread(ReadStream)
            {
                IsBackground = true,
                Name = "cli-runner-stderr"
            };
            _outThread.Start(new StreamState { Stream = _process.StandardOutput.BaseStream, Target = _stdout, Lock = _lock });
            _errThread.Start(new StreamState { Stream = _process.StandardError.BaseStream, Target = _stderr, Lock = _lock });
        }

        /// <summary>
        /// Sends a line of UTF-8 text (the newline is appended automatically),
        /// matching what the GUI writes via ProcessHandler.SendInput.
        /// </summary>
        public void WriteLine(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
            _process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
            _process.StandardInput.BaseStream.Flush();
        }

        /// <summary>
        /// Writes raw UTF-8 bytes followed by a newline to the CLI's stdin.
        /// </summary>
        public void WriteInput(byte[] inputUtf8)
        {
            _process.StandardInput.BaseStream.Write(inputUtf8, 0, inputUtf8.Length);
            _process.StandardInput.BaseStream.Flush();
        }

        /// <summary>
        /// Sends the STOP command to the CLI's control pipe.
        /// Returns false if the pipe was unreachable (e.g. CLI already exited).
        /// </summary>
        public bool SendStop(int timeoutMs)
        {
            string pipeName = "SimpleLLMChat.Control." + _process.Id.ToString(CultureInfo.InvariantCulture);
            Stopwatch sw = Stopwatch.StartNew();
            int attempt = 0;

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
                    {
                        pipe.Connect(Math.Min(500, (int)(timeoutMs - sw.ElapsedMilliseconds)));
                        byte[] command = Encoding.UTF8.GetBytes("STOP\n");
                        pipe.Write(command, 0, command.Length);
                        pipe.Flush();
                    }
                    return true;
                }
                catch
                {
                    attempt++;
                    Thread.Sleep(50);
                }
            }
            return false;
        }

        /// <summary>
        /// Polls Stdout until <paramref name="needle"/> appears or the timeout expires.
        /// </summary>
        public static void WaitForText(CliRunner cli, string needle, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cli.Stdout.IndexOf(needle, StringComparison.Ordinal) >= 0)
                    return;
                Thread.Sleep(50);
            }
            throw new TestFailureException(
                "Timeout (" + timeoutMs + " ms) waiting for text: " + needle + "\n" +
                "Stdout so far:\n" + cli.Stdout);
        }

        /// <summary>
        /// Polls Stdout until <paramref name="needle"/> appears at an index greater than
        /// the last occurrence of <paramref name="after"/>, or the timeout expires.
        /// </summary>
        public static void WaitForTextAfter(CliRunner cli, string after, string needle, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                string text = cli.Stdout;
                int anchor = text.LastIndexOf(after, StringComparison.Ordinal);
                if (anchor >= 0 && text.IndexOf(needle, anchor + after.Length, StringComparison.Ordinal) >= 0)
                    return;
                Thread.Sleep(50);
            }
            throw new TestFailureException(
                "Timeout (" + timeoutMs + " ms) waiting for \"" + needle + "\" after \"" + after + "\"\n" +
                "Stdout so far:\n" + cli.Stdout);
        }

        public void Dispose()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                    _process.Kill();
            }
            catch { }
            try
            {
                if (_process != null)
                    _process.WaitForExit(2000);
            }
            catch { }
            try
            {
                if (_process != null)
                    _process.Dispose();
            }
            catch { }
        }

        private void ReadStream(object state)
        {
            StreamState s = (StreamState)state;
            try
            {
                byte[] buffer = new byte[1024];
                Decoder decoder = Encoding.UTF8.GetDecoder();
                while (true)
                {
                    int read = s.Stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;
                    int charCount = decoder.GetCharCount(buffer, 0, read);
                    char[] chars = new char[charCount];
                    decoder.GetChars(buffer, 0, read, chars, 0);
                    lock (s.Lock)
                    {
                        s.Target.Append(chars);
                    }
                }
            }
            catch
            {
            }
        }
    }
}