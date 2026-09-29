using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Forms;
using System.Drawing;

namespace SimpleLLMChatGUI
{
    public partial class ColorsForm : Window
    {
        /// <summary>
        /// One configurable color. Previews show the color the app actually
        /// renders (custom value if set, otherwise the active theme's color),
        /// so the dialog cannot disagree with the window behind it.
        /// </summary>
        public sealed class ColorSetting : INotifyPropertyChanged
        {
            private readonly ColorConfig _config;
            private string _theme;
            private System.Windows.Media.Color? _value;

            public ColorSetting(ColorConfig config, string theme)
            {
                _config = config;
                _theme = theme;
            }

            public ColorConfig Config { get { return _config; } }
            public string Key { get { return _config.Key; } }
            public string DisplayName { get { return _config.DisplayName; } }
            public string ChooseButtonId { get { return "chooseButton_" + _config.Key; } }
            public string ClearButtonId { get { return "clearButton_" + _config.Key; } }
            public string PreviewId { get { return "preview_" + _config.Key; } }

            /// <summary>Theme the preview falls back to when no custom value is set.</summary>
            public string Theme
            {
                get { return _theme; }
                set
                {
                    if (string.Equals(_theme, value, StringComparison.OrdinalIgnoreCase))
                        return;

                    _theme = value;
                    OnPropertyChanged("Theme");
                    OnPropertyChanged("PreviewBrush");
                    OnPropertyChanged("PreviewTip");
                }
            }

            /// <summary>Custom color, or null to follow the theme.</summary>
            public System.Windows.Media.Color? Value
            {
                get { return _value; }
                set
                {
                    _value = value;
                    OnPropertyChanged("Value");
                    OnPropertyChanged("PreviewBrush");
                    OnPropertyChanged("PreviewTip");
                }
            }

            /// <summary>The color the window behind the dialog actually renders.</summary>
            public System.Windows.Media.Color EffectiveColor
            {
                get { return _value ?? ThemeColor; }
            }

            public System.Windows.Media.Brush PreviewBrush
            {
                get { return new SolidColorBrush(EffectiveColor); }
            }

            public string PreviewTip
            {
                get
                {
                    if (_value.HasValue)
                        return "Custom " + Hex(_value.Value) + " - overrides the theme";

                    string source = _theme == ColorHelper.ThemeSystem
                        ? "System default"
                        : ColorHelper.GetThemeDisplayName(_theme) + " theme";
                    return source + " " + Hex(ThemeColor);
                }
            }

            private System.Windows.Media.Color ThemeColor
            {
                get { return ColorHelper.ResolveColor(_config.ResourceKey, _theme, null); }
            }

            private static string Hex(System.Windows.Media.Color color)
            {
                return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", color.A, color.R, color.G, color.B);
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private void OnPropertyChanged(string name)
            {
                PropertyChangedEventHandler handler = PropertyChanged;
                if (handler != null)
                    handler(this, new PropertyChangedEventArgs(name));
            }
        }

        private readonly List<ColorSetting> _colorSettings;
        private string _theme = ColorHelper.ThemeSystem;

        public ColorsForm()
        {
            InitializeComponent();

            // One row per configured color, in a stable order.
            var keys = new List<string>(ColorHelper.ColorConfigs.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);

            _colorSettings = new List<ColorSetting>(keys.Count);
            foreach (string key in keys)
                _colorSettings.Add(new ColorSetting(ColorHelper.ColorConfigs[key], _theme));

            colorList.ItemsSource = _colorSettings;
        }

        private void SetColor(string key, System.Windows.Media.Color? value)
        {
            ColorSetting setting = Find(key);
            if (setting != null)
                setting.Value = value;
        }

        private ColorSetting Find(string key)
        {
            foreach (var setting in _colorSettings)
            {
                if (string.Equals(setting.Key, key, StringComparison.OrdinalIgnoreCase))
                    return setting;
            }
            return null;
        }

        private void ShowColorDialog(System.Windows.Media.Color? currentColor, System.Windows.Media.Color defaultColor, Action<System.Windows.Media.Color> onColorSelected)
        {
            using (var colorDialog = new ColorDialog())
            {
                // Start from the pending value, or from what the theme shows.
                System.Windows.Media.Color dialogColor = currentColor ?? defaultColor;
                colorDialog.Color = System.Drawing.Color.FromArgb(
                    dialogColor.A,
                    dialogColor.R,
                    dialogColor.G,
                    dialogColor.B);

                if (colorDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    var selectedColor = colorDialog.Color;
                    onColorSelected(System.Windows.Media.Color.FromArgb(
                        selectedColor.A,
                        selectedColor.R,
                        selectedColor.G,
                        selectedColor.B));
                }
            }
        }

        // Generic event handlers - use Tag property to identify which color
        private void ChooseColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is string key)
            {
                ColorSetting setting = Find(key);
                if (setting != null)
                    ShowColorDialog(setting.Value, setting.Config.DefaultSystemColor, color => SetColor(key, color));
            }
        }

        private void ClearColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is string key)
            {
                SetColor(key, null);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveColors(App.ColorsFileName);

                // Re-derive every brush from the saved file: the palette applies
                // again for colors that were cleared instead of being pinned to
                // system colors.
                ColorHelper.ReloadFrom(App.ColorsFileName);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this, "Error saving colors: " + ex.Message, "Warning",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Apply custom font to this window
            FontHandler.ApplyFontToWindow(this);

            LoadColors(App.ColorsFileName);
        }

        private void LoadColors(string path)
        {
            if (!File.Exists(path))
                return;

            try
            {
                var settings = IniFileHandler.LoadIni(path);

                string themeValue;
                if (settings.TryGetValue(ColorHelper.ThemeKey, out themeValue) && !string.IsNullOrWhiteSpace(themeValue))
                {
                    _theme = themeValue.Trim().ToLowerInvariant();
                }

                foreach (var setting in _colorSettings)
                    setting.Theme = _theme;

                foreach (var setting in _colorSettings)
                {
                    string colorValue;
                    if (settings.TryGetValue(setting.Key, out colorValue) && !string.IsNullOrWhiteSpace(colorValue))
                    {
                        System.Windows.Media.Color color;
                        if (ColorHelper.TryParseColor(colorValue, out color))
                            setting.Value = color;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    "Error loading colors file: " + ex.Message,
                    "Warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Writes the theme and every configured color, keeping any other keys
        /// the file happens to contain.
        /// </summary>
        private void SaveColors(string path)
        {
            var settings = IniFileHandler.LoadIni(path);
            settings[ColorHelper.ThemeKey] = _theme;

            foreach (var setting in _colorSettings)
            {
                if (setting.Value.HasValue)
                {
                    System.Windows.Media.Color color = setting.Value.Value;
                    settings[setting.Key] = string.Format(
                        "#{0:X2}{1:X2}{2:X2}{3:X2}", color.A, color.R, color.G, color.B);
                }
                else
                {
                    // Blank means "follow the theme".
                    settings[setting.Key] = string.Empty;
                }
            }

            var lines = new List<string>(settings.Count);
            foreach (var kvp in settings)
                lines.Add(kvp.Key + "=" + kvp.Value);

            File.WriteAllLines(path, lines);
        }
    }
}
