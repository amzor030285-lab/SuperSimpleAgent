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
        /// (Resources\NotoEmoji-Regular.ttf, SIL OFL 1.1), used only on systems
        /// that have no emoji font of their own (Windows XP). On Windows 7+ the
        /// OS ships Segoe UI Emoji, so this returns null and the system renders
        /// emoji normally; forcing the bundled font there would turn working
        /// color emoji into tofu. Loaded lazily and cached.
        /// </summary>
        public static FontFamily EmojiFontFamily
        {
            get
            {
                if (_emojiFontDecided)
                    return _emojiFontFamily;
                _emojiFontDecided = true;

                try
                {
                    if (HasSystemEmojiFont())
                        return null;

                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "NotoEmoji-Regular.ttf");
                    if (File.Exists(path))
                    {
                        string fileUri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
                        FontFamily family = new FontFamily(fileUri + "#Noto Emoji");
                        if (family.GetTypefaces().Any())
                            _emojiFontFamily = family;
                    }
                }
                catch
                {
                    // Missing/corrupt font: fall back to the default font.
                }

                return _emojiFontFamily;
            }
        }

        // True when the OS already provides an emoji font (Segoe UI Emoji,
        // Windows 7+). When true the bundled font must not be forced.
        private static bool HasSystemEmojiFont()
        {
            try
            {
                return new FontFamily("Segoe UI Emoji").GetTypefaces().Any();
            }
            catch
            {
                return false;
            }
        }

        private static FontFamily _emojiFontFamily;
        private static bool _emojiFontDecided;

        public static void ApplyFontSizeToControl(Control control, int fontSize)
        {
            if (control != null)
            {
                control.FontSize = fontSize;
            }
        }
    }
}
