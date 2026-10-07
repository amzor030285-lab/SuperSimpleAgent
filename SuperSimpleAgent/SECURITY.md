# Security Policy

## Supported versions

| Version | Supported |
| ------- | --------- |
| 1.0.x   | Yes       |

Only the latest tagged release receives fixes. This project targets Windows XP SP3 and later;
XP has no supported security updates from Microsoft, so treat an XP host accordingly.

## Bundled third-party binaries

The release ships external programs that the tools start as child processes. None of them is
built or patched here, and each keeps its own license - see the README section
*Third-party components* and the license texts in `THIRD_PARTY_LICENSES`:

| Binary | Where it comes from |
| ------ | ------------------- |
| `curl.exe`, `curl-ca-bundle.crt` | cURL builds for XP by [LoRd\_MuldeR](https://github.com/lordmulder/cURL-build-win32), taken from a maintained XP deployment of this project |
| `7za.exe` | 7-Zip, from the same XP deployment |
| `yt-dlp.exe` | YT-DLP builds for XP by [nicolaasjan](https://github.com/nicolaasjan), from the same XP deployment |
| `grep.exe`, `libiconv2.dll`, `libintl3.dll`, `pcre3.dll`, `regex2.dll` | GNU grep 2.5.3 (GnuWin32 port), taken from the upstream author's v1.5 XP release archive |
| `Markdig.dll` (0.18.3) | checked into this repository at `SuperSimpleAgentGUI\lib\Markdig.dll` |
| `Newtonsoft.Json.dll` (13.0.4) | restored by the build through NuGet, version pinned in `packages.config` |

`deps\` keeps copies of the optional binaries so that builds and test runs are reproducible;
`build.bat` copies whatever is present next to the tool that needs it. None of them is required
to start the application - a missing one only limits the tool that calls it.

The XP-compatible builds are frozen at the last version that runs on XP (GNU grep 2.5.3 dates
from 2008) and nothing here rebuilds or updates them, so they do not pick up upstream security
fixes. On modern Windows prefer a maintained build of the same tool; if you rely on a bundled
one, follow that upstream project's advisories.

## Reporting a vulnerability

Please report suspected vulnerabilities privately rather than in a public issue:

- Open a [GitHub Security Advisory](https://github.com/<owner>/SuperSimpleAgent/security/advisories/new) for this repository, or
- open an issue if the advisory flow is not available to you.

Include: what happens, how to reproduce it, the affected version, and your OS version. Please do
not include real API keys or private conversation content in the report - a placeholder value is
enough to reproduce.

## What this application does by design

SuperSimpleAgent is an agent: the model can call tools, and some tools deliberately affect the
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
