using System;
using System.Collections.Generic;

namespace SuperSimpleAgentCLI.RAG
{
    public sealed class AutoRagResult
    {
        public string MergedPrompt;
        public string UserStatusLine;
    }

    /// <summary>
    /// Automatic semantic retrieval for new chats / every turn.
    /// </summary>
    public static class AutoRagContext
    {
        public static AutoRagResult TryRetrieve(ConfigHandler config, string query, string baseUserPrompt)
        {
            AutoRagResult result = new AutoRagResult
            {
                MergedPrompt = baseUserPrompt
            };

            if (!config.GetConfigBool("ragEnabled", false))
                return result;
            if (string.IsNullOrWhiteSpace(query))
                return result;

            DocumentIndex index = DocumentIndex.GetCurrent(config);
            if (!index.HasIndex)
                return result;

            IndexedHitSet hits = SemanticHitFormatter.Search(
                index, config, query, config.GetConfigInt("ragMaxResults", 5));
            if (hits == null || string.IsNullOrWhiteSpace(hits.FormattedText))
                return result;

            string block = "---Retrieved Context---\n"
                + hits.FormattedText
                + "\n---End Retrieved Context---";

            result.MergedPrompt = MergeIntoPrompt(baseUserPrompt, block);
            result.UserStatusLine = hits.FilePaths.Count == 0
                ? null
                : "[reading " + string.Join(", ", hits.FilePaths) + "]";
            return result;
        }

        private static string MergeIntoPrompt(string basePrompt, string retrievedBlock)
        {
            const string separator = "\n\n---\n\n";
            string baseText = basePrompt ?? string.Empty;
            int idx = baseText.IndexOf(separator, StringComparison.Ordinal);
            if (idx >= 0)
            {
                string before = baseText.Substring(0, idx);
                string after = baseText.Substring(idx); // includes ---
                return before + "\n\n" + retrievedBlock + after;
            }
            return retrievedBlock + separator + baseText;
        }

    }
}
