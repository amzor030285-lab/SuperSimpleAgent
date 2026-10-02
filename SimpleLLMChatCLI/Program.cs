using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SimpleLLMChatCLI.RAG;

namespace SimpleLLMChatCLI
{
    internal class Program
    {
        public static ConfigHandler Config;
        public static StatusPipeServer StatusPipe;
        public static ControlPipeServer ControlPipe;

        // Print interactive CLI instructions
        private static readonly string[] ValidReasoningEfforts =
        {
            "none", "minimal", "low", "medium", "high", "xhigh"
        };

        static void PrintCliInstructions()
        {
            ChatOutput.WriteLine("================== SimpleLLMChat ==================");
            ChatOutput.WriteLine("| Commands:                                       |");
            ChatOutput.WriteLine("| /approval <mode>: ask auto config plan          |");
            ChatOutput.WriteLine("| /buildindex: Reconcile knowledge index          |");
            ChatOutput.WriteLine("| /clear: Clear chat context                      |");
            ChatOutput.WriteLine("| /clearindex: Clear knowledge index              |");
            ChatOutput.WriteLine("| /compact: Drop old tool messages                |");
            ChatOutput.WriteLine("| /compactfull: Full compaction (micro+summarize) |");
            ChatOutput.WriteLine("| /delete name: Delete a saved session            |");
            ChatOutput.WriteLine("| /exit: Exit the application                     |");
            ChatOutput.WriteLine("| /image \"path\" prompt: Send an image           |");
            ChatOutput.WriteLine("| /load keyword: Load a saved session             |");
            ChatOutput.WriteLine("| /plan [on|off]: Read-only plan mode             |");
            ChatOutput.WriteLine("| /reasoning [effort]: Set reasoning effort       |");
            ChatOutput.WriteLine("| /reload: Reload configuration                   |");
            ChatOutput.WriteLine("| /rename old new: Rename a saved session         |");
            ChatOutput.WriteLine("| /save [name]: Save current session              |");
            ChatOutput.WriteLine("| /sessions: List saved sessions                  |");
            ChatOutput.WriteLine("===================================================");
        }

        static bool TryParseImageCommand(string input, out string imagePath, out string textPrompt)
        {
            imagePath = null;
            textPrompt = null;

            if (input.Length < 8 || !input.StartsWith("/image "))
                return false;

            int quoteStart = input.IndexOf('"', 7);
            int quoteEnd = input.IndexOf('"', quoteStart + 1);

            if (quoteStart == -1 || quoteEnd == -1)
                return false;

            imagePath = input.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
            textPrompt = (quoteEnd + 1 < input.Length)
                ? input.Substring(quoteEnd + 1).TrimStart(' ', '\t')
                : string.Empty;
            return true;
        }

