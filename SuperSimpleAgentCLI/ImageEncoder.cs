using System;
using System.Collections.Generic;
using System.IO;

namespace SuperSimpleAgentCLI
{
    internal static class ImageEncoder
    {
        public const string DefaultMime = "image/png";

        private static readonly Dictionary<string, string> MimeByExtension =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { ".jpg", "image/jpeg" },
                { ".jpeg", "image/jpeg" },
                { ".gif", "image/gif" },
                { ".webp", "image/webp" },
                { ".bmp", "image/bmp" },
                { ".tif", "image/tiff" },
                { ".tiff", "image/tiff" },
                { ".png", "image/png" },
            };

        public static string ImageFileToBase64(string path, out string mime)
        {
            mime = GuessMime(path);
            return Convert.ToBase64String(File.ReadAllBytes(path));
        }

        public static string GuessMime(string path)
        {
            if (string.IsNullOrEmpty(path))
                return DefaultMime;

            string mime;
            return MimeByExtension.TryGetValue(Path.GetExtension(path), out mime)
                ? mime
                : DefaultMime;
        }
    }
}
