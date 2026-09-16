using System;

/// <summary>
/// Shared token approximation and context-usage helpers (chars / 3).
/// </summary>
public static class TokenEstimator
{
    /// <summary>At this usage the conversation is silently summarized into a compact recap.</summary>
    public const double SummarizeThreshold = 0.90;

    /// <summary>At this usage the conversation is micro-compacted (old tool groups dropped).</summary>
    public const double MicroCompactThreshold = 0.80;

    /// <summary>
    /// Critical usage: automatic compaction kicks in (repeated oldest-message
    /// truncation, then, as a last resort, shrinking the biggest message contents).
    /// </summary>
    public const double AskUserThreshold = 0.95;

    /// <summary>Fraction of the oldest messages to drop when truncating (30%).</summary>
    public const double TruncateFraction = 0.30;

    public static int ApproximateTokens(int characterCount)
    {
        if (characterCount <= 0)
            return 0;
        return characterCount / 3;
    }

    public static bool ShouldSummarize(int characterCount, int contextWindowSize)
    {
        if (contextWindowSize <= 0)
            return false;

        return Usage(characterCount, contextWindowSize) >= SummarizeThreshold;
    }

    /// <summary>Token usage as a fraction (0..1+) of the context window.</summary>
    public static double Usage(int characterCount, int contextWindowSize)
    {
        if (contextWindowSize <= 0)
            return 0.0;

        int approximateTokens = ApproximateTokens(characterCount);
        return (double)approximateTokens / contextWindowSize;
    }

    /// <summary>
    /// NyoCoder-style status text: "Tokens: ~N / max (p.p%)" or "Tokens: ~N".
    /// </summary>
    public static string FormatStatus(int tokens, int? contextWindowSize)
    {
        if (contextWindowSize.HasValue && contextWindowSize.Value > 0)
        {
            double percentage = (double)tokens / contextWindowSize.Value * 100.0;
            return string.Format("Tokens: ~{0:N0} / {1:N0} ({2:F1}%)",
                tokens, contextWindowSize.Value, percentage);
        }

        return string.Format("Tokens: ~{0:N0}", tokens);
    }
}