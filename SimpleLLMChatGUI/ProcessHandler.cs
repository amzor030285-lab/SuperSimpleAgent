using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SimpleLLMChatGUI
{
    public class ProcessHandler : IDisposable
    {
        private Process llmProcess;
        private bool disposed;
        private StringBuilder textBuffer = new StringBuilder(); // Buffer for incomplete text
        private bool _streamAtStart = true; // Nothing of the stream has been consumed yet.
        private Regex _promptLinePattern = BuildPromptLinePattern(AppConstants.DefaultAssistantName);

        public event Action<string> OutputReceived;
        public event Action<string> ErrorOccurred;
        public event Action GenerationComplete;
        public event Func<string, string, bool> ApprovalRequested;
        public event Action<int> StatusReceived;
        public event Action<string> RunningToolReceived;

        private readonly StringBuilder streamBuffer = new StringBuilder();
        private readonly char[] charBuffer = new char[256];
        private StatusPipeClient statusPipeClient;
        private StreamWriter inputWriter;

        public bool IsProcessRunning
        {
            get { return llmProcess != null && !llmProcess.HasExited; }
        }

        public ProcessHandler()
        {
        }

        /// <summary>
        /// Sets the assistant name whose "<name>:" output line is filtered from
        /// the streamed text (the GUI shows a role header instead).
        /// </summary>
        public void SetAssistantName(string name)
        {
            _promptLinePattern = BuildPromptLinePattern(
                string.IsNullOrWhiteSpace(name) ? AppConstants.DefaultAssistantName : name);
        }

        private static Regex BuildPromptLinePattern(string assistantName)
        {
            // Whole lines (or line starts) that are the CLI's own prompts:
            // "You: " before reading input and "<assistant>:" at the response start.
            return new Regex(
                @"(^|\r?\n)[ \t]*(?:You|" + Regex.Escape(assistantName) + @"):[ \t]*",
                RegexOptions.Multiline);
        }

        public bool StartProcess(string executablePath)
        {
            try
            {
                DisposeStatusPipeClient();

                if (inputWriter != null)
                {
                    inputWriter.Dispose();
                    inputWriter = null;
                }

                llmProcess = new Process();
                llmProcess.StartInfo.FileName = executablePath;
                llmProcess.StartInfo.UseShellExecute = false;
                llmProcess.StartInfo.RedirectStandardOutput = true;
                llmProcess.StartInfo.RedirectStandardInput = true;
                llmProcess.StartInfo.CreateNoWindow = true;
                llmProcess.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                llmProcess.                StartInfo.Arguments = "--no-banners";
                textBuffer.Clear();
                streamBuffer.Clear();
                _streamAtStart = true;
                llmProcess.Start();
                inputWriter = new StreamWriter(llmProcess.StandardInput.BaseStream, new UTF8Encoding(false));
                inputWriter.AutoFlush = true;

                statusPipeClient = new StatusPipeClient(llmProcess.Id);
                statusPipeClient.StatusReceived += OnStatusPipeReceived;
                statusPipeClient.IndexingStatusReceived += OnIndexingStatusPipeReceived;
                statusPipeClient.RunningToolReceived += OnRunningToolPipeReceived;
                statusPipeClient.Start();

                ReadOutputLoop();

                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke("Failed to start process: " + ex.Message);
                return false;
            }
        }

        private void OnStatusPipeReceived(int tokens)
        {
            StatusReceived?.Invoke(tokens);
        }

        private void OnIndexingStatusPipeReceived(IndexingStatusEvent status)
        {
            IndexingStatusHub.Publish(status);
        }

        private void OnRunningToolPipeReceived(string toolName)
        {
            RunningToolReceived?.Invoke(toolName);
        }

        private void DisposeStatusPipeClient()
        {
            if (statusPipeClient != null)
            {
                statusPipeClient.StatusReceived -= OnStatusPipeReceived;
                statusPipeClient.IndexingStatusReceived -= OnIndexingStatusPipeReceived;
                statusPipeClient.RunningToolReceived -= OnRunningToolPipeReceived;
                statusPipeClient.Dispose();
                statusPipeClient = null;
            }
        }

        public bool SendInput(string input)
        {
            if (llmProcess != null && !llmProcess.HasExited)
            {
                try
                {
                    // Encode multi-line text as single line: replace newlines with <<NEWLINE>> marker
                    string encodedInput = input.Replace("\r\n", "<<NEWLINE>>").Replace("\n", "<<NEWLINE>>").Replace("\r", "<<NEWLINE>>");
                    inputWriter.WriteLine(encodedInput);
                    return true;
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke("Error sending input: " + ex.Message);
                    return false;
                }
            }
            return false;
        }

        public bool SendReasoningEffort(string effort)
        {
            string command = string.IsNullOrEmpty(effort) ? "/reasoning" : "/reasoning " + effort;
            return SendInput(command);
        }

        public bool SendReload()
        {
            return SendInput("/reload");
        }

        /// <summary>
        /// Asks the CLI to abort the current generation via its control pipe.
        /// Returns false if the process is gone or the request could not be delivered.
        /// </summary>
        public bool RequestStop()
        {
            if (llmProcess == null || llmProcess.HasExited)
                return false;

            try
            {
                return ControlPipeClient.SendStop(llmProcess.Id);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke("Error stopping generation: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Asks the CLI to cancel the currently running tool via its control pipe.
        /// Returns false if the process is gone or the request could not be delivered.
        /// </summary>
        public bool RequestStopTool()
        {
            if (llmProcess == null || llmProcess.HasExited)
                return false;

            try
            {
                return ControlPipeClient.SendStopTool(llmProcess.Id);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke("Error stopping tool: " + ex.Message);
                return false;
            }
        }

        private void ReadOutputLoop()
        {
            try
            {
                Task.Factory.StartNew(() =>
                {
                    while (true)
                    {
                        int chars;
                        try
                        {
                            chars = llmProcess.StandardOutput.Read(charBuffer, 0, charBuffer.Length);
                        }
                        catch
                        {
                            return;
                        }
                        if (chars <= 0)
                            return;
                        ProcessStreamingText(new string(charBuffer, 0, chars));
                    }
                });
            }
            catch
            {
                // ignore errors
            }
        }

        private bool SendApprovalResponse(bool approved)
        {
            if (llmProcess != null && !llmProcess.HasExited && inputWriter != null)
            {
                try
                {
                    inputWriter.WriteLine(approved ? "Y" : "N");
                    return true;
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke("Error sending approval response: " + ex.Message);
                    return false;
                }
            }

            return false;
        }

        private void ProcessStreamingText(string newText)
        {
            if (string.IsNullOrEmpty(newText))
                return;

            streamBuffer.Append(newText);

            while (true)
            {
                string buffered = streamBuffer.ToString();

                // Detect the tool approval prompt.
                int approvalIndex = buffered.IndexOf(ToolApproval.ApprovalPrompt, StringComparison.Ordinal);

                if (approvalIndex < 0)
                {
                    // Hold back an in-progress tool-call block.
                    int runIndex = buffered.LastIndexOf(ToolApproval.RunToolPrefix, StringComparison.Ordinal);
                    if (runIndex >= 0)
                    {
                        if (runIndex > 0)
                            ProcessTextChunk(buffered.Substring(0, runIndex));

                        streamBuffer.Clear();
                        streamBuffer.Append(buffered.Substring(runIndex));
                        return;
                    }

                    // Hold back any partial suffix of known prompt / block markers so they
                    // are not flushed as chat text by accident.
                    int holdBack = Math.Max(
                        GetPartialSuffixLength(buffered, ToolApproval.ApprovalPrompt),
                        GetPartialSuffixLength(buffered, ToolApproval.RunToolPrefix));

                    if (holdBack > 0)
                    {
                        ProcessTextChunk(buffered.Substring(0, buffered.Length - holdBack));
                        streamBuffer.Clear();
                        streamBuffer.Append(buffered.Substring(buffered.Length - holdBack));
                    }
                    else
                    {
                        ProcessTextChunk(buffered);
                        streamBuffer.Clear();
                    }
                    return;
                }

                string marker = ToolApproval.ApprovalPrompt;
                string blockStartMarker = ToolApproval.RunToolPrefix;

                int blockStart = buffered.LastIndexOf(blockStartMarker, approvalIndex, StringComparison.Ordinal);

                if (blockStart < 0)
                {
                    ProcessTextChunk(buffered.Substring(0, approvalIndex + marker.Length));
                    streamBuffer.Clear();
                    streamBuffer.Append(buffered.Substring(approvalIndex + marker.Length));
                    continue;
                }

                if (blockStart > 0)
                    ProcessTextChunk(buffered.Substring(0, blockStart));

                string block = buffered.Substring(blockStart, approvalIndex + marker.Length - blockStart);
                HandleApprovalBlock(block);

                string remaining = buffered.Substring(approvalIndex + marker.Length);
                streamBuffer.Clear();
                if (string.IsNullOrEmpty(remaining))
                    return;

                streamBuffer.Append(remaining);
            }
        }

        private static int GetPartialSuffixLength(string text, string marker)
        {
            int maxLength = Math.Min(marker.Length - 1, text.Length);
            for (int length = maxLength; length > 0; length--)
            {
                if (text.EndsWith(marker.Substring(0, length), StringComparison.Ordinal))
                    return length;
            }

            return 0;
        }

        private void HandleApprovalBlock(string approvalBlock)
        {
            string toolName;
            string arguments;
            if (!ToolApproval.TryParseApprovalPrompt(approvalBlock, out toolName, out arguments))
            {
                ErrorOccurred?.Invoke("Failed to parse tool approval prompt.");
                SendApprovalResponse(false);
                return;
            }

            bool approved = ApprovalRequested != null && ApprovalRequested(toolName, arguments);

            SendApprovalResponse(approved);
        }

        private void ProcessTextChunk(string textChunk)
        {
            if (string.IsNullOrEmpty(textChunk))
                return;

            textBuffer.Append(textChunk);
            string text = textBuffer.ToString();
            textBuffer.Clear();

            // The CLI emits its end-of-turn prompt on a line of its own, so it
            // is always preceded by a newline: "\n\nYou: " after a reply,
            // "\nYou: " after a command line (/save, /load, ...), and at the
            // very start of the stream. A "You: " mid-line in the reply (e.g.
            // the model quoting the user) is chat content and must not end the
            // generation; a "You:" without a trailing space is never the
            // prompt. The one exception, a silent command's re-prompt (zero
            // newlines, e.g. /reasoning), arrives only while the GUI is idle
            // and is stripped from display by the prompt-line filter.
            int pos = 0;
            while (pos < text.Length)
            {
                int youIndex = FindYouPrompt(text, pos, _streamAtStart);
                if (youIndex < 0)
                    break;

                // Everything before the prompt is the end of the generation.
                if (youIndex > pos)
                    OutputText(text.Substring(pos, youIndex - pos));

                // Signal that generation is complete
                GenerationComplete?.Invoke();

                // Consume the prompt itself; the stream continues mid-line
                // (the next output is a newline, then the reply label).
                pos = youIndex + YouPromptMarker.Length;
                while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\t'))
                    pos++;
                _streamAtStart = false;
            }

            if (pos < text.Length)
            {
                // Hold back a trailing partial prompt (e.g. "\nYou:") so a
                // prompt split across reads is still seen with its anchoring
                // newline. A "You:" not followed by a space can never be the
                // prompt (the CLI always emits "You: "), so it is not held.
                int holdBack = GetPartialYouPromptSuffixLength(text, pos, _streamAtStart);
                int emitEnd = text.Length - holdBack;
                if (emitEnd > pos)
                {
                    OutputText(text.Substring(pos, emitEnd - pos));
                    _streamAtStart = false;
                }
                if (holdBack > 0)
                    textBuffer.Append(text.Substring(emitEnd));
            }
        }

        /// <summary>
        /// The CLI's end-of-turn prompt, exactly as ChatOutput writes it.
        /// </summary>
        private const string YouPromptMarker = "You: ";

        /// <summary>
        /// Finds the CLI's prompt: "You: " sitting at the very start of the
        /// stream, or preceded by a newline (the prompt is always written on a
        /// line of its own). Returns -1 when the remainder holds no such
        /// prompt. streamAtStart is true only while no stream bytes have been
        /// consumed.
        /// </summary>
        private static int FindYouPrompt(string text, int start, bool streamAtStart)
        {
            int from = start;
            while (true)
            {
                int idx = text.IndexOf(YouPromptMarker, from, StringComparison.Ordinal);
                if (idx < 0)
                    return -1;
                if ((idx == 0 && streamAtStart) || (idx > 0 && text[idx - 1] == '\n'))
                    return idx;
                from = idx + 1;
            }
        }

        /// <summary>
        /// Longest suffix of text[pos..length) that could still grow into the
        /// CLI's prompt once more bytes arrive.
        /// </summary>
        private static int GetPartialYouPromptSuffixLength(string text, int pos, bool streamAtStart)
        {
            string marker = (streamAtStart && pos == 0) ? YouPromptMarker : "\n" + YouPromptMarker;
            int maxLength = Math.Min(marker.Length - 1, text.Length - pos);
            for (int length = maxLength; length > 0; length--)
            {
                if (text.EndsWith(marker.Substring(0, length), StringComparison.Ordinal))
                    return length;
            }
            return 0;
        }

        private void OutputText(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                // Remove the CLI's own prompt lines ("You:" / "<assistant>:")
                string filteredText = _promptLinePattern.Replace(
                    text,
                    match => match.Groups[1].Value // Keep just the newline part
                );

                // Normalize line endings for Windows display
                filteredText = NormalizeLineEndings(filteredText);

                // Raise event immediately for real-time streaming
                if (!string.IsNullOrEmpty(filteredText))
                {
                    OutputReceived?.Invoke(filteredText);
                }
            }
        }

        private string NormalizeLineEndings(string text)
        {
            // First normalize all line endings to \n
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            // Then convert to Windows standard \r\n
            return text.Replace("\n", "\r\n");
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            DisposeStatusPipeClient();
            if (inputWriter != null)
            {
                inputWriter.Dispose();
                inputWriter = null;
            }
            OutputReceived = null;
            ErrorOccurred = null;
            GenerationComplete = null;
            ApprovalRequested = null;
            StatusReceived = null;
            if (llmProcess != null && !llmProcess.HasExited)
            {
                try { llmProcess.Kill(); }
                catch { }
            }
            if (llmProcess != null)
            {
                llmProcess.Dispose();
                llmProcess = null;
            }
        }
    }
}