using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

/// <summary>
/// Masks credentials in text that is about to leave the process: tool output that
/// enters the conversation, the session file and the API request.
/// <para>
/// The pattern set is adapted from gitleaks (MIT licence,
/// https://github.com/gitleaks/gitleaks - config/gitleaks.toml). It is a subset of
/// that config, widened for masking: a detection may afford false positives, a mask
/// may not miss. Every pattern is linear-time on purpose - .NET 4.0 has no Regex
/// match timeout, so a backtracking pattern would hang the CLI on a large tool
/// output.
/// </para>
/// </summary>
public static class SecretRedactor
{
    /// <summary>Opening of the marker written in place of a masked credential.</summary>
    public const string PlaceholderPrefix = "[REDACTED:";

    /// <summary>Shortest literal value that is masked. Real API keys are longer;
    /// a short value is more likely to be ordinary text than a credential.</summary>
    private const int MinLiteralLength = 12;

    private sealed class Rule
    {
        public readonly Regex Pattern;
        public readonly int Group;
        public readonly string Label;

        public Rule(string pattern, RegexOptions options, int group, string label)
        {
            Pattern = new Regex(pattern, options);
            Group = group;
            Label = label;
        }
    }

    private static readonly List<Rule> Rules = BuildRules();

    private static List<Rule> BuildRules()
    {
        RegexOptions ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        RegexOptions cs = RegexOptions.CultureInvariant;
        return new List<Rule>
        {
            // OpenAI / Anthropic and most LLM providers: sk-..., sk-proj-..., sk-ant-...
            new Rule(@"\bsk-[A-Za-z0-9_\-]{16,}", cs, 0, "llm_key"),
            // AWS access key id (gitleaks: aws-access-token)
            new Rule(@"\b(?:A3T[A-Z0-9]|AKIA|ASIA|ABIA|ACCA)[A-Z2-7]{16}\b", cs, 0, "aws_key"),
            // GitHub tokens (gitleaks: github-pat / github-oauth / github-app / github-refresh)
            new Rule(@"\bgh[pousr]_[0-9A-Za-z]{36}\b", cs, 0, "github_token"),
            new Rule(@"\bgithub_pat_[0-9A-Za-z_]{20,}", cs, 0, "github_token"),
            // GitLab personal access token
            new Rule(@"\bglpat-[0-9A-Za-z_\-]{16,}", cs, 0, "gitlab_token"),
            // Slack (gitleaks: slack-bot-token and friends)
            new Rule(@"\bxox[baprs]-[0-9A-Za-z\-]{10,}", cs, 0, "slack_token"),
            // Google API key (gitleaks: gcp-api-key)
            new Rule(@"\bAIza[0-9A-Za-z_\-]{35}\b", cs, 0, "google_key"),
            // Stripe
            new Rule(@"\b(?:sk|rk)_(?:live|test)_[0-9A-Za-z]{10,}", cs, 0, "stripe_key"),
            // Hugging Face, npm
            new Rule(@"\bhf_[0-9A-Za-z]{30,}", cs, 0, "hf_token"),
            new Rule(@"\bnpm_[0-9A-Za-z]{30,}", cs, 0, "npm_token"),
            // JSON web token
            new Rule(@"\beyJ[0-9A-Za-z_\-]{8,}\.[0-9A-Za-z_\-]{8,}\.[0-9A-Za-z_\-]{8,}", cs, 0, "jwt"),
            // Authorization: Bearer <token>
            new Rule(@"\bBearer\s+[0-9A-Za-z_\-\.=+/]{16,}", ci, 0, "bearer_token"),
            // PEM private key: header plus its base64 body (gitleaks: private-key).
            // The body is matched with a bounded repetition so the pattern stays linear.
            new Rule(@"-----BEGIN[ A-Z0-9]{0,40}PRIVATE KEY(?: BLOCK)?-----[\r\n]+[0-9A-Za-z+/=\r\n]{32,20000}", cs, 0, "private_key"),
            // key = value / "key": "value" for credential-looking names. Only the value
            // is masked, the name stays so the model still knows what was there.
            new Rule(@"\b(api[_\-]?key|apikey|secret[_\-]?key|secret|password|passwd|client[_\-]?secret|access[_\-]?token|auth[_\-]?token|private[_\-]?key|credentials?)([ \t]*[:=][ \t]*[""']?)([0-9A-Za-z_\-./+=]{8,200})", ci, 3, "secret_value"),
            // Credentials inside a URL: scheme://user:password@host
            new Rule(@"\b([a-z][0-9a-z+.\-]{1,20}://[^\s:/@]{1,64}:)([^\s@/]{3,64})(@)", ci, 2, "url_password"),
        };
    }

    /// <summary>
    /// Masks every recognised credential in the text.
    /// </summary>
    public static string Redact(string text)
    {
        return Redact(text, null);
    }

    /// <summary>
    /// Masks recognised credentials and the given literal values. The literals are
    /// the keys configured in LLMSettings.ini: no pattern can recognise them, but
    /// they are exactly the values that must not reach a third-party API.
    /// </summary>
    public static string Redact(string text, IEnumerable<string> literalSecrets)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        string result = text;
        for (int i = 0; i < Rules.Count; i++)
        {
            Rule rule = Rules[i];
            string placeholder = PlaceholderPrefix + rule.Label + "]";
            result = rule.Pattern.Replace(result, delegate(Match m)
            {
                Group group = m.Groups[rule.Group];
                if (!group.Success || group.Length == 0)
                    return m.Value;
                int start = group.Index - m.Index;
                return m.Value.Substring(0, start) + placeholder + m.Value.Substring(start + group.Length);
            });
        }

        if (literalSecrets != null)
        {
            foreach (string secret in literalSecrets)
            {
                if (secret == null || secret.Trim().Length < MinLiteralLength)
                    continue;
                result = result.Replace(secret, PlaceholderPrefix + "configured_key]");
            }
        }

        return result;
    }
}
