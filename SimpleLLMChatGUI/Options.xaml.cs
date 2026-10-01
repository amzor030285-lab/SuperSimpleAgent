using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json.Linq;

namespace SimpleLLMChatGUI
{
    public partial class Options : Window, INotifyPropertyChanged
    {
        private string _serverUrl;
        private string _chatPath;
        private string _apiKey;
        private string _model;
        private string _sysPrompt;
        private int _contextWindowSize;
        private bool _repetitionGuardEnabled;
        private string _assistantName;
        private string _thinkingDisplayModeText = "Collapsed";
        private string _toolCallDisplayModeText = "Collapsed";
        private string _toolOutputDisplayModeText = "Shown";
        private bool _markdownParsing;
        private string _codeFontFamily;
        private string _customFontFamily;
        private int _chatFontSize;

        // RAG
        private bool _ragEnabled;
        private string _ragKnowledgePath;
        private int _ragMaxResults;
        private int _indexChunkLines;
        private int _indexChunkOverlap;
        private int _ragMaxSnippetLength;
        private string _ragRetrieveMode;
        private string _ragAllowedExtensions;
        private string _embeddingsEndpoint;
        private string _embeddingsModel;
        private string _embeddingsApiKey;
        private readonly ProcessHandler _processHandler;

        /// <summary>Theme currently persisted in colors.ini; restored if the dialog is cancelled.</summary>
        private string _savedTheme;

        public event PropertyChangedEventHandler PropertyChanged;

        private readonly List<string> _availableTools;
        private readonly List<string> _systemFonts;
        private static readonly List<string> DisplayModeOptionList =
            new List<string> { "Shown", "Collapsed", "Hidden" };

        // Dynamic tool options loaded from manifests
        private List<ToolOptionDefinition> _toolOptions;
        private readonly Dictionary<string, FrameworkElement> _toolOptionControls = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
        private List<ScrollViewer> _toolGroupPages = new List<ScrollViewer>();

        // Per-tool timeout controls (keyed by tool name)
        private readonly Dictionary<string, TextBox> _toolTimeoutControls = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);

        private readonly string _toolsDir;

        public List<string> AvailableTools
        {
            get { return _availableTools; }
        }

        public List<string> SystemFonts
        {
            get { return _systemFonts; }
        }

        public string ServerURL
        {
            get { return _serverUrl; }
            set { _serverUrl = value; OnPropertyChanged(nameof(ServerURL)); }
        }

        /// <summary>
        /// Explicit chat-completions path from the chatpath key. Empty means the CLI
        /// detects it from the server URL, which is what almost every provider needs.
        /// </summary>
        public string ChatPath
        {
            get { return _chatPath; }
            set { _chatPath = value; OnPropertyChanged(nameof(ChatPath)); }
        }

        public string ApiKey
        {
            get { return _apiKey; }
            set { _apiKey = value; OnPropertyChanged(nameof(ApiKey)); }
        }

        public string Model
        {
            get { return _model; }
            set { _model = value; OnPropertyChanged(nameof(Model)); }
        }

        public string SysPrompt
        {
            get { return _sysPrompt; }
            set { _sysPrompt = value; OnPropertyChanged(nameof(SysPrompt)); }
        }

        public int ContextWindowSize
        {
            get { return _contextWindowSize; }
            set { _contextWindowSize = value; OnPropertyChanged(nameof(ContextWindowSize)); }
        }

        public bool RepetitionGuardEnabled
        {
            get { return _repetitionGuardEnabled; }
            set { _repetitionGuardEnabled = value; OnPropertyChanged(nameof(RepetitionGuardEnabled)); }
        }

        public string AssistantName
        {
            get { return _assistantName; }
            set { _assistantName = value; OnPropertyChanged(nameof(AssistantName)); }
        }

        public List<string> DisplayModeOptions
        {
            get { return DisplayModeOptionList; }
        }

        public string ThinkingDisplayModeText
        {
            get { return _thinkingDisplayModeText; }
            set { _thinkingDisplayModeText = value; OnPropertyChanged(nameof(ThinkingDisplayModeText)); }
        }

