using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SimpleLLMChatCLI.RAG;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace SimpleLLMChatCLI
{
public class LLMClient
{
    private readonly ConfigHandler config;
    private readonly ToolRegistry registry;
    private readonly Func<string, string, bool> requestToolApproval;

    // Cached sysprompt + tools schema length (NyoCoder-style base overhead).
    // Cleared (set null) on /reload so the next request recomputes it.
    public int? BaseOverheadChars;

    public string ReasoningEffort { get; set; }

    public LLMClient(ConfigHandler config, ToolRegistry registry, Func<string, string, bool> requestToolApproval = null)
    {
        this.registry = registry;
        this.config = config;
        this.requestToolApproval = requestToolApproval ?? CliRequestApproval;
    }

    // Struct for chat messages
    public struct ChatMessage
    {
        public string Role;
        public string Content;
        public string Image;
        public string ImageMime;
        public List<ToolRegistry.ToolCall> ToolCalls;
        public string ToolCallId;

        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
            ToolCallId = "";
            Image = null;
            ImageMime = null;
            ToolCalls = new List<ToolRegistry.ToolCall>();
        }
    }

    public struct LLMCompletionResponse
    {
        public string Content;
        public List<ToolRegistry.ToolCall> ToolCalls;
        public string FinishReason;

        public LLMCompletionResponse(string content, List<ToolRegistry.ToolCall> toolCalls, string finishReason)
        {
            Content = content;
            ToolCalls = toolCalls ?? new List<ToolRegistry.ToolCall>();
            FinishReason = finishReason;
        }
    }

    public void ProcessConversation(
        List<ChatMessage> conversation,
        string userMessage,
        string image,
        string assistantName,
        List<string> enabledTools,
        List<string> toolsRequiringApproval,
        bool outputOnly)
    {
        ChatBlockDisplayMode toolCallDisplay = config.GetChatBlockDisplayMode("toolcalldisplay", ChatBlockDisplayMode.Collapsed);
        ChatBlockDisplayMode toolOutputDisplay = config.GetChatBlockDisplayMode("tooloutputdisplay", ChatBlockDisplayMode.Shown);

        HashSet<string> enabledSet = new HashSet<string>(
            enabledTools ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        HashSet<string> approvalSet = new HashSet<string>(
            toolsRequiringApproval ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        // If prior context is already high, compact it (micro/summarize/ask), then
        // run the user prompt.
        MaybeManageContext(
            conversation,
            outputOnly,
            "\n\n[Continue from this context. The user's next message follows.]");

        RagHost.FlushPendingErrorOnce();
        RagHost.WaitIfIndexing();

        string contentForLlm = userMessage ?? string.Empty;
        if (config.GetConfigBool("ragEnabled", false))
        {
            bool everyTurn = string.Equals(
                config.GetConfigValue("ragRetrieveMode", "newchat"),
                "everyturn",
                StringComparison.OrdinalIgnoreCase);
            bool isNewChat = conversation == null || conversation.Count == 0;
            
            // If RAG is enabled and the retrieve mode is set to every turn or it's a new chat, retrieve the context.
            if (everyTurn || isNewChat)
            {
                AutoRagResult rag = AutoRagContext.TryRetrieve(config, userMessage, contentForLlm);
                if (!outputOnly && !string.IsNullOrEmpty(rag.UserStatusLine))
                    ChatOutput.WriteLine(rag.UserStatusLine);
                contentForLlm = rag.MergedPrompt;
            }
        }

        conversation.Add(new ChatMessage
        {
            Role = "user",
            Content = contentForLlm,
            Image = image
        });

        // Context injectors (memory/skills summaries) are fetched once per user message
        // and reused across agent-loop iterations (tool rounds) for this turn.
        List<string> contextInjections = registry != null
            ? registry.GetContextInjections(enabledTools)
            : new List<string>();

        // The assistant name is printed once per turn:
        //   "LLM: output"  when the response starts with plain output, or
        //   "LLM:" on its own block when a [thinking]/[tool ...] block comes first.
        bool assistantHeaderPrinted = false;
        Action startBlock = null;
        Action onContentStart = null;
        if (!outputOnly)
        {
            // Runs before tagged blocks ([thinking], [thought for ...], [tool ...], errors).
            startBlock = () =>
            {
                ChatOutput.StartBlock();
                if (!assistantHeaderPrinted)
                {
                    assistantHeaderPrinted = true;
                    ChatOutput.WriteLine(assistantName + ":");
                    ChatOutput.StartBlock();
                }
            };
            // Runs before the first content chunk of each response.
            onContentStart = () =>
            {
                ChatOutput.StartBlock();
                if (!assistantHeaderPrinted)
                {
                    assistantHeaderPrinted = true;
                    ChatOutput.Write(assistantName + ": ");
                }
            };
        }

        while (true)
        {
            PublishStatusTokens(GetConversationCharacterCount(conversation) + GetBaseCharacterOverhead());

            // Stream tool calls with explicit open/close markers as they arrive, rather than
            // waiting for the full response before printing them. A tool that requires approval
            // is suppressed here since the approval prompt below shows its name + args instead.
            // Hidden: name only (no args). outputOnly: no tool-call UI at all.
            bool toolCallUiOpen = false;
            bool toolCallArgsEndedWithNewline = true;
            bool currentToolCallSuppressed = false;
            Action<ToolRegistry.ToolCall> toolCallStreamCallback = null;
            if (!outputOnly)
            {
                toolCallStreamCallback = (toolCall) =>
                {
                    if (!string.IsNullOrEmpty(toolCall.Name) && string.IsNullOrEmpty(toolCall.Arguments))
                    {
                        if (toolCallUiOpen)
                        {
                            if (!toolCallArgsEndedWithNewline)
                                ChatOutput.WriteLine();
                            ChatOutput.WriteLine("[/tool call]");
                        }

                        currentToolCallSuppressed = approvalSet.Contains(toolCall.Name);

                        if (!currentToolCallSuppressed)
                        {
                            startBlock();
                            ChatOutput.WriteLine("[tool call] " + toolCall.Name);
                            toolCallUiOpen = true;
                            toolCallArgsEndedWithNewline = true;
                        }
                        else
                        {
                            toolCallUiOpen = false;
                        }
                    }
                    else if (!string.IsNullOrEmpty(toolCall.Arguments)
                        && !currentToolCallSuppressed
                        && toolCallDisplay != ChatBlockDisplayMode.Hidden)
                    {
                        ChatOutput.Write(toolCall.Arguments);
                        toolCallArgsEndedWithNewline = toolCall.Arguments.EndsWith("\n");
                    }
                };
            }

            LLMCompletionResponse response = sendMessages(conversation, enabledTools, ChatOutput.Write, toolCallStreamCallback, onContentStart, startBlock, outputOnly, contextInjections);

            if (toolCallUiOpen)
            {
                if (!toolCallArgsEndedWithNewline)
                    ChatOutput.WriteLine();
                ChatOutput.WriteLine("[/tool call]");
            }

            if (response.FinishReason == "request_failed")
            {
                if (!outputOnly)
                    ChatOutput.StartBlock();
                ChatOutput.WriteLine("Request to LLM Failed (" + response.Content + ")");
                break;
            }

            if (response.FinishReason == "aborted")
            {
                PublishStatusTokens(GetConversationCharacterCount(conversation) + GetBaseCharacterOverhead());
                if (!outputOnly)
                {
                    ChatOutput.StartBlock();
                    ChatOutput.WriteLine("[Generation stopped by user]");
                }
                break;
            }

            if (response.ToolCalls != null && response.ToolCalls.Count > 0)
            {
                // Add assistant tool call message
                ChatMessage assistantCall = new ChatMessage
                {
                    Role = "assistant",
                    Content = string.Empty,
                    ToolCalls = response.ToolCalls
                };
                conversation.Add(assistantCall);

                for (int i = 0; i < response.ToolCalls.Count; i++)
                {
                    ToolRegistry.ToolCall call = response.ToolCalls[i];
                    bool needsApproval = approvalSet.Contains(call.Name);

                    if (needsApproval && outputOnly)
                    {
                        ChatOutput.WriteLine("Error: Model called " + call.Name + " which requires approval");
                        return;
                    }

                    // The tool call block itself was already streamed above (if not suppressed
                    // for approval); only pad a blank line before the approval prompt here.
                    if (!outputOnly && needsApproval)
                        startBlock();

                    int exitCode = 0;
                    string toolContent;

                    string toolImage = null;
                    string toolImageMime = null;
                    if (!enabledSet.Contains(call.Name))
                    {
                        exitCode = -1;
                        toolContent = ToolRegistry.FormatCommandResult(
                            call.Name,
                            "error: tool '" + call.Name + "' is disabled by configuration.",
                            exitCode
                        );
                    }
                    else if (needsApproval)
                    {
                        if (requestToolApproval(call.Name, call.Arguments))
                        {
                            registry.ExecuteToolCall(call.Name, call.Arguments, out toolContent, out exitCode, out toolImage, out toolImageMime);
                        }
                        else
                        {
                            exitCode = -1;
                            toolContent = ToolRegistry.FormatCommandResult(
                                call.Name,
                                "Tool execution was cancelled by the user.",
                                exitCode
                            );
                        }
                    }
                    else
                    {
                        registry.ExecuteToolCall(call.Name, call.Arguments, out toolContent, out exitCode, out toolImage, out toolImageMime);
                    }

                    ChatMessage toolMsg = new ChatMessage
                    {
                        Role = "tool",
                        Content = toolContent,
                        ToolCallId = call.Id,
                        Image = toolImage,
                        ImageMime = toolImageMime
                    };
                    conversation.Add(toolMsg);

                    if (!outputOnly)
                    {
                        startBlock();
                        if (toolOutputDisplay == ChatBlockDisplayMode.Hidden)
                        {
                            ChatOutput.WriteLine("[tool output]");
                            ChatOutput.WriteLine("Exit Code: " + exitCode);
                        }
                        else
                        {
                            ChatOutput.WriteLine("[tool output]");
                            ChatOutput.Write(toolContent ?? "");
                            if (string.IsNullOrEmpty(toolContent) || !toolContent.EndsWith("\n"))
                                ChatOutput.WriteLine();
                            ChatOutput.WriteLine("[/tool output]");
                        }
                    }
                }
                // Mid-turn (after tools): compact context silently, then continue the user request.
                MaybeManageContext(
                    conversation,
                    outputOnly,
                    "\n\n[Continue from this context. The user's original request is being processed.]");
                continue;
            }

            // Add assistant message
            ChatMessage assistantMsg = new ChatMessage
            {
                Role = "assistant",
                Content = response.Content
            };
            conversation.Add(assistantMsg);
            PublishStatusTokens(GetConversationCharacterCount(conversation) + GetBaseCharacterOverhead());

            if (!outputOnly)
            {
                ChatOutput.EndLine(); // Terminate the response line if the model didn't
            }
            break;
        }
    }

    /// <summary>
    /// Context-usage ladder (fully automatic, never blocks on input). Called before the
    /// first request of a turn and again after each tool round:
    ///   &gt;= 80%  micro-compact: drop old assistant(tool_calls)+tool(tool_call_id) groups,
    ///            keeping the newest 2.
    ///   &gt;= 95%  truncate the oldest ~30% of messages, repeated until below 95%
    ///            (absorbs single-turn spikes that jump straight past the thresholds).
    ///   &gt;= 90%  summarize the whole conversation into a compact recap (the summarize
    ///            request itself falls back to a hard truncation if it fails).
    ///   &gt;= 95%  last resort: shrink the biggest message contents so the request fits.
    /// Compaction always happens when usage is high enough, and the chat continues.
    /// </summary>
    private void MaybeManageContext(List<ChatMessage> conversation, bool outputOnly, string continueHint)
    {
        int contextWindowSize = config.GetConfigInt("contextWindowSize", 0);
        PublishStatusTokens(GetConversationCharacterCount(conversation) + GetBaseCharacterOverhead());

        if (UsageOf(conversation, contextWindowSize) < TokenEstimator.MicroCompactThreshold)
            return;

        // Pass 1 (free): drop old tool groups (the biggest, least essential messages).
        int toolRemoved = MicroCompact(conversation, 2);
        if (toolRemoved > 0)
            ChatOutput.WriteLine("[Context usage high - removed " + toolRemoved + " old tool messages]");

        // Pass 2 (free): critical level - truncate oldest ~30% until below 95%.
        int guard = 0;
        while (UsageOf(conversation, contextWindowSize) >= TokenEstimator.AskUserThreshold && guard++ < 10)
        {
            int removed = TruncateOldestFraction(conversation, TokenEstimator.TruncateFraction);
            if (removed <= 0)
                break;
            ChatOutput.WriteLine("[Context usage high - truncated " + removed + " oldest messages]");
        }

        // Pass 3 (one LLM call): still >= 90% after the free passes - summarize silently.
        if (UsageOf(conversation, contextWindowSize) >= TokenEstimator.SummarizeThreshold)
            SummaryAndReplace(conversation, outputOnly, continueHint);

        // Pass 4 (last resort): still critical - shrink the biggest message contents.
        if (UsageOf(conversation, contextWindowSize) >= TokenEstimator.AskUserThreshold)
            TruncateToFit(conversation, contextWindowSize);

        PublishStatusTokens(GetConversationCharacterCount(conversation) + GetBaseCharacterOverhead());
    }

    private double UsageOf(List<ChatMessage> conversation, int contextWindowSize)
    {
        return TokenEstimator.Usage(
            GetConversationCharacterCount(conversation) + GetBaseCharacterOverhead(),
            contextWindowSize);
    }

    /// <summary>
    /// Last-resort pass: while the conversation still exceeds the critical threshold,
    /// truncate the single biggest message content to ~60% of the window so the outgoing
    /// request fits. Guarantees the turn continues even when one message alone is larger
    /// than the context window.
    /// </summary>
    private void TruncateToFit(List<ChatMessage> conversation, int contextWindowSize)
    {
        int budgetChars = (int)(contextWindowSize * 3 * 0.6);
        int guard = 0;
        while (UsageOf(conversation, contextWindowSize) >= TokenEstimator.AskUserThreshold && guard++ < 10)
        {
            int biggestIndex = -1;
            int biggestLen = -1;
            for (int i = 0; i < conversation.Count; i++)
            {
                ChatMessage message = conversation[i];
                if (message.Content != null && message.Content.Length > biggestLen)
                {
                    biggestIndex = i;
                    biggestLen = message.Content.Length;
                }
            }
            if (biggestIndex < 0 || biggestLen <= budgetChars + 100)
                break;

            ChatMessage biggest = conversation[biggestIndex];
            biggest.Content = biggest.Content.Substring(0, budgetChars)
                + "\n[... content truncated to fit the context window ...]";
            conversation[biggestIndex] = biggest;
            ChatOutput.WriteLine("[Context usage high - message content truncated to fit the context window]");
        }
    }

    /// <summary>
    /// Silent summarize-and-replace used by the >= 90% branch.
    /// </summary>
    private void SummaryAndReplace(List<ChatMessage> conversation, bool outputOnly, string continueHint)
    {
        if (!outputOnly)
        {
            ChatOutput.StartBlock();
            ChatOutput.WriteLine("[Context usage high - summarizing conversation...]");
        }

        string previousSummary = ExtractPreviousSummary(conversation);
        string summary = SummarizeConversation(conversation, previousSummary);
        if (string.IsNullOrEmpty(summary))
        {
            // The summarize pass failed (model stalled / request error): fall back to a
            // hard truncation so the turn can continue with a reduced context.
            int removed = TruncateOldestFraction(conversation, TokenEstimator.TruncateFraction);
            if (!outputOnly)
                ChatOutput.WriteLine("[Context usage high - summary failed, truncated " + removed + " oldest messages]");
            PublishStatusTokens(GetConversationCharacterCount(conversation) + GetBaseCharacterOverhead());
            return;
        }

        conversation.Clear();
        conversation.Add(new ChatMessage(
            "user",
            SessionStore.SummaryMarker + "\n" + summary + continueHint));

        if (!outputOnly)
            ChatOutput.WriteLine("[Conversation summarized - continuing...]");

        PublishStatusTokens(GetConversationCharacterCount(conversation) + GetBaseCharacterOverhead());
    }

    /// <summary>
    /// Removes whole assistant(tool_calls) + tool(tool_call_id) groups from the OLDEST
    /// end of the conversation, keeping the newest <paramref name="keepLastGroups"/>
    /// groups untouched. Returns the number of messages removed.
    /// </summary>
    internal static int MicroCompact(List<ChatMessage> conversation, int keepLastGroups)
    {
        if (conversation == null || conversation.Count == 0 || keepLastGroups < 0)
            return 0;

        bool[] inGroup = ComputeToolGroupMask(conversation);
        List<int> groupStarts = new List<int>();
        for (int i = 0; i < conversation.Count; i++)
        {
            if (conversation[i].Role == "assistant"
                && conversation[i].ToolCalls != null
                && conversation[i].ToolCalls.Count > 0)
            {
                groupStarts.Add(i);
            }
        }

        int groupsToRemove = groupStarts.Count - keepLastGroups;
        if (groupsToRemove <= 0)
            return 0;

        HashSet<int> removeIndexes = new HashSet<int>();
        int targetLastGroup = groupStarts[groupStarts.Count - keepLastGroups];
        for (int i = 0; i < targetLastGroup; i++)
        {
            if (inGroup[i])
                removeIndexes.Add(i);
        }

        if (removeIndexes.Count == 0)
            return 0;

        List<ChatMessage> compacted = new List<ChatMessage>(conversation.Count - removeIndexes.Count);
        for (int i = 0; i < conversation.Count; i++)
        {
            if (!removeIndexes.Contains(i))
                compacted.Add(conversation[i]);
        }

        conversation.Clear();
        conversation.AddRange(compacted);
        return removeIndexes.Count;
    }

    /// <summary>
    /// Marks indexes that belong to an intermediate tool group: an assistant message
    /// with tool_calls plus every following "tool" message. Such groups must be
    /// compacted atomically so the OpenAI payload stays well-formed.
    /// </summary>
    private static bool[] ComputeToolGroupMask(List<ChatMessage> conversation)
    {
        bool[] inGroup = new bool[conversation.Count];
        for (int i = 0; i < conversation.Count; i++)
        {
            if (conversation[i].Role == "assistant"
                && conversation[i].ToolCalls != null
                && conversation[i].ToolCalls.Count > 0)
            {
                inGroup[i] = true;
                for (int j = i + 1; j < conversation.Count && conversation[j].Role == "tool"; j++)
                    inGroup[j] = true;
            }
        }
        return inGroup;
    }

    /// <summary>
    /// Hard-truncates the oldest messages, removing whole tool groups when they are
    /// hit and never wiping the conversation entirely. Returns messages removed.
    /// </summary>
    internal static int TruncateOldestFraction(List<ChatMessage> conversation, double fraction)
    {
        if (conversation == null || conversation.Count == 0)
            return 0;

        int target = (int)Math.Ceiling(conversation.Count * Math.Max(0.0, Math.Min(1.0, fraction)));
        if (target <= 0)
            return 0;
        if (target >= conversation.Count)
            target = conversation.Count - 1;
        if (target <= 0)
            return 0;

        // Mark the oldest `target` messages as the base removal prefix
        // (no whole-group instruction here; the prefix is expanded below to stay
        // group-safe).
        HashSet<int> removeIndexes = new HashSet<int>();
        for (int i = 0; i < target; i++)
            removeIndexes.Add(i);

        // Expand the removal prefix so no group is cut: any assistant(tool_calls)
        // removed also removes its following tool messages, and any removed tool
        // message also removes its preceding assistant call.
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < conversation.Count; i++)
            {
                if (!removeIndexes.Contains(i))
                    continue;

                if (conversation[i].Role == "assistant")
                {
                    for (int j = i + 1; j < conversation.Count && conversation[j].Role == "tool"; j++)
                    {
                        if (removeIndexes.Add(j))
                            changed = true;
                    }
                }
                else if (conversation[i].Role == "tool")
                {
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (conversation[j].Role == "assistant")
                        {
                            if (removeIndexes.Add(j))
                                changed = true;
                            break;
                        }
                        if (conversation[j].Role != "tool")
                            break;
                    }
                }
            }
        }

        int removed = removeIndexes.Count;

        if (removed == 0)
            return 0;
        if (removed >= conversation.Count)
        {
            // Always keep the very newest message.
            removeIndexes.Remove(conversation.Count - 1);
        }

        List<ChatMessage> compacted = new List<ChatMessage>(conversation.Count - removeIndexes.Count);
        for (int i = 0; i < conversation.Count; i++)
        {
            if (!removeIndexes.Contains(i))
                compacted.Add(conversation[i]);
        }

        conversation.Clear();
        conversation.AddRange(compacted);
        return removed;
    }

    /// <summary>
    /// Manual /compact command: runs the 80% micro-compaction regardless of the
    /// current usage so the user can trim old tool traffic on demand.
    /// </summary>
    public static int PerformMicroCompact(List<ChatMessage> conversation)
    {
        return MicroCompact(conversation, 2);
    }

    public void PublishStatusTokens(int characterCount)
    {
        if (Program.StatusPipe == null)
            return;

        Program.StatusPipe.PublishStatus(TokenEstimator.ApproximateTokens(characterCount));
    }

    /// <summary>
    /// Sum of message content and tool-call name/arguments (excludes base overhead).
    /// </summary>
    public static int GetConversationCharacterCount(List<ChatMessage> conversation)
    {
        int count = 0;
        if (conversation == null)
            return count;

        foreach (var msg in conversation)
        {
            if (!string.IsNullOrEmpty(msg.Content))
                count += msg.Content.Length;

            if (msg.ToolCalls != null)
            {
                foreach (var toolCall in msg.ToolCalls)
                {
                    if (!string.IsNullOrEmpty(toolCall.Name))
                        count += toolCall.Name.Length;
                    if (!string.IsNullOrEmpty(toolCall.Arguments))
                        count += toolCall.Arguments.Length;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Cached system prompt + tool schema length. 0 until the first request is built
    /// or <see cref="RefreshBaseCharacterOverhead"/> runs (e.g. after /reload).
    /// </summary>
    public int GetBaseCharacterOverhead()
    {
        return BaseOverheadChars.HasValue ? BaseOverheadChars.Value : 0;
    }

    /// <summary>
    /// Recomputes system prompt + tool schema overhead from the current config/tools.
    /// Used after /reload so status stays accurate without waiting for the next request.
    /// </summary>
    public void RefreshBaseCharacterOverhead(List<string> enabledTools)
    {
        List<string> contextInjections = registry != null
            ? registry.GetContextInjections(enabledTools)
            : new List<string>();
        string systemPrompt = BuildSystemPrompt(enabledTools, contextInjections);
        int toolsChars = 0;
        if (enabledTools != null && enabledTools.Count > 0 && registry != null)
        {
            JArray toolsArray = registry.BuildToolsArray(enabledTools);
            if (toolsArray.Count > 0)
                toolsChars = toolsArray.ToString(Formatting.None).Length;
        }
        BaseOverheadChars = systemPrompt.Length + toolsChars;
    }

    private string BuildSystemPrompt(List<string> enabledTools, List<string> contextInjections = null)
    {
        string sysprompt = ConfigHandler.DecodeStoredPrompt(config.GetConfigValue("sysprompt")) ?? "";

        if (registry != null)
        {
            List<string> injections = contextInjections ?? registry.GetContextInjections(enabledTools);
            foreach (string injection in injections)
                sysprompt += "\n\n" + injection;
        }

        string ragHint = RagHost.GetKnowledgePathHint(config);
        if (!string.IsNullOrEmpty(ragHint))
        {
            if (sysprompt.Length > 0)
                sysprompt += "\n\n";
            sysprompt += ragHint;
        }

        return sysprompt;
    }

    private JObject BuildRequestPayload(List<ChatMessage> conversation, List<string> enabledTools, List<string> contextInjections = null)
    {
        JObject payload = new JObject
        {
            ["model"] = config.GetConfigValue("model")
        };

        string systemPrompt = BuildSystemPrompt(enabledTools, contextInjections);

        JArray messages = new JArray();
        messages.Add(new JObject
        {
            ["role"] = "system",
            ["content"] = systemPrompt
        });

        if (conversation != null)
        {
            foreach (var msg in conversation)
                messages.Add(BuildMessageObject(msg));
        }

        payload["messages"] = messages;

        int toolsChars = 0;
        if (enabledTools != null && enabledTools.Count > 0 && registry != null)
        {
            JArray toolsArray = registry.BuildToolsArray(enabledTools);
            if (toolsArray.Count > 0)
            {
                payload["tools"] = toolsArray;
                toolsChars = toolsArray.ToString(Formatting.None).Length;
            }
        }

        // Capture overhead from a normal request only (not summarization with tools disabled).
        if (!BaseOverheadChars.HasValue)
        {
            List<string> configuredTools = config.GetConfigList("tools");
            bool summarizationPass = (enabledTools == null || enabledTools.Count == 0)
                && configuredTools != null && configuredTools.Count > 0;
            if (!summarizationPass)
                BaseOverheadChars = systemPrompt.Length + toolsChars;
        }

        payload["stream"] = true;

        if (!string.IsNullOrEmpty(ReasoningEffort))
            payload["reasoning_effort"] = ReasoningEffort;

        return payload;
    }

    /// <summary>
    /// Asks the model for a compact structured recap of the conversation (tools disabled).
    /// Runs silently — summary text is not streamed to the user. When a previous recap
    /// exists (the floating summary message), it is folded in so details are not lost.
    /// </summary>
    private string SummarizeConversation(List<ChatMessage> conversation, string previousSummary)
    {
        if (conversation == null || conversation.Count == 0)
            return string.Empty;

        List<ChatMessage> summaryConversation = new List<ChatMessage>(conversation);

        bool previousAlreadyInConversation = conversation.Count > 0
            && conversation[conversation.Count - 1].Content != null
            && conversation[conversation.Count - 1].Content.StartsWith(
                SessionStore.SummaryMarker, StringComparison.Ordinal);

        // Inject the earlier recap only when it is not already the floating message.
        if (!string.IsNullOrEmpty(previousSummary) && !previousAlreadyInConversation)
        {
            summaryConversation.Add(new ChatMessage(
                "user",
                "Earlier recap you produced; fold it into the updated recap below:\n" + previousSummary));
        }

        summaryConversation.Add(new ChatMessage("user",
            "Produce a compact, well-structured recap of the conversation above so that you " +
            "can continue seamlessly after this context is replaced.\n" +
            "Preserve EXACT file paths, URLs, identifiers (UUIDs, tokens, IDs), numbers and " +
            "version strings verbatim — do not abbreviate, guess, or invent them.\n" +
            "Use exactly these 9 sections, one short bullet per line, '-' when empty:\n" +
            "1. Main topics discussed\n" +
            "2. Key facts and information learned\n" +
            "3. Decisions made and open questions\n" +
            "4. Code, commands, file paths, URLs, identifiers (verbatim)\n" +
            "5. Current state / in-progress work\n" +
            "6. Tools used and their outcomes\n" +
            "7. User preferences and constraints\n" +
            "8. What still needs follow-up\n" +
            "9. Anything that would be hard to reconstruct\n" +
            "Keep it dense; prefer nouns and exact names over prose."));

        // No tools, null output — silent background pass.
        LLMCompletionResponse response = sendMessages(summaryConversation, new List<string>(), null);
        if (response.FinishReason == "request_failed" || response.FinishReason == "aborted")
            return string.Empty;

        return response.Content ?? string.Empty;
    }

    /// <summary>
    /// Pulls the floating recap out of a previously-compacted conversation, stripping
    /// the marker prefix and any trailing "Continue from" hint so a re-summarization
    /// does not treat the hint as conversation content.
    /// </summary>
    private static string ExtractPreviousSummary(List<ChatMessage> conversation)
    {
        if (conversation == null)
            return null;

        for (int i = conversation.Count - 1; i >= 0; i--)
        {
            string content = conversation[i].Content;
            if (string.IsNullOrEmpty(content)
                || !content.StartsWith(SessionStore.SummaryMarker, StringComparison.Ordinal))
                continue;

            int newline = content.IndexOf('\n');
            string body = (newline < 0)
                ? content.Substring(SessionStore.SummaryMarker.Length)
                : content.Substring(newline + 1);

            int hint = body.IndexOf("[Continue from", StringComparison.Ordinal);
            if (hint >= 0)
                body = body.Substring(0, hint);
            return body.TrimEnd('\r', '\n', ' ');
        }
        return null;
    }

    private JObject BuildMessageObject(ChatMessage msg)
    {
        JObject msgObj = new JObject
        {
            ["role"] = msg.Role
        };

        if (!string.IsNullOrEmpty(msg.ToolCallId))
            msgObj["tool_call_id"] = msg.ToolCallId;

        if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
        {
            msgObj["content"] = msg.Content ?? "";
            JArray toolCallsArray = new JArray();

            foreach (var call in msg.ToolCalls)
            {
                JObject toolObj = new JObject
                {
                    ["id"] = call.Id ?? "",
                    ["type"] = "function"
                };

                JObject functionObj = new JObject
                {
                    ["name"] = call.Name ?? "",
                    ["arguments"] = call.Arguments ?? ""
                };

                toolObj["function"] = functionObj;
                toolCallsArray.Add(toolObj);
            }

            msgObj["tool_calls"] = toolCallsArray;
        }
        else if (msg.Image != null)
        {
            JArray contentArray = new JArray();

            if (!string.IsNullOrEmpty(msg.Content))
            {
                JObject textPart = new JObject
                {
                    ["type"] = "text",
                    ["text"] = msg.Content
                };
                contentArray.Add(textPart);
            }

            if (!string.IsNullOrEmpty(msg.Image))
            {
                string mime = string.IsNullOrEmpty(msg.ImageMime) ? "image/png" : msg.ImageMime;
                JObject imgPart = new JObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JObject
                    {
                        ["url"] = "data:" + mime + ";base64," + msg.Image
                    }
                };
                contentArray.Add(imgPart);
            }

            if (contentArray.Count == 0)
            {
                JObject emptyText = new JObject
                {
                    ["type"] = "text",
                    ["text"] = ""
                };
                contentArray.Add(emptyText);
            }

            msgObj["content"] = contentArray;
        }
        else
        {
            msgObj["content"] = msg.Content ?? "";
        }

        return msgObj;
    }

    LLMCompletionResponse sendMessages(
        List<ChatMessage> conversation,
        List<string> enabledTools,
        Action<string> outputCallback = null,
        Action<ToolRegistry.ToolCall> toolCallCallback = null,
        Action onContentStart = null,
        Action startBlock = null,
        bool outputOnly = false,
        List<string> contextInjections = null)
    {
        return SendHttpRequest(BuildRequestPayload(conversation, enabledTools, contextInjections), outputCallback, toolCallCallback, onContentStart, startBlock, outputOnly);
    }

    private LLMCompletionResponse SendHttpRequest(JObject payload, Action<string> outputCallback = null,
        Action<ToolRegistry.ToolCall> toolCallCallback = null,
        Action onContentStart = null, Action startBlock = null, bool outputOnly = false)
    {
        Action<string> onReasoningChunk = null;
        Action<int> onReasoningSummary = null;
        if (outputCallback != null && !outputOnly)
        {
            bool showThinking = config.GetChatBlockDisplayMode("thinkingdisplay", ChatBlockDisplayMode.Collapsed)
                != ChatBlockDisplayMode.Hidden;
            onReasoningChunk = showThinking ? outputCallback : null;
            if (!showThinking)
            {
                onReasoningSummary = s =>
                {
                    if (startBlock != null) startBlock();
                    outputCallback("[thought for " + s + " second" + (s == 1 ? "" : "s") + "]\n");
                };
            }
        }

        HttpWebRequest request = null;
        StreamActivity activity = new StreamActivity();
        int stallSeconds = config.GetConfigInt("streamstalltimeout", 120);
        int requestTimeoutMs = Math.Max(1, config.GetConfigInt("requesttimeout", 300)) * 1000;
        int finished = 0;
        int stallAborted = 0;

        try
        {
            // A stop clicked during a tool round must not start the next request.
            if (AbortSupport.AbortRequested)
                return new LLMCompletionResponse("generation_aborted", null, "aborted");

            request = (HttpWebRequest)WebRequest.Create($"{config.GetConfigValue("llmserver")}/v1/chat/completions");
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Headers.Add("Authorization", "Bearer " + config.GetConfigValue("apikey"));
            request.Timeout = requestTimeoutMs;

            byte[] payloadBytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
            request.ContentLength = payloadBytes.Length;

            AbortSupport.RegisterRequest(request);

            using (var stream = request.GetRequestStream())
            {
                stream.Write(payloadBytes, 0, payloadBytes.Length);
            }

            if (stallSeconds > 0)
            {
                // Watchdog: abort the request if the stream goes silent for longer than
                // stallSeconds. A stalled model would otherwise block this thread forever
                // (and with it the /save, /load, ... stdin loop).
                Thread stallWatchdog = new Thread(delegate ()
                {
                    try
                    {
                        while (finished == 0 && stallAborted == 0 && activity.Idle() <= TimeSpan.FromSeconds(stallSeconds))
                            Thread.Sleep(250);
                        if (Interlocked.Exchange(ref stallAborted, 1) == 0)
                            try { request.Abort(); } catch { }
                    }
                    catch { }
                }) { IsBackground = true };
                stallWatchdog.Start();
            }

            using (var httpResponse = (HttpWebResponse)request.GetResponse())
            using (var responseStream = httpResponse.GetResponseStream())
            using (var reader = new ActivityReader(new StreamReader(responseStream, Encoding.UTF8), activity))
            {
                LLMCompletionResponse result = SseStreamParser.Parse(reader, outputCallback, onReasoningChunk, onReasoningSummary, toolCallCallback, onContentStart, startBlock);
                AbortSupport.UnregisterRequest(request);
                return result;
            }
        }
        catch (Exception ex)
        {
            AbortSupport.UnregisterRequest(request);

            // Abort was requested by the user (via the control pipe): stop silently.
            if (AbortSupport.AbortRequested)
                return new LLMCompletionResponse("generation_aborted", null, "aborted");

            // The stream went silent and the watchdog aborted the request.
            if (stallAborted == 1)
                return new LLMCompletionResponse(
                    "no stream data for " + stallSeconds + " s (model stalled, request aborted)",
                    null, "request_failed");

            string reason;
            WebException webEx = ex as WebException;
            if (webEx != null && webEx.Response is HttpWebResponse errorResponse)
            {
                using (var errorStream = errorResponse.GetResponseStream())
                using (var errorReader = new StreamReader(errorStream, Encoding.UTF8))
                {
                    string body = errorReader.ReadToEnd();
                    reason = "HTTP " + (int)errorResponse.StatusCode + " " + errorResponse.StatusDescription + ": " + body;
                }
            }
            else
            {
                reason = ex.Message;
            }

            // Try curl fallback for HTTPS connection errors
            string serverUrl = config.GetConfigValue("llmserver") ?? "";
            if (CurlClient.CanFallback(serverUrl, ex))
                return CurlClient.SendRequest(serverUrl, config.GetConfigValue("apikey"), payload, outputCallback, onReasoningChunk, onReasoningSummary, toolCallCallback, onContentStart, startBlock);

            return new LLMCompletionResponse(reason, null, "request_failed");
        }
        finally
        {
            Interlocked.Exchange(ref finished, 1);
        }
    }

    /// <summary>
    /// Console approval prompt routed through ChatOutput so block spacing stays
    /// accurate. Wire format is FormatApprovalMessage + ApprovalPrompt (the GUI
    /// parses the "Run tool:" ... "Approve? (Y/N): " block from stdout).
    /// </summary>
    private static bool CliRequestApproval(string toolName, string arguments)
    {
        ChatOutput.WriteLine(ToolApproval.FormatApprovalMessage(toolName, arguments));
        Console.Out.Flush();

        while (true)
        {
            ChatOutput.Write(ToolApproval.ApprovalPrompt);
            Console.Out.Flush();

            string input = Console.ReadLine();
            ChatOutput.EndInputLine();
            if (input == null)
                continue;

            input = input.Trim();
            if (input.Length == 0)
                continue;

            if (string.Equals(input, "Y", StringComparison.OrdinalIgnoreCase)
                || string.Equals(input, "yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(input, "N", StringComparison.OrdinalIgnoreCase)
                || string.Equals(input, "no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            ChatOutput.WriteLine("Please enter Y or N.");
        }
    }

    /// <summary>
    /// Thread-safe last-activity timestamp fed by the stream stall watchdog.
    /// </summary>
    private sealed class StreamActivity
    {
        private long _lastTicks;

        public StreamActivity()
        {
            _lastTicks = DateTime.UtcNow.Ticks;
        }

        public void Touch()
        {
            Interlocked.Exchange(ref _lastTicks, DateTime.UtcNow.Ticks);
        }

        public TimeSpan Idle()
        {
            return DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastTicks));
        }
    }

    /// <summary>
    /// TextReader that reports every byte read to the stall watchdog.
    /// </summary>
    private sealed class ActivityReader : TextReader
    {
        private readonly TextReader _inner;
        private readonly StreamActivity _activity;

        public ActivityReader(TextReader inner, StreamActivity activity)
        {
            _inner = inner;
            _activity = activity;
        }

        public override int Read()
        {
            int c = _inner.Read();
            if (c != -1)
                _activity.Touch();
            return c;
        }
    }
}
}