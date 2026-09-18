using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace SimpleLLMChatGUI
{
    public partial class MainWindow : Window
    {
        private ProcessHandler processHandler;
        private readonly ImageHandler imageHandler;
        private string _reasoningEffort = "";
        private TokenTracker tokenTracker;
        private ContextMenu reasoningEffortMenu;
        private bool _isGenerating;
        private DateTime _generationStartTime = DateTime.MinValue;
        private bool _startupPromptConsumed;
        private readonly ObservableCollection<ChatTurn> _chatTurns = new ObservableCollection<ChatTurn>();
        private ChatTurn _streamingTurn;

        private const double ChatTurnGapMin = 8;
        private const double ChatTurnGapMax = 24;

        /// <summary>
        /// Bottom gap between chat turns; scales with font size (~1em + 2), clamped.
        /// </summary>
        public static readonly DependencyProperty ChatTurnItemMarginProperty =
            DependencyProperty.Register(
                nameof(ChatTurnItemMargin),
                typeof(Thickness),
                typeof(MainWindow),
                new PropertyMetadata(new Thickness(0, 0, 0, AppConstants.DefaultChatFontSize + 2)));

        public Thickness ChatTurnItemMargin
        {
            get { return (Thickness)GetValue(ChatTurnItemMarginProperty); }
            set { SetValue(ChatTurnItemMarginProperty, value); }
        }

        private static readonly KeyValuePair<string, string>[] ReasoningEffortOptions =
        {
            new KeyValuePair<string, string>("Default", ""),
            new KeyValuePair<string, string>("None", "none"),
            new KeyValuePair<string, string>("Minimal", "minimal"),
            new KeyValuePair<string, string>("Low", "low"),
            new KeyValuePair<string, string>("Medium", "medium"),
            new KeyValuePair<string, string>("High", "high"),
            new KeyValuePair<string, string>("Extra High", "xhigh"),
        };

        public MainWindow()
        {
            InitializeComponent();

            // Initialize image handler and centralize UI updates via events
            imageHandler = new ImageHandler();

            imageHandler.ImageSelected += () =>
            {
                attachButton.ToolTip = "Detach Image";
                attachButton.IsChecked = true;
                attachButton.Background = System.Windows.Media.Brushes.LightBlue;
            };

            imageHandler.ImageDetached += () =>
            {
                attachButton.ToolTip = "Attach Image";
                attachButton.IsChecked = false;
                attachButton.ClearValue(Button.BackgroundProperty);
            };
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            chatList.ItemsSource = _chatTurns;
            ChatTurn.ThinkingExpanderStyle = TryFindResource("ThinkingExpanderStyle") as Style;

            FontHandler.ApplyFontToWindow(this);
            FontHandler.ApplyCodeBlockFontFamily();
            LoadAndApplyFontSize();
            LoadAndApplyColors();
            UpdateReasoningEffortLabel();
            BuildReasoningEffortMenu();
            tokenTracker = new TokenTracker(tokenStatusText);

            // Gate send until the CLI is up; leave input/attach usable meanwhile.
            sendButton.IsEnabled = false;

            // After first paint: Process.Start can take 100–1000ms and would white-out Loaded.
            EventHandler onRendered = null;
            onRendered = (s, args) =>
            {
                ContentRendered -= onRendered;
                OnboardingWizardWindow.ShowIfNeeded(this);
                StartLLMProcess();
                AutoRestoreLastSession();
            };
            ContentRendered += onRendered;
        }

        private void UpdateReasoningEffortLabel()
        {
            string label = ReasoningEffortOptions[0].Key;
            for (int i = 0; i < ReasoningEffortOptions.Length; i++)
            {
                if (string.Equals(ReasoningEffortOptions[i].Value, _reasoningEffort, StringComparison.OrdinalIgnoreCase))
                {
                    label = ReasoningEffortOptions[i].Key;
                    break;
                }
            }
            reasoningEffortLabel.Text = label;
        }

        private void BuildReasoningEffortMenu()
        {
            reasoningEffortMenu = new ContextMenu();
            foreach (KeyValuePair<string, string> option in ReasoningEffortOptions)
            {
                MenuItem item = new MenuItem();
                item.Header = option.Key;
                item.Tag = option.Value;
                item.IsCheckable = true;
                item.Click += reasoningEffortMenuItem_Click;
                reasoningEffortMenu.Items.Add(item);
            }
        }

        private void reasoningEffortLabel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (reasoningEffortMenu == null)
                return;

            foreach (MenuItem item in reasoningEffortMenu.Items)
            {
                string value = item.Tag as string ?? "";
                item.IsChecked = string.Equals(value, _reasoningEffort, StringComparison.OrdinalIgnoreCase);
            }

            reasoningEffortMenu.PlacementTarget = (UIElement)sender;
            reasoningEffortMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            reasoningEffortMenu.IsOpen = true;
        }

        private void reasoningEffortMenuItem_Click(object sender, RoutedEventArgs e)
        {
            MenuItem item = sender as MenuItem;
            if (item == null)
                return;

            _reasoningEffort = item.Tag as string ?? "";
            UpdateReasoningEffortLabel();
            if (processHandler != null && processHandler.IsProcessRunning)
                processHandler.SendReasoningEffort(_reasoningEffort);
        }

        private void StartLLMProcess()
        {
            if (processHandler != null)
            {
                processHandler.Dispose();
                processHandler = null;
            }
            stopToolButton.IsEnabled = false;

            processHandler = new ProcessHandler();
            processHandler.SetAssistantName(GetAssistantName());
            _startupPromptConsumed = false;

            processHandler.OutputReceived += OnOutputReceived;
            processHandler.ErrorOccurred += OnErrorOccurred;
            processHandler.GenerationComplete += OnGenerationComplete;
            processHandler.ApprovalRequested += OnApprovalRequested;
            processHandler.StatusReceived += OnStatusReceived;
            processHandler.RunningToolReceived += OnRunningToolReceived;

            if (tokenTracker != null)
                tokenTracker.Reset();

            // Start the process
            if (!processHandler.StartProcess("SimpleLLMChatCLI.exe"))
            {
                MessageBox.Show("Failed to start LLM process. Please check if SimpleLLMChatCLI.exe exists.");
                return;
            }

            if (!string.IsNullOrEmpty(_reasoningEffort))
                processHandler.SendReasoningEffort(_reasoningEffort);

            SetInputControlsEnabled(true);
            chatInput.Focus();
        }

        private void OnStatusReceived(int tokens)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (tokenTracker != null)
                    tokenTracker.SetTokens(tokens);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OnRunningToolReceived(string toolName)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                stopToolButton.IsEnabled = !string.IsNullOrEmpty(toolName);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OnOutputReceived(string text)
        {
            // BeginInvoke so stdout reading is not gated behind UI paint/scroll work.
            Dispatcher.BeginInvoke((Action)(() =>
            {
                AppendStreamingText(text);
                ScrollChatToEnd();
            }));
        }

        /// <summary>
        /// Each thinking / tool-call block gets its own turn, so the chat list's
        /// item margin separates them the way it separates user and LLM turns.
        /// </summary>
        private void AppendStreamingText(string text)
        {
            string remaining = text;
            while (!string.IsNullOrEmpty(remaining))
            {
                if (_streamingTurn == null)
                {
                    _streamingTurn = AddTurn();
                    _streamingTurn.RoleLabel = GetAssistantName();
                }

                string leftover = _streamingTurn.AppendText(remaining);
                if (leftover == null)
                    return;

                EndStreamingTurn();
                remaining = leftover;
            }
        }

        private void EndStreamingTurn()
        {
            if (_streamingTurn == null)
                return;

            _streamingTurn.Complete();
            if (!_streamingTurn.HasRenderedContent())
                _chatTurns.Remove(_streamingTurn);
            _streamingTurn = null;
        }

        private void OnErrorOccurred(string errorMessage)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                MessageBox.Show(errorMessage, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }));
        }

        private bool OnApprovalRequested(string toolName, string arguments)
        {
            bool approved = false;
            // Run after queued stdout updates so the approval block's leading
            // separator can be removed before the (hidden) GUI prompt is shown.
            Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, (Action)(() =>
            {
                if (_streamingTurn != null)
                {
                    _streamingTurn.TrimTrailingBlankParagraphs();
                    // Keep half of the block separator; the CLI emits the
                    // second newline after receiving the approval response.
                    _streamingTurn.AppendText("\r\n");
                }

                string message = ToolApproval.FormatApprovalMessage(toolName, arguments);
                MessageBoxResult result = MessageBox.Show(
                    message,
                    "Tool Call",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.No);
                approved = result == MessageBoxResult.Yes;
            }));
            return approved;
        }

        private void OnGenerationComplete()
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                // The CLI emits its initial "You:" prompt right after start; that
                // first completion signal is not a real generation finish and must
                // not flip the button back to Send if a send was just issued.
                if (!_startupPromptConsumed)
                {
                    _startupPromptConsumed = true;
                    return;
                }
                EndStreamingTurn();
                if (IsMarkdownParsingEnabled())
                    ApplyMarkdown();
                ExitGeneratingMode();
                chatInput.Focus();
            }));
        }

        /// <summary>
        /// A generation now spans several turns; each tracks what it already
        /// rendered, so processed blocks are not revisited.
        /// </summary>
        private void ApplyMarkdown()
        {
            foreach (ChatTurn turn in _chatTurns)
            {
                turn.ProcessMarkdown();
            }
        }

        private bool IsMarkdownParsingEnabled()
        {
            return App.Config.GetConfigBool("markdownparsing", true);
        }

        private void LoadAndApplyFontSize()
        {
            int fontSize = FontHandler.GetFontSize();
            FontSize = fontSize;
            ChatTurnItemMargin = new Thickness(0, 0, 0,
                Math.Max(ChatTurnGapMin, Math.Min(ChatTurnGapMax, fontSize + 2)));
            FontHandler.ApplyFontSizeToControl(chatList, fontSize);
            FontHandler.ApplyFontSizeToControl(chatInput, fontSize);
            foreach (ChatTurn turn in _chatTurns)
                turn.ApplyFontSize(fontSize);
            for (int i = 0; i < chatList.Items.Count; i++)
            {
                DependencyObject container = chatList.ItemContainerGenerator.ContainerFromIndex(i);
                if (container == null)
                    continue;
                ChatTurnView view = FindVisualChild(container);
                if (view != null)
                    view.ApplyFontSize(fontSize);
            }
        }

        private static ChatTurnView FindVisualChild(DependencyObject root)
        {
            ChatTurnView view = root as ChatTurnView;
            if (view != null)
                return view;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                ChatTurnView found = FindVisualChild(VisualTreeHelper.GetChild(root, i));
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>
        /// The assistant name the CLI prefixes its output with ("LLM" by default).
        /// </summary>
        private string GetAssistantName()
        {
            string assistantName = App.Config.GetConfigValue("assistantname");
            return string.IsNullOrWhiteSpace(assistantName)
                ? AppConstants.DefaultAssistantName
                : assistantName;
        }

        private ChatTurn AddTurn()
        {
            ChatTurn turn = new ChatTurn();
            turn.ApplyFontSize(FontHandler.GetFontSize());
            ApplyDocumentPageWidth(turn);
            _chatTurns.Add(turn);
            return turn;
        }

        private void chatList_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            foreach (ChatTurn turn in _chatTurns)
                ApplyDocumentPageWidth(turn);
        }

        private void ApplyDocumentPageWidth(ChatTurn turn)
        {
            if (turn == null || chatList == null)
                return;

            double width = chatList.ActualWidth
                - SystemParameters.VerticalScrollBarWidth
                - 16;
            if (width > 50)
                turn.SetPageWidth(width);
        }

        private void ScrollChatToEnd()
        {
            if (_chatTurns.Count == 0)
                return;

            // Clear selection before forcing scroll — scrolling while text is
            // highlighted during streaming can freeze the UI thread.
            ClearChatSelection(chatList);

            chatList.ScrollIntoView(_chatTurns[_chatTurns.Count - 1]);

            ScrollViewer scrollViewer = FindScrollViewer(chatList);
            if (scrollViewer != null)
                scrollViewer.ScrollToEnd();
        }

        private static void ClearChatSelection(DependencyObject root)
        {
            RichTextBox richTextBox = root as RichTextBox;
            if (richTextBox != null
                && richTextBox.Selection != null
                && !richTextBox.Selection.IsEmpty)
            {
                TextPointer end = richTextBox.Document.ContentEnd;
                richTextBox.Selection.Select(end, end);
            }
            else
            {
                TextBox textBox = root as TextBox;
                if (textBox != null && textBox.SelectionLength > 0)
                    textBox.Select(0, 0);
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                ClearChatSelection(VisualTreeHelper.GetChild(root, i));
        }

        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer viewer)
                return viewer;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                ScrollViewer child = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (child != null)
                    return child;
            }
            return null;
        }

        private void LoadAndApplyColors()
        {
            if (File.Exists(App.ColorsFileName))
            {
                try
                {
                    var colorSettings = IniFileHandler.LoadIni(App.ColorsFileName);
                    ColorHelper.LoadAndApplyColors(colorSettings);
                }
                catch
                {
                    // On error, do nothing (use system defaults)
                }
            }
            UpdateThemeButton();
        }

        private void UpdateThemeButton()
        {
            string theme = ColorHelper.GetTheme(App.ColorsFileName);
            bool dark = ColorHelper.IsDarkTheme(theme);
            themeButton.Content = dark ? "☾" : "☀";
            themeButton.ToolTip = "Theme: " + ColorHelper.GetThemeDisplayName(theme);
        }

        private void compactButton_Click(object sender, RoutedEventArgs e)
        {
            if (processHandler != null && processHandler.IsProcessRunning)
                processHandler.SendInput("/compactfull");
        }

        private void themeButton_Click(object sender, RoutedEventArgs e)
        {
            string current = ColorHelper.GetTheme(App.ColorsFileName);
            int idx = Array.IndexOf(ColorHelper.ThemeOrder, current);
            string theme = ColorHelper.ThemeOrder[(idx + 1) % ColorHelper.ThemeOrder.Length];
            ColorHelper.SaveTheme(theme, App.ColorsFileName);
            LoadAndApplyColors();
        }


        private void SetInputControlsEnabled(bool enabled)
        {
            // Only used when generation is not running (startup, restart, failure paths).
            if (enabled)
                ExitGeneratingMode();

            attachButton.IsEnabled = enabled;
            chatInput.IsEnabled = enabled;
            sendButton.IsEnabled = enabled;
        }

        /// <summary>
        /// Enter generation mode: the send button becomes an enabled Stop button
        /// (the model cannot be interrupted via stdin, so Stop talks to the CLI's
        /// control pipe instead).
        /// </summary>
        private void EnterGeneratingMode()
        {
            _isGenerating = true;
            _generationStartTime = DateTime.UtcNow;
            attachButton.IsEnabled = false;
            chatInput.IsEnabled = false;
            sendButton.Content = "Stop";
            sendButton.IsEnabled = true;
            saveButton.IsEnabled = false;
            loadButton.IsEnabled = false;
        }

        private void ExitGeneratingMode()
        {
            _isGenerating = false;
            attachButton.IsEnabled = true;
            chatInput.IsEnabled = true;
            sendButton.Content = "Send";
            sendButton.IsEnabled = true;
            saveButton.IsEnabled = true;
            loadButton.IsEnabled = true;
            stopToolButton.IsEnabled = false;
        }

        private void stopToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (processHandler != null && processHandler.IsProcessRunning)
                processHandler.RequestStopTool();
        }

        private void StopGeneration()
        {
            // Ignore the stray second click of a double-click on Send.
            if ((DateTime.UtcNow - _generationStartTime).TotalMilliseconds
                < System.Windows.Forms.SystemInformation.DoubleClickTime)
                return;

            bool stopped = processHandler != null
                && processHandler.IsProcessRunning
                && processHandler.RequestStop();

            if (stopped)
            {
                // Wait for the CLI to return to its prompt before re-enabling input;
                // re-enable once GenerationComplete is raised for the "You:" prompt.
                sendButton.IsEnabled = false;
            }
            else
            {
                // The stop could not be delivered; recover once generation ends.
                ExitGeneratingMode();
            }
        }

        private void sendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isGenerating)
            {
                StopGeneration();
                return;
            }

            string userInput = chatInput.Text;

            ChatTurn userTurn = AddTurn();
            userTurn.Role = ChatTurnRole.User;
            userTurn.RoleLabel = "You";
            userTurn.AppendText(userInput);
            userTurn.Complete();
            _streamingTurn = AddTurn();
            _streamingTurn.RoleLabel = GetAssistantName();
            ScrollChatToEnd();

            // Switch the send button into a Stop button while the model generates.
            EnterGeneratingMode();

            if (processHandler != null && processHandler.IsProcessRunning)
            {
                if (imageHandler?.IsImageAttached == true && !string.IsNullOrEmpty(imageHandler.AttachedImagePath))
                {
                    // Send input with image
                    if (!processHandler.SendInputWithImage(imageHandler.AttachedImagePath, userInput))
                    {
                        MessageBox.Show("Failed to send input with image to the process.");
                        SetInputControlsEnabled(true);
                        return;
                    }

                    // Detach image after sending
                    imageHandler.DetachImage();
                    attachButton.IsChecked = false;
                }
                else
                {
                    // Normal input
                    if (!processHandler.SendInput(userInput))
                    {
                        MessageBox.Show("Failed to send input to the process.");
                        SetInputControlsEnabled(true);
                        return;
                    }
                }

                chatInput.Clear();
            }
            else
            {
                MessageBox.Show("Error: CLI is not running!");
                SetInputControlsEnabled(false);
                chatInput.Clear();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            if (processHandler != null)
            {
                processHandler.Dispose();
            }
        }

        // Fired when the attach button is toggled ON
        private void attachButton_Checked(object sender, RoutedEventArgs e)
        {
            if (!imageHandler.SelectImage())
            {
                attachButton.IsChecked = false;
            }
        }

        // Fired when the attach button is toggled OFF
        private void attachButton_Unchecked(object sender, RoutedEventArgs e)
        {
            if (imageHandler != null)
                imageHandler.DetachImage();
        }

        private void chatInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Send on Enter (without Shift), create new line on Shift+Enter
            if (e.Key == Key.Enter && sendButton.IsEnabled)
            {
                if (Keyboard.Modifiers != ModifierKeys.Shift)
                {
                    e.Handled = true; // prevent the ding sound and new line
                    sendButton_Click(sendButton, new RoutedEventArgs());
                }
                // If Shift+Enter, allow default behavior (new line)
            }
        }

        private void ClearChatAndRestart()
        {
            // Queue the clear operation to run after all pending output has been processed.
            // Use BeginInvoke (fire-and-forget) so the UI thread doesn't pump re-entrantly
            // while waiting — that caused intermittent crashes when clicking Clear.
            Dispatcher.BeginInvoke((Action)(() =>
            {
                EndStreamingTurn();
                _chatTurns.Clear();
                _streamingTurn = null;
                SetInputControlsEnabled(false);
                // Wipe the auto-saved last session so the restarted CLI does
                // not restore it into a freshly cleared chat.
                try
                {
                    SessionStore.Delete(
                        SessionStore.DefaultDirectory(AppDomain.CurrentDomain.BaseDirectory),
                        "lastsession");
                }
                catch { }
                StartLLMProcess();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void saveButton_Click(object sender, RoutedEventArgs e)
        {
            if (processHandler == null || !processHandler.IsProcessRunning)
            {
                MessageBox.Show("CLI is not running.", "Save Session");
                return;
            }

            string sessionsDir = SessionStore.DefaultDirectory(AppDomain.CurrentDomain.BaseDirectory);
            List<SessionStore.SessionInfo> sessions = SessionStore.List(sessionsDir);
            var dialog = new SessionSaveDialog(sessions);
            dialog.Owner = this;
            if (dialog.ShowDialog() != true)
                return;

            string name = dialog.SessionName;
            processHandler.SendInput(string.IsNullOrEmpty(name) ? "/save" : "/save " + name);
        }

        private void loadButton_Click(object sender, RoutedEventArgs e)
        {
            if (processHandler == null || !processHandler.IsProcessRunning)
            {
                MessageBox.Show("CLI is not running.", "Load Session");
                return;
            }

            string sessionsDir = SessionStore.DefaultDirectory(AppDomain.CurrentDomain.BaseDirectory);
            List<SessionStore.SessionInfo> sessions = SessionStore.List(sessionsDir);
            if (sessions.Count == 0)
            {
                MessageBox.Show("No saved sessions found.", "Load Session");
                return;
            }

            var dialog = new SessionLoadDialog(sessions);
            dialog.Owner = this;
            if (dialog.ShowDialog() != true)
                return;

            string sessionName = dialog.SelectedSessionName;
            if (string.IsNullOrEmpty(sessionName))
                return;

            EndStreamingTurn();
            _chatTurns.Clear();
            _streamingTurn = null;
            RenderHistoricTurns(sessionsDir, sessionName);
            EnterGeneratingMode();
            processHandler.SendInput("/load " + sessionName);
        }

        /// <summary>
        /// Draws a saved session's past messages straight into the window so the
        /// user sees the conversation itself, not just the CLI's one-line
        /// "session loaded, N messages" acknowledgement.
        /// </summary>
        private void RenderHistoricTurns(string sessionsDir, string sessionName)
        {
            List<HistoricMessage> loaded = null;
            try
            {
                SessionStore.Load(sessionsDir, sessionName, out loaded);
            }
            catch
            {
                loaded = null;
            }

            if (loaded == null)
                return;

            foreach (HistoricMessage msg in loaded)
            {
                ChatTurn turn = AddTurn();
                if (string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    // User messages stay plain text; the role header carries the name.
                    turn.Role = ChatTurnRole.User;
                    turn.RoleLabel = "You";
                    turn.AppendText(msg.Content);
                }
                else
                {
                    turn.RoleLabel = GetAssistantName();
                    turn.AppendText(msg.Content);
                    if (IsMarkdownParsingEnabled())
                        turn.ProcessMarkdown();
                }
                turn.Complete();
            }
        }

        private void AutoRestoreLastSession()
        {
            try
            {
                string sessionsDir = SessionStore.DefaultDirectory(AppDomain.CurrentDomain.BaseDirectory);
                string sessionFile = Path.Combine(sessionsDir, "lastsession.json");
                if (File.Exists(sessionFile))
                {
                    // Render the previously saved conversation into the window so a
                    // freshly reopened app shows the actual past messages instead of
                    // an empty chat. The CLI itself also restores this session into
                    // its own context, so nothing is lost or overwritten.
                    RenderHistoricTurns(sessionsDir, "lastsession");
                }
            }
            catch
            {
                // Best-effort: never block startup on history rendering failures.
            }
        }

        private sealed class HistoricMessage
        {
            public string Role { get; set; }
            public string Content { get; set; }
        }

        private void clearButton_Click(object sender, RoutedEventArgs e)
        {
            // Kill running process first to stop any new output
            if (processHandler != null)
            {
                processHandler.Dispose();
                processHandler = null;
            }

            ClearChatAndRestart();
        }

        private void optionsButton_Click(object sender, RoutedEventArgs e)
        {
            var optionsDialog = new Options(processHandler);
            optionsDialog.Owner = this;

            if (optionsDialog.ShowDialog() == true)
            {
                App.LoadSettings(); // Reload settings after options dialog saves
                FontHandler.ApplyFontToWindow(this);
                FontHandler.ApplyCodeBlockFontFamily();
                LoadAndApplyFontSize();
                LoadAndApplyColors();
                if (processHandler != null)
                    processHandler.SetAssistantName(GetAssistantName());
                if (tokenTracker != null)
                    tokenTracker.Refresh();

                if (processHandler != null && processHandler.IsProcessRunning)
                    processHandler.SendReload();
                else
                    ClearChatAndRestart();
            }
        }
    }
}