        public string ToolCallDisplayModeText
        {
            get { return _toolCallDisplayModeText; }
            set { _toolCallDisplayModeText = value; OnPropertyChanged(nameof(ToolCallDisplayModeText)); }
        }

        public string ToolOutputDisplayModeText
        {
            get { return _toolOutputDisplayModeText; }
            set { _toolOutputDisplayModeText = value; OnPropertyChanged(nameof(ToolOutputDisplayModeText)); }
        }

        public bool MarkdownParsing
        {
            get { return _markdownParsing; }
            set { _markdownParsing = value; OnPropertyChanged(nameof(MarkdownParsing)); }
        }

        public string CodeFontFamily
        {
            get { return _codeFontFamily; }
            set { _codeFontFamily = value; OnPropertyChanged(nameof(CodeFontFamily)); }
        }

        public string CustomFontFamily
        {
            get { return _customFontFamily; }
            set { _customFontFamily = value; OnPropertyChanged(nameof(CustomFontFamily)); }
        }

        public int ChatFontSize
        {
            get { return _chatFontSize; }
            set { _chatFontSize = value; OnPropertyChanged(nameof(ChatFontSize)); }
        }

        public bool RagEnabled
        {
            get { return _ragEnabled; }
            set { _ragEnabled = value; OnPropertyChanged(nameof(RagEnabled)); }
        }

        public string RagKnowledgePath
        {
            get { return _ragKnowledgePath; }
            set { _ragKnowledgePath = value; OnPropertyChanged(nameof(RagKnowledgePath)); }
        }

        public int RagMaxResults
        {
            get { return _ragMaxResults; }
            set { _ragMaxResults = value; OnPropertyChanged(nameof(RagMaxResults)); }
        }

        public int IndexChunkLines
        {
            get { return _indexChunkLines; }
            set { _indexChunkLines = value; OnPropertyChanged(nameof(IndexChunkLines)); }
        }

        public int IndexChunkOverlap
        {
            get { return _indexChunkOverlap; }
            set { _indexChunkOverlap = value; OnPropertyChanged(nameof(IndexChunkOverlap)); }
        }

        public int RagMaxSnippetLength
        {
            get { return _ragMaxSnippetLength; }
            set { _ragMaxSnippetLength = value; OnPropertyChanged(nameof(RagMaxSnippetLength)); }
        }

        public string RagRetrieveMode
        {
            get { return _ragRetrieveMode; }
            set { _ragRetrieveMode = value; OnPropertyChanged(nameof(RagRetrieveMode)); }
        }

        public string RagAllowedExtensions
        {
            get { return _ragAllowedExtensions; }
            set { _ragAllowedExtensions = value; OnPropertyChanged(nameof(RagAllowedExtensions)); }
        }

        public string EmbeddingsEndpoint
        {
            get { return _embeddingsEndpoint; }
            set { _embeddingsEndpoint = value; OnPropertyChanged(nameof(EmbeddingsEndpoint)); }
        }

        public string EmbeddingsModel
        {
            get { return _embeddingsModel; }
            set { _embeddingsModel = value; OnPropertyChanged(nameof(EmbeddingsModel)); }
        }

        public string EmbeddingsApiKey
        {
            get { return _embeddingsApiKey; }
            set { _embeddingsApiKey = value; OnPropertyChanged(nameof(EmbeddingsApiKey)); }
        }

        public Options(ProcessHandler processHandler)
        {
            _processHandler = processHandler;
            _savedTheme = ColorHelper.GetTheme(App.ColorsFileName);
            InitializeComponent();
            DataContext = this;

            _toolsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");

            // Discover available tools from manifests in the tools/ directory
            _availableTools = LoadAvailableToolsFromManifests();

            // Discover tool options from manifests
            _toolOptions = ToolOptionsRegistry.LoadOptionsFromDirectory(_toolsDir);

            // Load system fonts
            _systemFonts = Fonts.SystemFontFamilies
                .Select(f => f.Source)
                .OrderBy(f => f)
                .ToList();
            _systemFonts.Insert(0, "Default");

        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentSettings();

            DialogResult = true;
            Close();
        }


        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ChooseColorsButton_Click(object sender, RoutedEventArgs e)
        {
            var colorsForm = new ColorsForm
            {
                Owner = this
            };
            colorsForm.ShowDialog();
        }

