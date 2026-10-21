using System;
using System.Text.RegularExpressions;

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
        if (explicitPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || explicitPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return explicitPath;

        // Path form: trailing slash and surrounding whitespace go away, a missing
        // leading slash is added; empty stays empty so the caller can fall back to
        // auto-detection instead of appending nothing.
        string trimmedPath = TrimTrailingSlash(explicitPath);
        explicitPath = trimmedPath.Length == 0 ? string.Empty
            : trimmedPath[0] == '/' ? trimmedPath : "/" + trimmedPath;
        if (explicitPath.Length == 0)
        {
            string baseTrimmed = TrimTrailingSlash(baseUrl);
            if (baseTrimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
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
    /// True when the last path segment is a version number: "v" followed by digits.
    /// "v1beta" or a host like "v1.example.com" do not count, so those bases keep the
    /// historical "/v1" behaviour.
    /// </summary>
    private static bool HasVersionSegment(string baseUrl)
    {
        return Regex.IsMatch(TrimTrailingSlash(baseUrl), @"/v[0-9]+$", RegexOptions.IgnoreCase);
    }

    /// <summary>Trims whitespace and every trailing slash; null becomes empty.</summary>
    private static string TrimTrailingSlash(string url)
    {
        return (url ?? string.Empty).Trim().TrimEnd('/');
    }
}
