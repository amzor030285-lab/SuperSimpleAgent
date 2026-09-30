using System;

/// <summary>
/// Builds the request URLs for an OpenAI-compatible server out of the base URL the
/// user configured, so providers that do not put "v1" in the base still work.
/// </summary>
/// <remarks>
/// There is no way to discover a provider's entry point: it is just a path in the
/// address. Every OpenAI SDK therefore takes the base URL verbatim and appends only
/// the leaf, and the base already carries the version — "/v1" for OpenAI, xAI, Groq,
/// OpenRouter, Ollama and LM Studio, "/compatible-mode/v1" for DashScope, but
/// "/api/paas/v4" for Zhipu/bigmodel. Appending a hardcoded "/v1" only works for the
/// first group.
/// <para>Two rules, in order:</para>
/// <list type="number">
/// <item>An explicit path (chatpath in LLMSettings.ini) is used as written. A full
/// URL there is used as the complete endpoint, which is the same escape hatch
/// LibreChat calls directEndpoint.</item>
/// <item>Otherwise a base ending in a version segment gets the bare leaf, and a base
/// with no version keeps the historical "/v1/..." so existing configs do not move.</item>
/// </list>
/// </remarks>
public static class EndpointUrl
{
    /// <summary>Name of the LLMSettings.ini key holding the explicit chat path.</summary>
    public const string ChatPathKey = "chatpath";

    /// <summary>
    /// Chat completions endpoint for <paramref name="baseUrl"/>. A non-empty
    /// <paramref name="chatPath"/> overrides everything; a base that already is a
    /// complete "/chat/completions" endpoint is passed through untouched.
    /// </summary>
    public static string ChatCompletions(string baseUrl, string chatPath)
    {
        // The scheme test comes first: normalizing would prepend a slash to a full URL.
        string explicitPath = (chatPath ?? string.Empty).Trim();
        if (IsFullUrl(explicitPath))
            return explicitPath;

        explicitPath = NormalizePath(explicitPath);
        if (explicitPath.Length == 0)
        {
            string baseTrimmed = TrimTrailingSlash(baseUrl);
            if (EndsWithPath(baseTrimmed, "/chat/completions"))
                return baseTrimmed;
            return baseTrimmed + Leaf(baseUrl, "/chat/completions");
        }

        return TrimTrailingSlash(baseUrl) + explicitPath;
    }

    /// <summary>Model-list endpoint for <paramref name="baseUrl"/>.</summary>
    public static string Models(string baseUrl)
    {
        return TrimTrailingSlash(baseUrl) + Leaf(baseUrl, "/models");
    }

    /// <summary>Embeddings endpoint for <paramref name="baseUrl"/>.</summary>
    public static string Embeddings(string baseUrl)
    {
        return TrimTrailingSlash(baseUrl) + Leaf(baseUrl, "/embeddings");
    }

    /// <summary>
    /// Returns <paramref name="leaf"/> alone when the base already ends in a version
    /// segment ("/v1", "/v4"), otherwise the legacy "/v1" + leaf.
    /// </summary>
    private static string Leaf(string baseUrl, string leaf)
    {
        return HasVersionSegment(baseUrl) ? leaf : "/v1" + leaf;
    }

    /// <summary>
    /// True when the last path segment is a version number: "v" followed by at least
    /// one digit. "v1beta" and a host that merely starts with v ("v1.example.com") do
    /// not count, so those bases keep the historical "/v1" behaviour.
    /// </summary>
    private static bool HasVersionSegment(string baseUrl)
    {
        string trimmed = TrimTrailingSlash(baseUrl);
        int slash = trimmed.LastIndexOf('/');
        if (slash < 0)
            return false;

        string segment = trimmed.Substring(slash + 1);
        if (segment.Length < 2 || (segment[0] != 'v' && segment[0] != 'V'))
            return false;

        for (int i = 1; i < segment.Length; i++)
        {
            if (segment[i] < '0' || segment[i] > '9')
                return false;
        }
        return true;
    }

    private static bool EndsWithPath(string url, string suffix)
    {
        return url.Length >= suffix.Length
            && url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFullUrl(string value)
    {
        return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Trims whitespace and every trailing slash; null becomes empty.</summary>
    private static string TrimTrailingSlash(string url)
    {
        string trimmed = (url ?? string.Empty).Trim();
        while (trimmed.Length > 0 && trimmed[trimmed.Length - 1] == '/')
            trimmed = trimmed.Substring(0, trimmed.Length - 1);
        return trimmed;
    }

    /// <summary>
    /// Turns a configured path into something appendable: surrounding whitespace and a
    /// trailing slash go away, a missing leading slash is added. Empty stays empty so
    /// the caller can fall back to auto-detection.
    /// </summary>
    private static string NormalizePath(string path)
    {
        string trimmed = TrimTrailingSlash(path);
        if (trimmed.Length == 0)
            return string.Empty;
        return trimmed[0] == '/' ? trimmed : "/" + trimmed;
    }
}