        private void ModelListButton_Click(object sender, RoutedEventArgs e)
        {
            string baseUrl = ServerURL != null ? ServerURL.Trim() : string.Empty;
            string picked = ModelChooserDialog.Pick(
                this,
                baseUrl,
                ApiKeyPasswordBox.Password,
                Model,
                "Enter an LLM server URL before listing models.");
            if (picked != null)
                Model = picked;
        }

        private void EmbeddingsModelListButton_Click(object sender, RoutedEventArgs e)
        {
            string baseUrl = EmbeddingsEndpoint;
            if (string.IsNullOrEmpty(baseUrl))
                baseUrl = ServerURL;
            if (baseUrl != null)
                baseUrl = baseUrl.Trim();

            string apiKey = EmbeddingsApiKeyPasswordBox.Password;
            if (string.IsNullOrEmpty(apiKey))
                apiKey = ApiKeyPasswordBox.Password;

            string picked = ModelChooserDialog.Pick(
                this,
                baseUrl,
                apiKey,
                EmbeddingsModel,
                "Enter an embeddings endpoint (or configure an LLM server) before listing models.");
            if (picked != null)
                EmbeddingsModel = picked;
        }

        private void CategoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AppearancePage == null) return;

            AppearancePage.Visibility = Visibility.Collapsed;
            SystemPage.Visibility = Visibility.Collapsed;
            ToolsPage.Visibility = Visibility.Collapsed;
            if (RagPage != null)
                RagPage.Visibility = Visibility.Collapsed;
            foreach (var page in _toolGroupPages)
                page.Visibility = Visibility.Collapsed;

            switch (CategoryListBox.SelectedIndex)
            {
                case 0: AppearancePage.Visibility = Visibility.Visible; break;
                case 1:
                    if (RagPage != null)
                        RagPage.Visibility = Visibility.Visible;
                    break;
                case 2: SystemPage.Visibility = Visibility.Visible; break;
                case 3: ToolsPage.Visibility = Visibility.Visible; break;
                default:
                    int toolIdx = CategoryListBox.SelectedIndex - 4;
                    if (toolIdx >= 0 && toolIdx < _toolGroupPages.Count)
                        _toolGroupPages[toolIdx].Visibility = Visibility.Visible;
                    break;
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            FontHandler.ApplyFontToWindow(this);

            foreach (string themeKey in ColorHelper.ThemeOrder)
                ThemeComboBox.Items.Add(ColorHelper.GetThemeDisplayName(themeKey));

            var config = App.Config;
            LoadSettings(config);
            LoadToolSelections(config);
            BuildToolOptionsUI(config);
            BuildToolTimeoutsUI(config);
            RefreshPresetCombo(null);

            IndexingStatusHub.Updated += OnIndexingStatusUpdated;
            RefreshIndexingStatusUi();
                Closed += Options_Closed;
            }

        /// <summary>
        /// Repopulates the preset list from disk. Pass a name to select it afterwards.
        /// The name box is a separate input and is left alone here.
        /// </summary>
        private void RefreshPresetCombo(string selectName)
        {
            List<string> names = PresetStore.Names(PresetStore.Load(PresetStore.DefaultPath));
            PresetComboBox.ItemsSource = names;
            PresetComboBox.SelectedItem =
                (string.IsNullOrEmpty(selectName) || !names.Contains(selectName)) ? null : selectName;
        }

        private void PresetLoadButton_Click(object sender, RoutedEventArgs e)
        {
            string selected = PresetComboBox.SelectedItem as string;
            if (string.IsNullOrEmpty(selected))
            {
                MessageBox.Show(this, "Select a preset to load.", "Presets", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            List<LlmPreset> presets = PresetStore.Load(PresetStore.DefaultPath);
            int index = PresetStore.FindIndex(presets, selected);
            if (index < 0)
            {
                MessageBox.Show(this, "That preset no longer exists.", "Presets", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshPresetCombo(null);
                return;
            }

            ApplyPreset(presets[index]);
        }

        /// <summary>
        /// Copies the preset into the System and RAG sections and applies it. sysprompt,
        /// tools, appearance and the RAG index are left exactly as they are: they are
        /// the user's, not the model's.
        /// </summary>
        private void ApplyPreset(LlmPreset preset)
        {
            ServerURL = preset.ServerUrl;
            ChatPath = preset.ChatPath;
            Model = preset.Model;
            ContextWindowSize = preset.ContextWindowSize;
            EmbeddingsEndpoint = preset.EmbeddingsEndpoint;
            EmbeddingsModel = preset.EmbeddingsModel;

            // The password boxes are not bound and SaveCurrentSettings reads them,
            // so they have to be filled before saving.
            ApiKeyPasswordBox.Password = preset.ApiKey;
            EmbeddingsApiKeyPasswordBox.Password = preset.EmbeddingsApiKey;

            SaveCurrentSettings();
            App.LoadSettings();

            if (_processHandler != null && _processHandler.IsProcessRunning)
                _processHandler.SendReload();
        }

        private void PresetSaveButton_Click(object sender, RoutedEventArgs e)
        {
            string name = (PresetNameTextBox.Text ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(this, "Type a name for the preset in the \"Save current settings as\" box first.", "Presets", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            List<LlmPreset> presets = PresetStore.Load(PresetStore.DefaultPath);
            int existingIndex = PresetStore.FindIndex(presets, name);
            if (existingIndex >= 0)
            {
                MessageBoxResult confirm = MessageBox.Show(
                    this, "Overwrite the preset '" + name + "'?", "Presets", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                    return;
            }

            // Snapshot what is on screen, not what was last saved.
            ApiKey = ApiKeyPasswordBox.Password;
            EmbeddingsApiKey = EmbeddingsApiKeyPasswordBox.Password;

            var preset = new LlmPreset
            {
                Name = name,
                ServerUrl = ServerURL ?? string.Empty,
                ApiKey = ApiKey,
                ChatPath = ChatPath ?? string.Empty,
                Model = Model ?? string.Empty,
                ContextWindowSize = ContextWindowSize,
                EmbeddingsEndpoint = EmbeddingsEndpoint ?? string.Empty,
                EmbeddingsApiKey = EmbeddingsApiKey,
                EmbeddingsModel = EmbeddingsModel ?? string.Empty,
            };

            if (existingIndex >= 0)
                presets[existingIndex] = preset;
            else
                presets.Add(preset);

            PresetStore.Save(PresetStore.DefaultPath, presets);
            PresetNameTextBox.Text = string.Empty;
            RefreshPresetCombo(name);
        }

        private void PresetDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            string selected = PresetComboBox.SelectedItem as string;
            if (string.IsNullOrEmpty(selected))
            {
                MessageBox.Show(this, "Select a preset to delete.", "Presets", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MessageBoxResult confirm = MessageBox.Show(
                this, "Delete the preset '" + selected + "'?", "Presets", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
                return;

            PresetStore.Delete(PresetStore.DefaultPath, selected);
            RefreshPresetCombo(null);
        }


        private void LoadSettings(ConfigHandler config)
        {
            ServerURL = config.GetConfigValue("llmserver");
            ChatPath = config.GetConfigValue(EndpointUrl.ChatPathKey);
            ApiKey = config.GetConfigValue("apikey");
            Model = config.GetConfigValue("model");
            SysPrompt = ConfigHandler.DecodeStoredPrompt(config.GetConfigValue("sysprompt"));
            ContextWindowSize = config.GetConfigInt("contextWindowSize", 0);
            // Same key and same default as the CLI reads in LLMClient, so the box
            // cannot show one state while the tool runs with another.
            RepetitionGuardEnabled = config.GetConfigInt("repetitionguard", 0) != 0;
            AssistantName = config.GetConfigValue("assistantname");
            if (string.IsNullOrWhiteSpace(AssistantName))
                AssistantName = AppConstants.DefaultAssistantName;
            ThinkingDisplayModeText = config.GetChatBlockDisplayMode("thinkingdisplay", ChatBlockDisplayMode.Collapsed).ToString();
            ToolCallDisplayModeText = config.GetChatBlockDisplayMode("toolcalldisplay", ChatBlockDisplayMode.Collapsed).ToString();
            ToolOutputDisplayModeText = config.GetChatBlockDisplayMode("tooloutputdisplay", ChatBlockDisplayMode.Shown).ToString();
            MarkdownParsing = config.GetConfigBool("markdownparsing", true);
            ThemeComboBox.SelectedIndex = Math.Max(0, Array.IndexOf(ColorHelper.ThemeOrder, ColorHelper.GetTheme(App.ColorsFileName)));
            CodeFontFamily = config.GetConfigValue("codeblockfontfamily");
            CustomFontFamily = config.GetConfigValue("customfontfamily");
            ChatFontSize = config.GetConfigInt("fontsize", AppConstants.DefaultChatFontSize);

            RagEnabled = config.GetConfigBool("ragEnabled", false);
            RagKnowledgePath = config.GetConfigValue("ragKnowledgePath");
            RagMaxResults = config.GetConfigInt("ragMaxResults", 5);
            IndexChunkLines = config.GetConfigInt("indexChunkLines", 60);
            IndexChunkOverlap = config.GetConfigInt("indexChunkOverlap", 10);
            RagMaxSnippetLength = config.GetConfigInt("ragMaxSnippetLength", 2000);
            RagRetrieveMode = config.GetConfigValue("ragRetrieveMode", "newchat");
            string allowedExt = config.GetConfigValue("ragAllowedExtensions");
            RagAllowedExtensions = string.IsNullOrEmpty(allowedExt)
                ? AppConstants.DefaultRagAllowedExtensions
                : allowedExt;
            EmbeddingsEndpoint = config.GetConfigValue("embeddingsEndpoint");
            EmbeddingsModel = config.GetConfigValue("embeddingsModel");
            EmbeddingsApiKey = config.GetConfigValue("embeddingsApiKey");

            ApiKeyPasswordBox.Password = ApiKey;
            EmbeddingsApiKeyPasswordBox.Password = EmbeddingsApiKey;
            RagRetrieveModeComboBox.SelectedIndex =
                string.Equals(RagRetrieveMode, "everyturn", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        private void LoadToolSelections(ConfigHandler config)
        {
            ApplyToolSelectionToListBox(ToolsListBox, config.GetConfigList("tools"));
            ApplyToolSelectionToListBox(
                ToolsRequiringApprovalListBox,
                config.GetConfigList("toolsrequiringapproval", AppConstants.DefaultToolsRequiringApproval));
        }

        private void Options_Closed(object sender, EventArgs e)
        {
            IndexingStatusHub.Updated -= OnIndexingStatusUpdated;

            // A theme previewed here was never persisted unless the dialog was
            // saved, so put the saved theme back on cancel / window close.
            if (DialogResult != true)
                ColorHelper.ApplyTheme(_savedTheme, IniFileHandler.LoadIni(App.ColorsFileName));
        }

        /// <summary>
        /// Repaints with the selected theme immediately, so the choice can be
        /// judged before saving.
        /// </summary>
        private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ColorHelper.ApplyTheme(SelectedTheme(), IniFileHandler.LoadIni(App.ColorsFileName));
        }

        private string SelectedTheme()
        {
            int idx = ThemeComboBox.SelectedIndex;
            return (idx >= 0 && idx < ColorHelper.ThemeOrder.Length)
                ? ColorHelper.ThemeOrder[idx]
                : ColorHelper.ThemeSystem;
        }

        private void OnIndexingStatusUpdated()
        {
            RefreshIndexingStatusUi();
        }

        private void RefreshIndexingStatusUi()
        {
            IndexingStatusSnapshot status = IndexingStatusHub.GetSnapshot();
            IndexingStatusBrief.Text = status.BriefText ?? "Index: (unknown)";
            IndexingStatusDetail.Text = status.DetailText ?? string.Empty;
            bool enabled = !status.IsBusy;
            BuildIndexButton.IsEnabled = enabled;
            ClearIndexButton.IsEnabled = enabled;
        }

        private void BuildIndexButton_Click(object sender, RoutedEventArgs e)
        {
            // Persist current Options so the CLI picks them up on /reload,
            // which also runs the RAG index check.
            SaveCurrentSettings();
            App.LoadSettings();

            if (_processHandler != null && _processHandler.IsProcessRunning)
                _processHandler.SendReload();
            else
                MessageBox.Show(this, "Start a chat session first so the CLI can build the index.", "RAG", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SaveCurrentSettings()
        {
            ApiKey = ApiKeyPasswordBox.Password;
            EmbeddingsApiKey = EmbeddingsApiKeyPasswordBox.Password;
            RagRetrieveMode = RagRetrieveModeComboBox.SelectedIndex == 1 ? "everyturn" : "newchat";
            SaveThemeSelection();
            SaveIni(App.ConfigFilePath);
        }

        private void SaveThemeSelection()
        {
            string theme = SelectedTheme();
            ColorHelper.SaveTheme(theme, App.ColorsFileName);
            _savedTheme = theme;
        }

        private void ClearIndexButton_Click(object sender, RoutedEventArgs e)
        {
            if (_processHandler != null && _processHandler.IsProcessRunning)
                _processHandler.SendInput("/clearindex");
            else
                MessageBox.Show(this, "Start a chat session first so the CLI can clear the index.", "RAG", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private List<string> GetSelectedToolsFromListBox(System.Windows.Controls.ListBox listBox)
        {
            return listBox.SelectedItems
                .OfType<string>()
                .Select(GetBareToolName)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }

        /// <summary>
        /// List items are shown as "Package Name/tool_name"; config/CLI use bare tool names.
        /// </summary>
        private static string GetBareToolName(string displayOrName)
        {
            if (string.IsNullOrEmpty(displayOrName))
                return displayOrName;
            int slash = displayOrName.LastIndexOf('/');
            return slash >= 0 ? displayOrName.Substring(slash + 1) : displayOrName;
        }

        private void SaveIni(string path)
        {
            var sections = new List<KeyValuePair<string, List<string>>>
            {
                new KeyValuePair<string, List<string>>("Appearance", new List<string>
                {
                    "assistantname=" + AssistantName,
                    "codeblockfontfamily=" + CodeFontFamily,
                    "customfontfamily=" + CustomFontFamily,
                    "fontsize=" + ChatFontSize,
                    "markdownparsing=" + (MarkdownParsing ? "1" : "0"),
                    "thinkingdisplay=" + (ThinkingDisplayModeText ?? "Collapsed").ToLowerInvariant(),
                    "toolcalldisplay=" + (ToolCallDisplayModeText ?? "Collapsed").ToLowerInvariant(),
                    "tooloutputdisplay=" + (ToolOutputDisplayModeText ?? "Shown").ToLowerInvariant(),
                }),
                new KeyValuePair<string, List<string>>("RAG", new List<string>
                {
                    "ragenabled=" + (RagEnabled ? "1" : "0"),
                    "ragallowedextensions=" + RagExtensionList.FormatForStorage(RagAllowedExtensions),
                    "indexchunkoverlap=" + IndexChunkOverlap,
                    "indexchunklines=" + IndexChunkLines,
                    "embeddingsapikey=" + SecretProtector.Protect(EmbeddingsApiKey),
                    "embeddingsendpoint=" + (EmbeddingsEndpoint ?? string.Empty),
                    "embeddingsmodel=" + (EmbeddingsModel ?? string.Empty),
                    "ragknowledgepath=" + (RagKnowledgePath ?? string.Empty),
                    "ragmaxsnippetlength=" + RagMaxSnippetLength,
                    "ragmaxresults=" + RagMaxResults,
                    "ragretrievemode=" + (RagRetrieveMode ?? "newchat"),
                }),
                new KeyValuePair<string, List<string>>("System", SettingsIniWriter.GetSystemSettings(ApiKey, ServerURL, ChatPath, Model, SysPrompt, ContextWindowSize, RepetitionGuardEnabled)),
                new KeyValuePair<string, List<string>>("Tools", GetToolSettings()),
            };
            AddDynamicToolSettings(sections);
            SettingsIniWriter.WriteSections(path, sections);
        }

        private List<string> GetToolSettings()
        {
            var toolLines = new List<string>
            {
                "tools=" + string.Join(",", GetSelectedToolsFromListBox(ToolsListBox)),
                "toolsrequiringapproval=" + string.Join(",", GetSelectedToolsFromListBox(ToolsRequiringApprovalListBox)),
            };

            foreach (var kvp in _toolTimeoutControls)
            {
                string val = kvp.Value.Text.Trim();
                int parsed;
                if (!string.IsNullOrEmpty(val) && int.TryParse(val, out parsed) && parsed > 0)
                    toolLines.Add("tooltimeout." + kvp.Key.ToLowerInvariant() + "=" + parsed);
            }
            return toolLines;
        }

        private void AddDynamicToolSettings(List<KeyValuePair<string, List<string>>> sections)
        {
            foreach (var group in GetToolOptionGroups())
            {
                var groupLines = new List<string>();
                foreach (var opt in group)
                {
                    string value = opt.Default;
                    FrameworkElement control;
                    if (_toolOptionControls.TryGetValue(opt.Name, out control))
                    {
                        if (opt.Type == "bool" && control is CheckBox cb)
                            value = cb.IsChecked == true ? "1" : "0";
                        else if (control is TextBox tb)
                            value = tb.Text;
                    }
                    groupLines.Add(opt.Name.ToLowerInvariant() + "=" + value);
                }
                sections.Add(new KeyValuePair<string, List<string>>(group.Key, groupLines));
            }
        }

        /// <summary>
        /// Builds per-tool-group pages: one ListBoxItem and one ScrollViewer page per group,
        /// added dynamically after the static Appearance/System/Tools entries.
        /// </summary>
        private void BuildToolOptionsUI(ConfigHandler config)
        {
            _toolOptionControls.Clear();
            _toolGroupPages.Clear();

            if (_toolOptions.Count == 0)
                return;

            foreach (var group in GetToolOptionGroups())
            {
                // Add category item to ListBox
                var listBoxItem = new ListBoxItem
                {
                    Content = group.Key
                };
                CategoryListBox.Items.Add(listBoxItem);

                // Build page
                var stackPanel = new StackPanel { Margin = new Thickness(8, 0, 8, 0) };

                var heading = new Label
                {
                    Content = group.Key,
                    FontWeight = FontWeights.Bold,
                    FontSize = 14
                };
                stackPanel.Children.Add(heading);

                var textOpts = group.Where(o => o.Type != "bool").OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase).ToList();
                var boolOpts = group.Where(o => o.Type == "bool").OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase).ToList();

                foreach (var opt in textOpts)
                {
                    string configValue = config.GetConfigValue(opt.Name);
                    string currentValue = string.IsNullOrEmpty(configValue) ? opt.Default : configValue;
                    var label = new Label { Content = opt.Label + ":" };
                    var textBox = new TextBox { Text = currentValue, Height = 23 };
                    stackPanel.Children.Add(label);
                    stackPanel.Children.Add(textBox);
                    _toolOptionControls[opt.Name] = textBox;
                }

                if (boolOpts.Count > 0)
                {
                    if (textOpts.Count > 0)
                        stackPanel.Children.Add(new Border { Height = 8 });

                    foreach (var opt in boolOpts)
                    {
                        string configValue = config.GetConfigValue(opt.Name);
                        string currentValue = string.IsNullOrEmpty(configValue) ? opt.Default : configValue;
                        var checkBox = new CheckBox
                        {
                            Content = opt.Label,
                            Margin = new Thickness(0, 4, 0, 0)
                        };
                        checkBox.IsChecked = currentValue == "1" || string.Equals(currentValue, "true", StringComparison.OrdinalIgnoreCase);
                        stackPanel.Children.Add(checkBox);
                        _toolOptionControls[opt.Name] = checkBox;
                    }
                }

                var scrollViewer = new ScrollViewer
                {
                    Margin = new Thickness(0, 0, 0, 10),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Visibility = Visibility.Collapsed,
                    Content = stackPanel
                };

                ContentGrid.Children.Add(scrollViewer);
                _toolGroupPages.Add(scrollViewer);
            }
        }

        /// <summary>
        /// Adds a per-tool timeout section to the Tools page.
        /// Each known tool gets a label and a small TextBox for its timeout in seconds.
        /// Values are stored in config as tooltimeout.&lt;toolname&gt;.
        /// </summary>
        private void BuildToolTimeoutsUI(ConfigHandler config)
        {
            _toolTimeoutControls.Clear();

            if (_availableTools.Count == 0)
                return;

            var stack = ToolsPage.Content as StackPanel;
            if (stack == null)
                return;

            stack.Children.Add(new Label
            {
                Content = "Tool Timeouts (seconds, 0 or blank = no timeout):",
                Margin = new Thickness(0, 8, 0, 0)
            });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

            foreach (string displayName in _availableTools)
            {
                string toolName = GetBareToolName(displayName);
                int rowIdx = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = displayName,
                    Padding = new Thickness(0, 2, 4, 2),
                    VerticalAlignment = VerticalAlignment.Center
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, "LabelTextColorBrush");
                Grid.SetRow(label, rowIdx);
                Grid.SetColumn(label, 0);

                string configVal = config.GetConfigValue("tooltimeout." + toolName.ToLowerInvariant());
                if (string.IsNullOrEmpty(configVal))
                    configVal = "0";
                var textBox = new TextBox
                {
                    Text = configVal,
                    Height = 23,
                    Margin = new Thickness(0, 2, 0, 2),
                    VerticalAlignment = VerticalAlignment.Center
                };
                textBox.PreviewTextInput += (s, e) => { e.Handled = !e.Text.All(char.IsDigit); };
                DataObject.AddPastingHandler(textBox, (s, e) =>
                {
                    if (e.DataObject.GetDataPresent(DataFormats.Text))
                    {
                        string text = (string)e.DataObject.GetData(DataFormats.Text);
                        if (!text.All(char.IsDigit)) e.CancelCommand();
                    }
                    else e.CancelCommand();
                });
                Grid.SetRow(textBox, rowIdx);
                Grid.SetColumn(textBox, 1);

                grid.Children.Add(label);
                grid.Children.Add(textBox);
                _toolTimeoutControls[toolName] = textBox;
            }

            stack.Children.Add(grid);
        }

        /// <summary>
        /// Groups tool option definitions by source package for the Options UI.
        /// </summary>
        private IOrderedEnumerable<IGrouping<string, ToolOptionDefinition>> GetToolOptionGroups()
        {
            return _toolOptions
                .GroupBy(opt => string.IsNullOrWhiteSpace(opt.Source) ? "Tools" : opt.Source, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        }

        private List<string> LoadAvailableToolsFromManifests()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var toolNames = new List<string>();

            foreach (string jsonFile in ManifestScanner.GetManifestFiles(_toolsDir))
            {
                try
                {
                    string json = File.ReadAllText(jsonFile, System.Text.Encoding.UTF8);
                    JObject manifest = JObject.Parse(json);
                    JArray tools = manifest["tools"] as JArray;
                    if (tools == null) continue;

                    string packageName = (string)manifest["name"];
                    if (string.IsNullOrWhiteSpace(packageName))
                        packageName = Path.GetFileNameWithoutExtension(jsonFile) ?? "Tools";

                    foreach (JObject tool in tools)
                    {
                        string name = (string)tool["name"];
                        if (string.IsNullOrEmpty(name) || !seen.Add(name))
                            continue;
                        toolNames.Add(packageName + "/" + name);
                    }
                }
                catch
                {
                    // Skip malformed manifests
                }
            }

            toolNames.Sort(StringComparer.OrdinalIgnoreCase);
            return toolNames;
        }

        private void ApplyToolSelectionToListBox(System.Windows.Controls.ListBox listBox, List<string> tools)
        {
            if (listBox == null || tools == null)
                return;

            var selectedTools = new HashSet<string>(tools, StringComparer.OrdinalIgnoreCase);
            foreach (var item in listBox.Items)
            {
                var displayName = item as string;
                if (displayName == null)
                    continue;
                if (selectedTools.Contains(displayName) || selectedTools.Contains(GetBareToolName(displayName)))
                    listBox.SelectedItems.Add(item);
            }
        }
    }
}
