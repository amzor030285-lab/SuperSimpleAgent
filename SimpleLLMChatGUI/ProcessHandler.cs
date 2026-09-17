using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SimpleLLMChatGUI
{
    public class ProcessHandler : IDisposable
    {
        private Process llmProcess;
        private bool disposed;
        private StringBuilder textBuffer = new StringBuilder(); // Buffer for incomplete text

        public event Action<string> OutputReceived;
        public event Action<string> ErrorOccurred;
        public event Action GenerationComplete;
        public event Func<string, string, bool> ApprovalRequested;
        public event Action<int> StatusReceived;
        public event Action<string> RunningToolReceived;

        private readonly StringBuilder streamBuffer = new StringBuilder();
        private readonly Decoder utf8Decoder = Encoding.UTF8.GetDecoder();
        private StatusPipeClient statusPipeClient;
        private StreamWriter inputWriter;

        public bool IsProcessRunning
        {
            get { return llmProcess != null && !llmProcess.HasExited; }
        }

        public ProcessHandler()
        {
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
                llmProcess.StartInfo.Arguments = "--no-banners";
                textBuffer.Clear();
                streamBuffer.Clear();
                utf8Decoder.Reset();
                llmProcess.Start();
                inputWriter = new StreamWriter(llmProcess.StandardInput.BaseStream, new UTF8Encoding(false));
                inputWriter.AutoFlush = true;

                statusPipeClient = new StatusPipeClient(llmProcess.Id);
                statusPipeClient.StatusReceived += OnStatusPipeReceived;
                statusPipeClient.IndexingStatusReceived += OnIndexingStatusPipeReceived;
                statusPipeClient.RunningToolReceived += OnRunningToolPipeReceived;
                statusPipeClient.Start();

                // 256 byte async buffer
                var buffer = new byte[256];
                Stream outputStream = llmProcess.StandardOutput.BaseStream;
                BeginReadOutput(outputStream, buffer);

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
            Action<int> handler = StatusReceived;
            if (handler != null)
                handler(tokens);
        }

        private void OnIndexingStatusPipeReceived(IndexingStatusEvent status)
        {
            IndexingStatusHub.Publish(status);
        }

        private void OnRunningToolPipeReceived(string toolName)
        {
            Action<string> handler = RunningToolReceived;
            if (handler != null)
                handler(toolName);
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
                    inputWriter.Flush();
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

        public bool SendInputWithImage(string imagePath, string prompt)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                ErrorOccurred?.Invoke("Image path cannot be empty.");
                return false;
            }

            // Ensure the path is quoted
            string quotedPath = "\"" + imagePath + "\"";

            // Build the final command
            string command = "/image " + quotedPath + " " + prompt;

            return SendInput(command);
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

        private void BeginReadOutput(Stream stream, byte[] buffer)
        {
            try
            {
                stream.BeginRead(buffer, 0, buffer.Length, new AsyncCallback(OutputReadCallback), new Tuple<Stream, byte[]>(stream, buffer));
            }
            catch
            {
                // ignore errors
            }
        }

        private void OutputReadCallback(IAsyncResult ar)
        {
            var state = (Tuple<Stream, byte[]>)ar.AsyncState;
            Stream stream = state.Item1;
            byte[] buffer = state.Item2;

            int bytesRead;
            try
            {
                bytesRead = stream.EndRead(ar);
            }
            catch
            {
                return;
            }

if (bytesRead > 0)
                {
                    try
                    {
                        int charCount = utf8Decoder.GetCharCount(buffer, 0, bytesRead);
                        char[] chars = new char[charCount];
                        utf8Decoder.GetChars(buffer, 0, bytesRead, chars, 0);
                        string newText = new string(chars);

                        if (!string.IsNullOrEmpty(newText))
                        {
                            // Process text immediately for streaming
                            ProcessStreamingText(newText);
                        }
                    }
                    catch
                    {
                        // If UTF-8 decoding fails, skip this chunk
                    }

                    // Continue reading
                    BeginReadOutput(stream, buffer);
                }
        }

        public bool SendApprovalResponse(bool approved)
        {
            if (llmProcess != null && !llmProcess.HasExited && inputWriter != null)
            {
                try
                {
                    inputWriter.WriteLine(approved ? "Y" : "N");
                    inputWriter.Flush();
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
                    FlushBufferedTextWithoutApprovalPrompt(buffered);
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

        private void FlushBufferedTextWithoutApprovalPrompt(string buffered)
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

            bool approved = false;
            if (ApprovalRequested != null)
                approved = ApprovalRequested(toolName, arguments);

            SendApprovalResponse(approved);
        }

        private void ProcessTextChunk(string textChunk)
        {
            if (string.IsNullOrEmpty(textChunk))
                return;

            if (textBuffer.Length > 0)
            {
                textBuffer.Append(textChunk);
                textChunk = textBuffer.ToString();
                textBuffer.Clear();
            }

            // For streaming, we want to output text immediately
            // But we need to be careful about "You:" patterns

            // Check if this chunk contains a "You:" pattern
            int youIndex = textChunk.IndexOf("You:");
            if (youIndex >= 0)
            {
                // Check if this "You:" is complete (followed by whitespace or newline)
                bool isCompletePattern = false;
                if (youIndex + 4 < textChunk.Length)
                {
                    char nextChar = textChunk[youIndex + 4];
                    isCompletePattern = char.IsWhiteSpace(nextChar) || nextChar == '\n' || nextChar == '\r';
                }
                else
                {
                    // "You:" is at the end, so it's incomplete
                    isCompletePattern = false;
                }

                if (isCompletePattern)
                {
                    // Process everything before "You:" and output it
                    string textBeforeYou = textChunk.Substring(0, youIndex);
                    if (!string.IsNullOrEmpty(textBeforeYou))
                    {
                        OutputText(textBeforeYou);
                    }

                    // Signal that generation is complete
                    GenerationComplete?.Invoke();

                    // Don't output the "You:" pattern itself
                    // Keep any text after "You:" for next chunk
                    string textAfterYou = textChunk.Substring(youIndex + 4);
                    if (!string.IsNullOrEmpty(textAfterYou))
                    {
                        // Remove leading whitespace after "You:"
                        textAfterYou = textAfterYou.TrimStart(' ', '\t');
                        if (!string.IsNullOrEmpty(textAfterYou))
                        {
                            OutputText(textAfterYou);
                        }
                    }
                }
                else
                {
                    // Incomplete "You:" pattern, output everything except the last few characters
                    int safeEndIndex = Math.Max(0, youIndex - 1);
                    string safeText = textChunk.Substring(0, safeEndIndex);
                    if (!string.IsNullOrEmpty(safeText))
                    {
                        OutputText(safeText);
                    }

                    // Keep the incomplete pattern for next chunk
                    textBuffer.Append(textChunk.Substring(safeEndIndex));
                }
            }
            else
            {
                // No "You:" pattern in this chunk, output it immediately
                OutputText(textChunk);
            }
        }

        private void OutputText(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                // Remove any "You:" prompts from the middle of the text
                string filteredText = Regex.Replace(
                    text,
                    @"(^|\r?\n)[ \t]*You:[ \t]*",
                    match => match.Groups[1].Value, // Keep just the newline part
                    RegexOptions.Multiline
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