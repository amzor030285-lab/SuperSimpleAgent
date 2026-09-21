using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace SimpleLLMChatCLI
{
    /// <summary>
    /// curl.exe HTTPS fallback via a Windows named pipe (body never touches disk).
    /// Chat completions stream SSE (<see cref="SendRequest"/>); embeddings use a
    /// buffered JSON response (<see cref="PostJson"/>).
    /// </summary>
    internal static class CurlClient
    {
        public static bool CanFallback(string url, Exception ex)
        {
            return TlsCurlFallback.CanAttempt(url, ex);
        }

        /// <summary>
        /// Non-streaming JSON POST. Used for embeddings (and similar one-shot APIs).
        /// <paramref name="timeoutSeconds"/> caps both the connect phase
        /// (--connect-timeout) and the whole request (--max-time); a dead server
        /// must not hang the caller forever.
        /// </summary>
        public static string PostJson(string fullUrl, string apiKey, JObject payload,
            int timeoutSeconds, out int exitCode)
        {
            exitCode = -1;
            try
            {
                string output = null;
                string error = null;
                Outcome outcome = RunPost(fullUrl, apiKey, payload,
                    noBuffer: false,
                    connectTimeoutSeconds: timeoutSeconds,
                    maxTimeSeconds: timeoutSeconds,
                    stallSeconds: 0,
                    activity: null,
                    process =>
                    {
                        Thread errThread = StartStderrReader(process, text => error = text);
                        output = process.StandardOutput.ReadToEnd();
                        errThread.Join(5000);
                    });
                exitCode = outcome.ExitCode;

                if (exitCode != 0 && string.IsNullOrEmpty(output))
                    return "cURL failed (exit " + exitCode + "): " + error;
                return output;
            }
            catch (Exception ex)
            {
                exitCode = -1;
                return "cURL fallback failed: " + ex.Message;
            }
        }

        /// <summary>
        /// Streaming chat-completions POST. Parses SSE from curl stdout.
        /// <paramref name="connectTimeoutSeconds"/> caps the connect phase; a stream
        /// silent for longer than <paramref name="stallSeconds"/> kills curl, just
        /// like the stall watchdog on the direct HTTP path (0 disables it).
        /// </summary>
        public static LLMClient.LLMCompletionResponse SendRequest(
            string serverUrl, string apiKey, JObject payload,
            Action<string> outputCallback,
            Action<string> onReasoningChunk,
            Action<int> onReasoningSummary,
            int connectTimeoutSeconds, int stallSeconds,
            Action<ToolRegistry.ToolCall> onToolCallChunk = null,
            Action onContentStart = null,
            Action startBlock = null)
        {
            string url = serverUrl.TrimEnd('/') + "/v1/chat/completions";
            try
            {
                LLMClient.LLMCompletionResponse result = new LLMClient.LLMCompletionResponse(
                    string.Empty, null, string.Empty);
                string stderr = null;
                bool parsed = false;
                LLMClient.StreamActivity activity = new LLMClient.StreamActivity();
                Outcome outcome = RunPost(url, apiKey, payload,
                    noBuffer: true,
                    connectTimeoutSeconds: connectTimeoutSeconds,
                    maxTimeSeconds: 0,
                    stallSeconds: stallSeconds,
                    activity: activity,
                    process =>
                    {
                        Thread errThread = StartStderrReader(process, text => stderr = text);
                        result = SseStreamParser.Parse(
                            new LLMClient.ActivityReader(process.StandardOutput, activity),
                            outputCallback, onReasoningChunk, onReasoningSummary,
                            onToolCallChunk, onContentStart, startBlock);
                        parsed = true;
                        errThread.Join(5000);
                    });

                // A stop clicked while the curl stream was in flight: same result as
                // aborting the direct HTTP request.
                if (outcome.AbortKilled)
                    return new LLMClient.LLMCompletionResponse("generation_aborted", null, "aborted");

                if (outcome.StallKilled)
                    return new LLMClient.LLMCompletionResponse(
                        "no stream data for " + stallSeconds + " s (model stalled, request aborted)",
                        null, "request_failed");

                if (outcome.ExitCode != 0)
                {
                    return new LLMClient.LLMCompletionResponse(
                        "cURL failed (exit " + outcome.ExitCode + "): " + stderr,
                        null, "request_failed");
                }

                if (!parsed
                    || (string.IsNullOrEmpty(result.Content) && string.IsNullOrEmpty(result.FinishReason)))
                {
                    string detail = string.IsNullOrEmpty(stderr) ? "no response data" : stderr.Trim();
                    return new LLMClient.LLMCompletionResponse(
                        "cURL returned no response: " + detail,
                        null, "request_failed");
                }

                return result;
            }
            catch (Exception ex)
            {
                return new LLMClient.LLMCompletionResponse(
                    "cURL fallback failed: " + ex.Message, null, "request_failed");
            }
        }

        private struct Outcome
        {
            public int ExitCode;
            public bool StallKilled;
            public bool AbortKilled;
        }

        private static Thread StartStderrReader(Process process, Action<string> onComplete)
        {
            Thread errThread = new Thread(() =>
            {
                string text = null;
                try { text = process.StandardError.ReadToEnd(); }
                catch { }
                if (onComplete != null)
                    onComplete(text);
            });
            errThread.IsBackground = true;
            errThread.Start();
            return errThread;
        }

        /// <summary>
        /// Shared pipe + process setup. <paramref name="consume"/> reads stdout while curl runs;
        /// the pipe writer is joined and the process waited on afterward. Returns exit code
        /// plus why the process was killed, if the watchdog did it.
        /// </summary>
        private static Outcome RunPost(string fullUrl, string apiKey, JObject payload,
            bool noBuffer, int connectTimeoutSeconds, int maxTimeSeconds, int stallSeconds,
            LLMClient.StreamActivity activity, Action<Process> consume)
        {
            Outcome outcome = new Outcome
            {
                ExitCode = -1,
                StallKilled = false,
                AbortKilled = false
            };

            string pipeName = "llmcurl_" + Guid.NewGuid().ToString("N");
            byte[] jsonBytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));

            using (NamedPipeServerStream pipeServer = new NamedPipeServerStream(
                pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte))
            {
                string args = "-s"
                    + (noBuffer ? " -N" : "")
                    + TlsCurlFallback.CacertFlag()
                    + (connectTimeoutSeconds > 0 ? " --connect-timeout " + connectTimeoutSeconds : "")
                    + (maxTimeSeconds > 0 ? " --max-time " + maxTimeSeconds : "")
                    + " -X POST"
                    + " -H \"Content-Type: application/json\""
                    + (string.IsNullOrEmpty(apiKey) ? "" : " -H \"Authorization: Bearer " + apiKey + "\"")
                    + " --data-binary \"@\\\\.\\pipe\\" + pipeName + "\""
                    + " \"" + fullUrl + "\"";

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = TlsCurlFallback.DefaultCurlPath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (Process process = Process.Start(psi))
                {
                    Thread pipeThread = new Thread(() =>
                    {
                        try
                        {
                            pipeServer.WaitForConnection();
                            pipeServer.Write(jsonBytes, 0, jsonBytes.Length);
                            pipeServer.Flush();
                            pipeServer.Close();
                        }
                        catch { }
                    });
                    pipeThread.IsBackground = true;
                    pipeThread.Start();

                    int finished = 0;
                    int killKind = 0; // 1 = stall, 2 = user abort

                    if (stallSeconds > 0 && activity != null)
                    {
                        // Watchdog: kill curl if the stream goes silent for longer than
                        // stallSeconds, or as soon as a stop is requested. Without this,
                        // a blackholed or frozen server blocks this thread forever
                        // (curl itself has no idle timeout for streaming).
                        Thread stallWatchdog = new Thread(delegate ()
                        {
                            try
                            {
                                while (finished == 0)
                                {
                                    if (AbortSupport.AbortRequested)
                                    {
                                        if (Interlocked.Exchange(ref killKind, 2) == 0)
                                        {
                                            try { process.Kill(); } catch { }
                                        }
                                        return;
                                    }
                                    if (activity.Idle() > TimeSpan.FromSeconds(stallSeconds))
                                    {
                                        if (Interlocked.Exchange(ref killKind, 1) == 0)
                                        {
                                            try { process.Kill(); } catch { }
                                        }
                                        return;
                                    }
                                    Thread.Sleep(250);
                                }
                            }
                            catch { }
                        }) { IsBackground = true };
                        stallWatchdog.Start();
                    }

                    try
                    {
                        consume(process);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref finished, 1);
                    }

                    pipeThread.Join(5000);
                    process.WaitForExit();
                    outcome.ExitCode = process.ExitCode;
                    if (killKind == 1) outcome.StallKilled = true;
                    if (killKind == 2) outcome.AbortKilled = true;
                    return outcome;
                }
            }
        }
    }
}