        static bool IsValidReasoningEffort(string effort)
        {
            foreach (string valid in ValidReasoningEfforts)
            {
                if (string.Equals(valid, effort, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>True for the arguments that turn plan mode on.</summary>
        static bool IsPlanToggleOn(string value)
        {
            return string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True for the arguments that turn plan mode off.</summary>
        static bool IsPlanToggleOff(string value)
        {
            return string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Applies an approval mode and keeps the token/status figures honest.
        /// The tools array and the system prompt both shrink or grow when plan
        /// mode goes on or off, so the cached overhead has to be recomputed.
        /// </summary>
        static void ApplyApprovalMode(LLMClient client, string mode, List<string> enabledTools, List<LLMClient.ChatMessage> conversation)
        {
            bool wasPlan = ToolApproval.IsPlanMode(client.ApprovalMode);
            client.ApprovalMode = mode;
            bool isPlan = ToolApproval.IsPlanMode(mode);

            if (wasPlan != isPlan && conversation != null)
            {
                string marker = isPlan ? PlanMode.EnterPlanMessage() : PlanMode.ExitPlanMessage();

                // Back-to-back toggles (no chat between them) must not stack two
                // synthetic "user" messages: some OpenAI-compatible endpoints reject
                // non-alternating roles, which would break the user's next message.
                LLMClient.ChatMessage last = conversation.Count > 0
                    ? conversation[conversation.Count - 1]
                    : default(LLMClient.ChatMessage);
                if (last.Content == PlanMode.EnterPlanMessage() || last.Content == PlanMode.ExitPlanMessage())
                {
                    LLMClient.ChatMessage replaced = last;
                    replaced.Content = marker;
                    conversation[conversation.Count - 1] = replaced;
                }
                else
                {
                    conversation.Add(new LLMClient.ChatMessage
                    {
                        Role = "user",
                        Content = marker
                    });
                }
            }

            if (enabledTools == null)
                return;

            try
            {
                client.RefreshBaseCharacterOverhead(enabledTools);
            }
            catch (Exception ex)
            {
                // The status figures are cosmetic; never fail the mode change over
                // them, but leave a trace so a persistently stale figure is explainable.
                Console.Error.WriteLine("note: could not refresh context overhead: " + ex.Message);
            }
        }

        /// <summary>
        /// Resolves a /load argument to an existing session name: exact match first,
        /// then a case-insensitive prefix match on the listed sessions. Returns null
        /// when nothing matches.
        /// </summary>
        static string ResolveSessionName(string sessionsDir, string keyword)
        {
            keyword = (keyword ?? string.Empty).Trim();
            List<SessionStore.SessionInfo> sessions = SessionStore.List(sessionsDir);
            if (sessions.Count == 0)
                return null;

            if (keyword.Length > 0)
            {
                foreach (SessionStore.SessionInfo info in sessions)
                {
                    if (string.Equals(info.Name, keyword, StringComparison.OrdinalIgnoreCase))
                        return info.Name;
                }
                foreach (SessionStore.SessionInfo info in sessions)
                {
                    if (info.Name.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                        return info.Name;
                }
                return null;
            }

            // No keyword: load the most recent session.
            return sessions[0].Name;
        }

        static void PrintSessionList(string sessionsDir)
        {
            List<SessionStore.SessionInfo> sessions = SessionStore.List(sessionsDir);
            if (sessions.Count == 0)
            {
                ChatOutput.WriteLine("No saved sessions.");
                return;
            }

            foreach (SessionStore.SessionInfo info in sessions)
            {
                DateTime savedUtc = SessionStore.EffectiveSavedAtUtc(info);
                string when = savedUtc != DateTime.MinValue
                    ? savedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : "unknown";
                double kb = (double)info.SizeBytes / 1024.0;
                ChatOutput.WriteLine("  " + info.Name + "  (" + when + ", " + info.MessageCount + " messages, " + kb.ToString("F1") + " KB)");
            }
        }

        static void Main(string[] args)
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;
            TlsConfig.EnsureModernProtocols();

            // Load configuration
            Config = new ConfigHandler(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "LLMSettings.ini"));

            // Always-on status pipe for GUI (and optional external monitors)
            StatusPipe = new StatusPipeServer();
            StatusPipe.Start();

            // Control pipe: lets the GUI abort the current generation.
            ControlPipe = new ControlPipeServer();
            ControlPipe.Start();

            try
            {
                Run(args);
            }
            finally
            {
                StatusPipe.Dispose();
                StatusPipe = null;
                ControlPipe.Dispose();
                ControlPipe = null;
            }
        }

        static void Run(string[] args)
        {
            // Initialize tool registry and load tools from tools/ directory
            ToolRegistry registry = new ToolRegistry(Config);
            string toolsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");
            registry.LoadToolsFromDirectory(toolsDir);

            // Initialize LLMClient
            LLMClient client = new LLMClient(Config, registry);

            // Auto-RAG index check on launch when enabled
            RagHost.Initialize(Config);

            // Get enabled tools
            List<string> enabledTools = Config.GetConfigList("tools");

            // Get tools requiring approval
            List<string> toolsRequiringApproval = Config.GetConfigList(
                "toolsrequiringapproval", AppConstants.DefaultToolsRequiringApproval);

            // Tool approval mode: ask (confirm every call), auto (never confirm),
            // config (confirm only the tools listed above).
            client.ApprovalMode = ToolApproval.NormalizeMode(Config.GetConfigValue("approvalmode"))
                ?? AppConstants.DefaultApprovalMode;

            string assistantName = Config.GetConfigValue("assistantname");
            if (string.IsNullOrWhiteSpace(assistantName))
                assistantName = AppConstants.DefaultAssistantName;

            // Conversation storage
            List<LLMClient.ChatMessage> conversation = new List<LLMClient.ChatMessage>();

            // Token status starts at 0; base overhead is measured on the first real request.
            client.PublishStatusTokens(0);

            string sessionsDir = SessionStore.DefaultDirectory(AppDomain.CurrentDomain.BaseDirectory);

            // Restore the most recent session so a freshly reopened app picks up
            // where the user left off instead of silently starting a blank chat
            // (which previously overwrote "lastsession" on the first new message).
            List<LLMClient.ChatMessage> restored = null;
            if (SessionStore.Load(sessionsDir, "lastsession", out restored)
                && restored.Count > 0)
            {
                conversation.AddRange(restored);
                ChatOutput.WriteLine("\n-- restored lastsession: " + restored.Count + " messages --");
            }

            bool showBanners = true;

            // Check if any command-line arguments were provided
            if (args.Length > 0)
            {
                // List to hold parts of the text prompt
                List<string> promptParts = new List<string>();
                // Flag to indicate if only output should be shown
                bool outputOnly = false;
                // String to store image
                string base64Image = null;
                string base64ImageMime = null;

                // Loop over each command-line argument
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];

                    if (arg == "--no-banners")
                    {
                        showBanners = false;
                        continue;
                    }

                    // Check for output-only flag
                    if (arg == "-o" || arg == "--output-only")
                    {
                        outputOnly = true;
                        continue;
                    }

                    if (arg == "--reasoning-effort")
                    {
                        if (i + 1 >= args.Length)
                        {
                            Console.Error.WriteLine("Error: --reasoning-effort flag requires a value.");
                            return;
                        }

                        string effort = args[++i];
                        if (!IsValidReasoningEffort(effort))
                        {
                            Console.Error.WriteLine("Error: Invalid reasoning effort. Valid values: none, minimal, low, medium, high, xhigh.");
                            return;
                        }

                        client.ReasoningEffort = effort;
                        continue;
                    }

                    if (arg == "--approval")
                    {
                        if (i + 1 >= args.Length)
                        {
                            Console.Error.WriteLine("Error: --approval flag requires a value.");
                            return;
                        }

                        string mode = ToolApproval.NormalizeMode(args[++i]);
                        if (mode == null)
                        {
                            Console.Error.WriteLine("Error: Invalid approval mode. Valid values: ask, auto, config, plan.");
                            return;
                        }

                        client.ApprovalMode = mode;
                        continue;
                    }

                    // Check for image flag
                    if (arg == "--image")
                    {
                        // Ensure there is a next argument for the image path
                        if (i + 1 >= args.Length)
                        {
                            Console.Error.WriteLine("Error: --image flag requires a file path.");
                            return;
                        }

                        string imagePath = args[++i];

                        try
                        {
                            base64Image = ImageEncoder.ImageFileToBase64(imagePath, out base64ImageMime);
                        }
                        catch (Exception e)
                        {
                            Console.Error.WriteLine("Error processing image: " + e.Message);
                            return;
                        }
                        continue;
                    }

                    // If not a flag, treat argument as part of the text prompt
                    promptParts.Add(arg);
                }

                // Combine all prompt parts into a single string separated by spaces
                string textPrompt = string.Join(" ", promptParts.ToArray());

                bool runNonInteractive = promptParts.Count > 0 || base64Image != null || outputOnly;
                if (runNonInteractive)
                {
                    client.ProcessConversation(
                        conversation,
                        textPrompt,
                        base64Image,
                        base64ImageMime,
                        assistantName,
                        enabledTools,
                        toolsRequiringApproval,
                        outputOnly);
                    return;
                }
            }

            if (showBanners)
            {
                PrintCliInstructions();
            }

            while (true)
            {
                // Pad to exactly one blank line before the prompt, regardless of
                // how the previous turn's output ended.
                ChatOutput.StartBlock();
                ChatOutput.Write("You: ");
                if (StatusPipe != null)
                    StatusPipe.PublishReady();
                string userInput = Console.ReadLine();
                ChatOutput.EndInputLine();

                if (userInput == null)
                {
                    // No Enter was echoed; don't pad the re-prompt.
                    ChatOutput.MarkSeparated();
                    continue;
                }

                // A new turn starts fresh: clear any stop requested during the previous one.
                AbortSupport.Reset();

                userInput = userInput.Replace("<<NEWLINE>>", "\n");

                if (userInput == "/exit")
                    break;

                if (userInput == "/clear")
                {
                    conversation.Clear();
                    // Also wipe the auto-saved last session on disk, otherwise
                    // the next startup (or a restarted GUI CLI process) would
                    // restore the conversation the user just cleared.
                    SessionStore.Delete(sessionsDir, "lastsession");
                    client.PublishStatusTokens(client.GetBaseCharacterOverhead());
                    if (showBanners)
                    {
                        ChatOutput.WriteLine("Context cleared.\n");
                        PrintCliInstructions();
                    }
                    continue;
                }

                if (userInput == "/reload")
                {
                    Config.LoadConfig(Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "LLMSettings.ini"));
                    registry.Clear();
                    registry.LoadToolsFromDirectory(toolsDir);
                    enabledTools = Config.GetConfigList("tools");
                    toolsRequiringApproval = Config.GetConfigList(
                        "toolsrequiringapproval", AppConstants.DefaultToolsRequiringApproval);
                    client.ApprovalMode = ToolApproval.NormalizeMode(Config.GetConfigValue("approvalmode"))
                        ?? AppConstants.DefaultApprovalMode;
                    assistantName = Config.GetConfigValue("assistantname");
                    if (string.IsNullOrWhiteSpace(assistantName))
                        assistantName = AppConstants.DefaultAssistantName;
                    RagHost.OnConfigReloaded(Config);
                    client.RefreshBaseCharacterOverhead(enabledTools);
                    client.PublishStatusTokens(
                        LLMClient.GetConversationCharacterCount(conversation) + client.GetBaseCharacterOverhead());

                    if (showBanners)
                        ChatOutput.WriteLine("Config reloaded.\n");
                    else
                        ChatOutput.MarkSeparated();
                    continue;
                }

                if (userInput == "/buildindex")
                {
                    RagHost.BuildIndex();
                    ChatOutput.MarkSeparated();
                    continue;
                }

                if (userInput == "/clearindex")
                {
                    RagHost.ClearIndex();
                    ChatOutput.MarkSeparated();
                    continue;
                }

                if (userInput == "/reasoning" || userInput.StartsWith("/reasoning "))
                {
                    string effort = userInput.Length > 11 ? userInput.Substring(11).Trim() : string.Empty;
                    if (string.IsNullOrEmpty(effort) || IsValidReasoningEffort(effort))
                        client.ReasoningEffort = string.IsNullOrEmpty(effort) ? null : effort;
                    else
                        Console.Error.WriteLine("Error: Invalid reasoning effort. Valid values: none, minimal, low, medium, high, xhigh.");

                    // Silent command (the GUI sends this programmatically): produce no
                    // stdout, and don't pad the re-prompt with a separator newline.
                    ChatOutput.MarkSeparated();
                    continue;
                }

                if (userInput == "/approval" || userInput.StartsWith("/approval "))
                {
                    string requestedMode = userInput.Length > 9 ? userInput.Substring(9).Trim() : string.Empty;
                    if (string.IsNullOrEmpty(requestedMode))
                    {
                        ChatOutput.WriteLine("Approval mode: " + client.ApprovalMode);
                    }
                    else
                    {
                        string mode = ToolApproval.NormalizeMode(requestedMode);
                        if (mode == null)
                            Console.Error.WriteLine("Error: Invalid approval mode. Valid values: ask, auto, config, plan.");
                        else
                            ApplyApprovalMode(client, mode, enabledTools, conversation);
                    }

                    ChatOutput.MarkSeparated();
                    continue;
                }

                // Plan mode is the /approval plan state under a short name, because
                // typing "/plan on" is what you reach for. /plan with no argument
                // reports the state; /plan off lands on ask (same as the GUI button).
                if (userInput == "/plan" || userInput.StartsWith("/plan "))
                {
                    string requested = userInput.Length > 5 ? userInput.Substring(5).Trim() : string.Empty;
                    if (string.IsNullOrEmpty(requested))
                    {
                        ChatOutput.WriteLine(ToolApproval.IsPlanMode(client.ApprovalMode)
                            ? "Plan mode: on (read-only, plan goes to " + PlanMode.PlanFileName + ")"
                            : "Plan mode: off (approval mode is " + client.ApprovalMode + ")");
                    }
                    else if (IsPlanToggleOn(requested))
                    {
                        ApplyApprovalMode(client, PlanMode.ModePlan, enabledTools, conversation);
                    }
                    else if (IsPlanToggleOff(requested))
                    {
                        // Same landing state as the GUI button cycle, which goes
                        // plan -> ask. ask is the most conservative approval mode,
                        // so leaving plan never silently loosens anything.
                        ApplyApprovalMode(client, ToolApproval.ModeAsk, enabledTools, conversation);
                    }
                    else
                    {
                        Console.Error.WriteLine("Error: Invalid plan argument. Valid values: on, off.");
                    }

                    ChatOutput.MarkSeparated();
                    continue;
                }

                if (userInput == "/compact")
                {
                    int removed = LLMClient.MicroCompact(conversation, 2);
                    if (removed > 0)
                        ChatOutput.WriteLine("Compaction: removed " + removed + " old tool messages.");
                    else
                        ChatOutput.WriteLine("Compaction: nothing to remove.");
                    client.PublishStatusTokens(
                        LLMClient.GetConversationCharacterCount(conversation) + client.GetBaseCharacterOverhead());
                    ChatOutput.MarkSeparated();
                    continue;
                }

                if (userInput == "/compactfull")
                {
                    client.PerformFullCompaction(conversation);
                    client.PublishStatusTokens(
                        LLMClient.GetConversationCharacterCount(conversation) + client.GetBaseCharacterOverhead());
                    ChatOutput.MarkSeparated();
                    continue;
                }

                if (userInput == "/save" || userInput.StartsWith("/save "))
                {
                    string name = userInput.Length > 6 ? userInput.Substring(6).Trim() : string.Empty;
                    name = SessionStore.SanitizeName(name);
                    if (string.IsNullOrEmpty(name) || name == "session")
                    {
                        name = "session_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    }

                    string fullPath;
                    if (SessionStore.Save(sessionsDir, name, conversation, out fullPath))
                        ChatOutput.WriteLine("Session saved: " + name);
                    else
                        ChatOutput.WriteLine("Error: could not save session '" + name + "'.");
                    ChatOutput.MarkSeparated();
                    continue;
                }

                if (userInput == "/load" || userInput.StartsWith("/load "))
                {
                    string keyword = userInput.Length > 6 ? userInput.Substring(6).Trim() : string.Empty;
                    string sessionName = ResolveSessionName(sessionsDir, keyword);
                    if (sessionName == null)
                    {
                        ChatOutput.WriteLine("Error: session not found" + (keyword.Length > 0 ? " for '" + keyword + "'." : "."));
                        ChatOutput.MarkSeparated();
                        continue;
                    }

                    List<LLMClient.ChatMessage> loaded;
                    if (!SessionStore.Load(sessionsDir, sessionName, out loaded))
                    {
                        ChatOutput.WriteLine("Error: could not read session '" + sessionName + "'.");
                        ChatOutput.MarkSeparated();
                        continue;
                    }

                    conversation.Clear();
                    conversation.AddRange(loaded);
                    ChatOutput.WriteLine("Session loaded: " + sessionName + " (" + loaded.Count + " messages).");
                    client.PublishStatusTokens(
                        LLMClient.GetConversationCharacterCount(conversation) + client.GetBaseCharacterOverhead());
                    ChatOutput.MarkSeparated();
                    continue;
                }

                if (userInput == "/sessions")
                {
                    PrintSessionList(sessionsDir);
                    ChatOutput.MarkSeparated();
                    continue;
                }

                if (userInput == "/delete" || userInput.StartsWith("/delete "))
                {
                    string name = userInput.Length > 8 ? userInput.Substring(8).Trim() : string.Empty;
                    if (name.Length == 0)
                    {
                        ChatOutput.WriteLine("Usage: /delete name");
                        ChatOutput.MarkSeparated();
                        continue;
                    }
                    if (SessionStore.Delete(sessionsDir, name))
                        ChatOutput.WriteLine("Session deleted: " + SessionStore.SanitizeName(name));
                    else
                        ChatOutput.WriteLine("Error: no session named '" + SessionStore.SanitizeName(name) + "'.");
                    ChatOutput.MarkSeparated();
                    continue;
                }

                if (userInput == "/rename" || userInput.StartsWith("/rename "))
                {
                    string rest = userInput.Length > 8 ? userInput.Substring(8).Trim() : string.Empty;
                    int space = rest.IndexOf(' ');
                    if (space <= 0)
                    {
                        ChatOutput.WriteLine("Usage: /rename old-name new-name");
                        ChatOutput.MarkSeparated();
                        continue;
                    }
                    string oldName = rest.Substring(0, space).Trim();
                    string newName = rest.Substring(space + 1).Trim();
                    if (oldName.Length == 0 || newName.Length == 0)
                    {
                        ChatOutput.WriteLine("Usage: /rename old-name new-name");
                        ChatOutput.MarkSeparated();
                        continue;
                    }
                    string error = SessionStore.Rename(sessionsDir, oldName, newName);
                    if (error == null)
                        ChatOutput.WriteLine("Session renamed: " + SessionStore.SanitizeName(newName));
                    else
                        ChatOutput.WriteLine("Error: " + error);
                    ChatOutput.MarkSeparated();
                    continue;
                }

                string textPrompt = null;
                string imageBase64 = null;
                string imageBase64Mime = null;

                string imagePath;
                if (TryParseImageCommand(userInput, out imagePath, out textPrompt))
                {
                    try
                    {
                        imageBase64 = ImageEncoder.ImageFileToBase64(imagePath, out imageBase64Mime);
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine("Error processing image: " + e.Message);
                        continue;
                    }
                }
                else
                {
                    // Regular text message
                    textPrompt = userInput;
                }

                client.ProcessConversation(conversation,
                                textPrompt,
                                imageBase64,
                                imageBase64Mime,
                                assistantName,
                                enabledTools,
                                toolsRequiringApproval,
                                false);

                // Keep the last session always available: after every completed turn
                // the current conversation is mirrored into a reserved "lastsession"
                // file so closing the app / starting a new chat never loses it.
                // The mirror is size-capped (oldest messages dropped first) so the
                // file cannot grow without bound in long chats.
                if (conversation != null && conversation.Count > 0)
                    SessionStore.SaveCapped(sessionsDir, "lastsession", conversation,
                        SessionStore.MaxLastSessionBytes, out _);
            }
        }
    }
}
