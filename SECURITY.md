# Security Policy

## Supported versions

| Version | Supported |
| ------- | --------- |
| 1.0.x   | Yes       |

Only the latest tagged release receives fixes. This project targets Windows XP SP3 and later;
XP has no supported security updates from Microsoft, so treat an XP host accordingly.

## Reporting a vulnerability

Please report suspected vulnerabilities privately rather than in a public issue:

- Open a [GitHub Security Advisory](https://github.com/<owner>/SimpleLLMChat/security/advisories/new) for this repository, or
- open an issue if the advisory flow is not available to you.

Include: what happens, how to reproduce it, the affected version, and your OS version. Please do
not include real API keys or private conversation content in the report - a placeholder value is
enough to reproduce.

## What this application does by design

SimpleLLMChat is an agent: the model can call tools, and some tools deliberately affect the
machine. The following are **not** vulnerabilities, they are the documented behavior:

- `run_shell_command` executes shell commands, and `download_file` writes files, when the tool is
  enabled and the call is approved. Approval mode `auto` approves every call without asking.
  Narrow the `tools` list in `LLMSettings.ini` if you do not want that surface at all.
- The model receives tool output and can act on it. Treat a tool-enabled session as you would treat
  running an assistant-generated script yourself.
- `LLMSettings.ini` holds your API key encrypted with Windows DPAPI under your Windows user
  account. Anyone running the application as that account can read the key - DPAPI is not a
  defense against other processes of the same user. Do not share the file.
- A saved session (`sessions/`) contains your conversation text verbatim, including tool output.
  It is plaintext on disk.
- The RAG knowledge folder is read and its contents are sent to whatever LLM endpoint is
  configured. Point it only at an endpoint you trust.
- A prompt-injected web page or file can make the model attempt tool calls. That is why
  `run_shell_command` and `download_file` require approval by default; keep them that way unless
  you fully trust the content being processed.
