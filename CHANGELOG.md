# Changelog

All notable changes to this project are documented in this file. This repository is a
continuation of the original [SimpleLLMChat](https://github.com/randomNinja64/SimpleLLMChat);
everything listed below was added on top of that upstream baseline.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.0.1] - 2026-09-27

Bug-fix release. Feature set is identical to 1.0.0; verified on Windows 10/11 (x64) and
Windows XP SP3 (x86): FileTools suite 18 pass, CLI suite 46 pass, GUI suite 16 pass.

### Fixed

- **The provider's prompt cache is no longer rebuilt on every turn.** Context injector output
  (with DesktopTools enabled that is the live window/control tree from `get_desktop_context`)
  was appended to the system prompt, which is the first message of the request, so its
  per-turn changes invalidated the cached prompt prefix and made every turn re-process the
  whole conversation. Injections are now appended to the last user turn: the system prompt
  stays byte-identical between turns while the model still receives fresh injected context.
  A user turn that carries an image (multimodal content) and a history without any user
  message are both handled.

## [1.0.0] - 2026-09-26

First tagged release of this repository. Verified on Windows 10/11 (x64) and Windows XP SP3 (x86):
FileTools suite 18 pass, CLI suite 42 pass, GUI suite 16 pass.

### Added

- **Automatic context compaction** - when `contextWindowSize` is configured, long chats are
  managed before the window fills: micro-compaction drops old tool messages, the old part of the
  conversation is summarized into a recap (the newest ~8k tokens are kept verbatim), and bounded
  truncation is only a fallback. Tool output inside a turn is clipped so one spike cannot
  dominate the payload.
- **`/compact` and `/compactfull`** - on-demand micro-compaction and full compaction
  (micro + summarization), plus a compaction button in the GUI header. A previous summary is
  carried into the next one so repeated compaction degrades gracefully.
- **Session management** - `/save`, `/load`, `/sessions`, `/rename`, `/delete`; the current chat
  is mirrored into `lastsession` after every turn (capped at 1 MB, newest messages kept) and
  restored automatically on the next launch, in both the CLI and the GUI.
- **Tool approval modes** - `ask`, `auto` and `config` (default), via the `--approval` flag, the
  `/approval` command and the INI key `approvalmode`, with a cycle button in the GUI.
  `run_shell_command` and `download_file` require approval out of the box.
- **Encrypted settings** - `apikey`, `embeddingsapikey` and `firecrawlapikey` are stored on disk
  protected with Windows DPAPI instead of plain text. Plaintext configs keep working.
- **Tool process-tree teardown** - every tool runs inside a Windows job object, so cancel or
  timeout kills the whole tree (`cmd.exe` + its children) instead of only the direct child. The
  job limits layout is probed at runtime, so the same binary works on XP x86 and x64.
- **Token estimator self-calibration** - the tokens-per-character ratio is recalibrated from the
  server-reported `usage.prompt_tokens` after each request, so compaction triggers at the right
  point on models whose ratio differs from the characters/3 default.
- **Markdown rendering in the GUI** - Markdig 0.18.3 replaces the regex renderer: nested lists,
  nested blockquotes, code blocks with a language header, native selectable/copyable tables,
  URL links, and collapsible thinking / tool-call / tool-output blocks.
- **Theme switching** - light/dark plus eight palettes, custom fonts and font sizes.
- **Better tool output UX** - `[running tool]` labels, a Stop tool button, per-tool timeouts,
  20k output caps with offload-to-file, and a visual copy icon on turn headers and code blocks.
- **Recovery paths** - a vision-less model that rejects images triggers a one-time text-only
  retry; a summarization failure falls back to truncation with the server's real error surfaced;
  restrictive local servers that require a user role are always sent one.
- **Release packaging** - `build-and-test.bat` chains build, test build and all suites;
  `THIRD_PARTY_LICENSES/` ships with the release; a fresh CA bundle is packaged beside the
  bundled XP cURL.
- **`grep_search`** - recursive regular-expression search over files, taken from upstream v1.5:
  `pattern`, `directory_path`, `file_pattern` (passed through as `--include`) and
  `case_insensitive`; it runs the bundled GNU grep and skips VCS, build and dependency
  directories as well as binaries, so a search does not drown in build output. The release ships
  `grep.exe` with the four DLLs it needs.
- **CLI readiness signal** - the CLI publishes `STATUS ready` on the status pipe when it prints
  its prompt, so the GUI can tell an idle CLI from a busy one without waiting for the whole turn
  to end.
- **Default data folders** - the staged release now contains `knowledge\`,
  `tools\SkillTools\skills\` and `tools\MemoryTools\memories\`, so a fresh unpack works
  without creating them by hand.

### Fixed

- Image attachments were uploaded as `image/png` whatever the file was; JPEG, GIF, WebP, BMP and
  TIFF now go out with their own MIME type instead of being mislabelled.
- A malformed tool manifest and a failing context provider are reported on stderr instead of
  being skipped in silence, which made a broken tool look like a missing one.

- Context loss without compaction: the ladder used to chew the oldest 30% of messages in a loop
  before attempting a summary, so a single large tool result could wipe a long session. Summarizing
  now happens first, truncation is a single bounded pass, and truncating a single message is the
  last resort.
- Undeclared configuration (API keys, server URL) was written into every tool's stdin payload;
  tools now only receive the options their own manifest declares.
- `read_file` loaded whole files into memory before slicing; it now streams only the requested
  window and reports the real file size. Process output reads are bounded with a read ceiling
  instead of accumulating unbounded output.
- A tool writing more than the pipe buffer to stderr could freeze the CLI or GUI forever;
  stderr is now drained.
- `edit_file` no longer converts every edited file to CRLF with a UTF-8 BOM; the original line
  endings and BOM state are preserved.
- SSE chunks sent as `data:{...}` without the optional space are no longer dropped.
- The cURL fallback got real connect/total timeouts plus a stream-stall watchdog instead of
  hanging on a blackholed server.
- GUI streaming no longer garbles non-ASCII text (UTF-8 instead of the ANSI code page), ends a
  turn only on a line-anchored prompt, wraps long user messages inside the bubble, restores saved
  sessions with their tool-call blocks intact, and no longer crashes on Markdig edge cases
  (ragged tables, links inside link labels).

[1.0.0]: https://github.com/randomNinja64/SimpleLLMChat
