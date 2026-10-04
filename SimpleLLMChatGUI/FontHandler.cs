using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SimpleLLMChatGUI
{
    public static class FontHandler
    {
        public const string CodeBlockFontFamilyKey = "CodeBlockFontFamily";

        public static void ApplyFontToWindow(Window window)
        {
            if (window == null) return;
            window.FontFamily = TryGetFontFamily(App.Config.GetConfigValue("customfontfamily"))
                                ?? SystemFonts.MessageFontFamily;
        }

        /// <summary>
        /// Pushes <c>codeblockfontfamily</c> into the app resource so code blocks
        /// bound with <see cref="FrameworkElement.SetResourceReference"/> update live.
        /// </summary>
        public static void ApplyCodeBlockFontFamily()
        {
            if (Application.Current == null)
                return;

            Application.Current.Resources[CodeBlockFontFamilyKey] =
                TryGetFontFamily(App.Config.GetConfigValue("codeblockfontfamily"))
                ?? new FontFamily("Consolas");
        }

        public static int GetFontSize()
        {
            return App.Config.GetConfigInt("fontsize", AppConstants.DefaultChatFontSize);
        }

        internal static FontFamily TryGetFontFamily(string fontFamilyName)
        {
            if (string.IsNullOrWhiteSpace(fontFamilyName) ||
                fontFamilyName.Equals("Default", StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                var fontFamily = new FontFamily(fontFamilyName);
                if (fontFamily.GetTypefaces().FirstOrDefault() != null)
                    return fontFamily;
            }
            catch { }

            return null;
        }

        /// <summary>
        /// The monochrome emoji font bundled next to the executable
        /// (Resources\NotoEmoji-Regular.ttf, SIL OFL 1.1). Referenced from a
        /// file URI so it is never installed system-wide. Loaded lazily and
        /// cached; returns null when the file is missing so callers fall back
        /// to the default chat font.
        /// </summary>
        public static FontFamily EmojiFontFamily
        {
            get
            {
                if (_emojiFontFamily != null)
                    return _emojiFontFamily;

                try
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "NotoEmoji-Regular.ttf");
                    if (!File.Exists(path))
                        return null;

                    string fileUri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
                    FontFamily family = new FontFamily(fileUri + "#Noto Emoji");
                    if (family.GetTypefaces().Any())
                        _emojiFontFamily = family;
                }
                catch
                {
                    // Missing/corrupt font: fall back to the default font.
                }

                return _emojiFontFamily;
            }
        }

        private static FontFamily _emojiFontFamily;

        public static void ApplyFontSizeToControl(Control control, int fontSize)
        {
            if (control != null)
            {
                control.FontSize = fontSize;
            }
        }
    }
}
