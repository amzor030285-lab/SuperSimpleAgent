# Changelog

All notable changes to this project are documented in this file. This repository is a
continuation of the original [SimpleLLMChat](https://github.com/randomNinja64/SimpleLLMChat);
everything listed below was added on top of that upstream baseline.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [2.1.0] - 2026-10-18

### Added

- **Safety guard for unattended tool execution** - A deterministic rule engine (`guardrules.json` shipped next to the CLI executable, evaluated in `Shared\CommandGuard.cs`) screens tool calls in `auto`/`config` modes: banned dangerous commands (disk formatting, shutdown, `mshta`/`regsvr32`/`bitsadmin`, obfuscated PowerShell, HKLM registry writes, service and account changes, force-push, ...), pipe-to-shell and exfiltration regex patterns, and file operations targeting protected system paths (Windows, Program Files, other users' profiles, ...). A blocked call is never executed: the model receives a synthetic error with the rule id and reason and continues on its own. Three blocks of the same reversible rule stop the turn; a first irreversible/remote-execution block taints the turn, and the next block of any kind stops it. New INI key `autoguardenabled` (default `1`) turns it off for a session; it is never active in `ask` or `plan` modes. Rule patterns adapted from open-source projects (safe-chains, shellguard, OpenHands), documented in `SECURITY.md`.
- **Guard banned list expanded (rules version 2)** - 33 new banned entries curated from the LOLBAS catalog cover the common LOLBins v1 missed: .NET assembly runners (`installutil`, `regasm`, `regsvcs`), `cmstp`, `msxsl`, `forfiles`, `esentutl`, `mofcomp`, `pnputil`, `mavinject`, `msdt`, `odbcconf`, `pcalua`, `pcwrun`, `scriptrunner`, `verclsid`, `xwizard`, `hh`, `diskshadow`, `makecab`, `mmc`, `presentationhost`, `ieexec`, `ftp`, legacy scheduled-task creation via `at` (the primary persistence vector on the XP/Win7 targets), application shimming via `sdbinst` (T1546.011), and `infdefaultinstall`; plus lower-risk t1 entries `control`, `write`, `winfile`, `unregmp2`, `rasautou` and `syncappvpublishingserver`. `msiexec` is promoted from t1 to t2, since `/i` accepts remote URIs (remote code execution).
- **Consecutive failed tool call limit** - New `maxconsecutivefailedtoolcalls` setting (default `10`, `0` = off): a turn is halted with `[Stopped: N consecutive tool calls failed]` when N tool calls in a row return a non-zero exit code; any success resets the streak, and calls that did not run (disabled or cancelled) do not count. Editable in `Options` -> `Tools`.
- **cURL POST body via named pipe** - `PostJson` (the Firecrawl search and scrape paths) now writes the JSON body to a named pipe and tells curl to read it with `--data-binary "@\.\pipe\..."` instead of quoting it inline on the command line, so bodies with quotes, backslashes, ampersands or unusual length can no longer be mangled by the shell.
- **Jev tool-approval decision gate** - Ported from the upstream author's `Auto Tool Approval (Jev)`. When `jevbaseurl` points at a System One decision endpoint (`/v1/systemone` - e.g. Ollama ≥ 0.35.0 with `ollama pull nimble`, or a llama.cpp decision model such as Kev-4B), a small decision model answers the tool-approval question before the human does for **every tool call** in `ask`, `config` and `auto` modes: an explicit `allow` runs the call without prompting (`[tool auto allowed, confidence 0.97]`), anything else - a `confirm`, an unknown choice, a malformed answer, a dead endpoint - falls back to the normal approval prompt with the gate's confidence shown (`Jev confidence: 0.30`). Screening is per-mode switchable (new INI keys `jevscreenask` / `jevscreenconfig` / `jevscreenauto`, all default `1`, off doesn't consult the gate in that mode); in `config` mode with the switch on, calls outside `toolsrequiringapproval` are gated too instead of running untouched. The gate never answers in `plan` mode, never screens a disabled tool call, never bypasses the safety guard (the guard keeps screening the calls it always screened), and is completely off until `jevbaseurl` is set. Additional INI keys: `jevbaseurl`, `jevmodel`, `jevapikey` (DPAPI-protected), `showjevconfidence` (default `1`). Tool arguments and recent messages are sent to the endpoint as untrusted data; see `SECURITY.md`. All of it is edited on `Options` -> `System`, including the three mode switches.

- **Interactive CLI input** - The CLI's interactive prompt now edits the input line in place instead of a plain `Console.ReadLine()`: arrow keys, `Home`/`End`, `Delete` and `Backspace` move and edit the cursor, `Tab` completes the leading `/command` word (repeating `Tab` cycles through the candidates), and `Up`/`Down` walk the input history (the last 500 entries). A new `/help` command repeats the grouped command list on demand, and an unknown `/...` word is answered with `Unknown command ... /help shows the command list` instead of being sent to the model as chat text.

### Changed

- **`run_python_script` prompts by default** - `toolsrequiringapproval` now defaults to `run_shell_command,download_file,run_python_script`: the Python tool executes arbitrary code exactly like the shell does, so it belongs in the same approval class. Configurations that already have the key keep their own list; the Jev gate (when configured) can auto-answer these prompts as any other.
- **Bundled yt-dlp updated** - `deps\yt-dlp.exe` is now the nicolaasjan Windows-XP-compatible build `2026.10.11.155708` (replacing the August 2026 build), bringing the current YouTube extractor behavior (e.g. the `tv_samsung` client) to `download_video`.
- **CLI errors in the GUI** - The GUI now redirects the CLI's stderr (UTF-8) and shows each line as its own turn in the chat transcript, so errors the CLI prints (e.g. `Error: Invalid approval mode...`) are visible instead of silently lost.
- **CLI launch from a foreign working directory** - The GUI starts the CLI by absolute path (next to the GUI executable) and sets that folder as its working directory, so the CLI is found even when the GUI is launched from a shortcut with a Start-in folder other than the install folder.

### Fixed

- **Temp offload files now age out** - outputs too large for a tool result are saved to `%TEMP%\webtools-offload` so the model can read them in parts; the folder now purges files older than 7 days on every write instead of growing without bound.
- **Orphaned tool processes and leaked handles** - a tool process whose stdout reader fails mid-stream is now killed instead of left running, the per-tool kernel job handle is released even when writing tool stdin fails, redirected pipe handles from `Tools\ToolHelper` are closed on every path, a context provider hung while assembling the request can be interrupted with Stop, and the GUI releases a child CLI that failed during startup.
- **False API errors in streamed replies** - A streamed response whose text merely contained the word "error" (e.g. `{"choices":[...,"content":"error",...]}`) was misreported as `[API Error]`, because the parser looked for the raw substring `"error"` in the JSON. The parser now parses the event first and only a real `error` member counts as an API error.
- **write_file stripped surrounding whitespace** - `write_file` trimmed leading and trailing whitespace from `content`, corrupting files that rely on leading blank lines or trailing newlines; the content is now written exactly as given.

- **Clock icon in the Scheduled button on Windows XP** - the button's content was the U+23F0 emoji clock, and XP ships no font containing that glyph, so the button came up empty unless an emoji font was installed; the clock is now drawn as vector geometry and renders on every Windows version without a font dependency.

- **Attach-image button icon** - the image-attach button used a raster PNG icon that did not match the rest of the header and kept its colour in both themes; it is now drawn as vector geometry in the same style as the scheduled-tasks clock and takes the button's text colour, so it follows the light and dark theme.

## [2.0.0] - 2026-10-13

### Changed

- **Renamed the project to SuperSimpleAgent (SSA)** - Solution, projects, namespaces, executables and the release folder are now `SuperSimpleAgent` / `SuperSimpleAgentGUI` / `SuperSimpleAgentCLI`; the GUI window title is `Super Simple Agent` and the named pipes are `SuperSimpleAgent.Control.*` / `SuperSimpleAgent.Status.*`. The old SimpleLLMChat names are gone everywhere except historical notes and credits.
- **New app icon** - `sllmc.ico` is replaced by `ssa.ico` (orange/bone orbital tile) in both executables.
- **Credits** - The README credits section and the MIT license now name the fork maintainer, [amzor030285-lab](https://github.com/amzor030285-lab).
- **RAG vector search** - `VectorStore.Search` retains only the top-K candidates while scanning (min-of-K eviction) instead of scoring every chunk and sorting the full result, so a K-neighborhood query no longer pays for sorting all chunks in the index.

### Fixed

- **Cyrillic in the standalone CLI on Windows XP** - the CLI no longer forces the console into UTF-8 when stdout is a live console on XP or earlier. XP has no UTF-8 console code page, so raw UTF-8 bytes were decoded as OEM (CP866) and Cyrillic rendered as garbage; the old UTF-8 switch also stayed on the console after the CLI exited. On such systems the CLI now explicitly resets both input and output to the system OEM code page (866 on Russian installs) instead of leaving the console in whatever state it was in; when stdout is a pipe (the GUI) or on any later OS, UTF-8 output is unchanged.
- **App icon on Windows XP** - `ssa.ico` is re-encoded with plain BMP frames instead of PNG-compressed ones: PNG-compressed .ico images do not render on Windows XP.

## [1.1.0] - 2026-10-05

### Added

- **In-app scheduled tasks** - The GUI's `Scheduled` button opens a manager for timed tasks. A task runs its prompt on a schedule - `at <time|offset>` (one-shot), `every <interval>` (recurring), or a 5-field cron expression / `@hourly`-style alias, optionally with a time zone - as a separate headless `SimpleLLMChatCLI.exe --session <name>` pass, so it never disturbs the live chat and writes to its own session file. Each task records its next run, last status, last output snippet and a recent-run history; a task that fails repeatedly is auto-disabled, and a one-shot `at` alarm whose instant was missed while the app was closed still fires once. Tasks persist in `scheduled-tasks.json` next to the executable. A new `--session <name>` CLI flag loads/saves the named session (used by the scheduler); the default one-shot behavior is unchanged.

- **Schedule builder in the scheduled-tasks form** - the raw "Schedule" text field is replaced by a small visual control with three modes (one-shot, recurring, cron). One-shot offers either "after N seconds/minutes/hours/days" or an exact local date and time; recurring offers "every N ..."; cron offers daily / weekday / weekly / monthly presets plus a custom 5-field expression. The control live-validates the expression against the same parser the engine uses and shows the exact schedule string together with the next run time (or the parse error), so an invalid schedule cannot be added.

- **Plan mode, a fourth tool mode** - `ask` -> `auto` -> `config` -> `plan` on the GUI toggle, or
  `/plan on|off`, or `--approval plan`. While it is on the model may read files, search the web,
  inspect windows and query memory or skills, and the only thing it may write is `plans\plan.md`
  where it puts the plan for you to read and approve. Everything that could change state is
  refused in `ToolRegistry.ExecuteToolCall` before the tool process starts, checked by target path
  rather than by tool name, so a plan cannot quietly turn into an edit. Modelled on OpenCode's
  plan mode, but with the guarantee OpenCode's non-experimental mode does not have: OpenCode only
  asks the model to behave (`edit` and `bash` stay `allow`, the tool list is unchanged), so a
  determined model can still write there. Turning plan mode off returns the button to `ask`.
   The system prompt is rebuilt every turn, so it never records *when* the mode changed; turning
   plan mode on or off therefore also drops a short reminder into the conversation itself, so the
   model sees the switch in context rather than having to infer it. Back-to-back toggles with no
   chat between them collapse into a single reminder, keeping the message roles alternating for
   picky OpenAI-compatible endpoints; `/plan` takes only `on`/`off`.

- **Per-model repetitive-output guard, off by default** - a `Stop repetitive output automatically`
  box on the System page of Options, next to `Context Window Size`, writes `repetitionguard` to
  `LLMSettings.ini`. The key already existed and defaulted to on, so the guard was running with no
  way to see it; how prone a model is to loop is a property of the model, so it is now off unless
  asked for - the box, or `repetitionguard=1` by hand.

- **Versioned provider endpoints and saved presets** - a base URL may carry the version
  (`.../v1`), or the chat path may be set separately in `chatpath`; the CLI detects it when the
  field is left empty, which is right for every provider that keeps the version in the base URL.
  System settings can be saved as a named preset and recalled instead of retyping a provider,
   model and key.

- **Response and stall timeouts in Options** - two boxes on the System page of Options, next to
  `Context Window Size`, let you override `requesttimeout` (the whole-answer cap, default 300 s)
  and `streamstalltimeout` (the silent-stream cap, default 120 s) in seconds. Both keys already
  existed in `LLMSettings.ini` with no way to change them from the app, so a slow model that blew
  past the 120 s stall or the 300 s request cap could only be fixed by hand-editing the config.

### Changed

- **The header `Scheduled` button is now an alarm icon** - the text button (min width 80 px) is
  a compact `⏰` icon button (min width 40 px, like the theme button), so it takes less space in
  the header row. The button keeps its AutomationId, tooltip and click behavior.

- **A repeating response is cut after a few copies, not 4000 characters** - the guard counted
  characters of an unbroken repeat, so a model looping an 80-character block had to get through
  4000 of them, about 15 repetitions of text already scrolled past. It now counts copies: four
  back-to-back copies of one block, confirmed on three checks in a row, so the example log goes
  from 30.5 s to 0.77 s. Three problems that hid loops are gone: one irregular copy no longer
  ends the walk backwards (a single missing full stop was enough), text is normalized on arrival
  so a chunk boundary cannot split a line or a fence marker, and fenced code plus Markdown table
  rows are held out of the comparison - a 30-times repeated code loop and a table of 100 identical
  rows are both literally periodic, and neither is a runaway.

### Tests

- The GUI driver no longer re-walks the whole window tree every 50 ms while waiting for
  a turn. Re-finding `sendButton` that way meant up to 600 full UI Automation tree walks
  per wait, competing for CPU with the UI thread whose state the wait was for - a rotating
  handful of cases failed with `sendButton never entered Stop state` depending on machine
  load, on cases with nothing in common. The elements are now remembered between polls.

- Waits are state-based rather than time-based. "Turn finished" used to be inferred from
  a 1.5 s grace period, and the send button's caption alone does not prove it: the app
  flips the caption back to `Send` when generation completes, but the last of the text can
  still be arriving, so a caller could assert on a half-written reply - which is exactly
  what `gui.second_turn_still_streams` was doing about half the time. A wait now ends on
  "not generating and the transcript has stopped growing". A failed wait also reports what
  it saw (caption, input state, whether the process is alive), and `StartTurn` confirms the
  text reached the input before clicking, so a lost click is no longer reported as a
  timeout.

- Known remaining: `gui.stop_button_interrupts` fails about half the time, and its
  transcript shows why - the reply is cut short (~200 of 3000 characters) on a stream the
  server holds open for 15 s, before the test gets to press Stop. That is an unanswered
  question about the product, not the harness, and is left visible rather than retuned
  away.

### Fixed

- **Repeated compaction no longer drops the previous summary** - a second compaction in one turn
  summarized the conversation again from scratch, because the summary it was meant to build on was
   written after the pass that reads it had already taken its slice. The recap of a recap is now
   built from the recap.

- **The repetition guard no longer misses a loop, and no longer misfires.** The copy
  count was capped by a floor division, so a loop whose block length does not divide the
  200-character minimum (for example a 44-character block) was walked short of the mass
  floor and cut off from being detected at all; the cap is now a ceiling. A run of
  backticks inside a table row could latch the code-fence state and freeze the comparison
  window for the rest of the answer, and a frozen window kept re-confirming a stale repeat,
  so a long code block that followed a bounded repeat was cut as if it were a loop; table
   rows are now held out of the fence state and a hit only counts when the window actually
   grows.

- **Scheduled tasks: persistence, threading and execution hardening** - the in-app scheduler's
  defects found in review are fixed. `scheduled-tasks.json` is now written atomically (payload to
  a sibling `.tmp`, then renamed over the real file) so a crash mid-write can no longer truncate
  it, and a corrupt or unreadable file is surfaced instead of being silently cleared and re-saved
  empty; the reader returns an empty list only for a missing file, checks the version, and repairs
  unset fields on load. The worker thread no longer races the UI: `GetJobs` and the save path hand
  the window a deep copy of each task, so the list and its run history can't be read while a run is
  mutating them. The child CLI's `stderr` is drained (a redirected-but-unread stream deadlocks
  past ~4 KB), session names and prompts are escaped for `CommandLineToArgvW` (embedded quotes can't
  corrupt the argv or inject flags), an impossible schedule (e.g. `0 0 31 2 *`) disables its task
  instead of throwing out of the worker, `MaxConsecutiveErrors <= 0` falls back to the default
  instead of auto-disabling after one success, run duration is now measured around the child
   process, the monthly cron preset no longer picks a day one earlier than shown, the schedule
   keywords now accept tab or any whitespace run (so `at\t20m` parses like `at 20m`), a date-less
   absolute `at` (e.g. `at 14:30`) now requires an explicit date instead of silently pinning to the
   parse day, and the worker stops calling `BeginInvoke` on a shutting-down dispatcher.

- **CLI `--session` handling and clean headless output** - `--session` is only honored as a flag
  before the first prompt word, so a `--session` typed inside a multi-word prompt is prompt text
  rather than being misread as the flag; an interactive session opened with `--session` now saves
  its turns to the named session instead of the `lastsession` mirror; and the `-- restored ...`
  banner is no longer written to stdout for a headless one-shot, so the scheduler's captured Output
  snippet is not polluted by startup chatter.

- **Emoji and other non-BMP characters survive CLI output on Windows XP** - reply
  text left the CLI through `Console.Write`, and on Windows XP the console's text
  path replaces surrogate pairs (any code point above U+FFFF - most emoji) with `?`
  even when `Console.OutputEncoding` is UTF-8, while BMP characters passed through
  fine. The reply now goes out through a plain UTF-8 `StreamWriter` on the
  standard-output stream, which encodes every code point to correct UTF-8 bytes
  without touching the console text stack, so the GUI - and any UTF-8 reader of the
  pipe - receives the full text.

## [1.0.4] - 2026-09-29

Cyrillic console output is decoded correctly, the chat stops yanking the viewport
to the bottom while a long answer streams, and long turns no longer cost
quadratically.

### Fixed

- **Loading a session no longer mixes stored context into the user's messages.** A saved
  conversation stores what was sent to the model, and a user turn is their message plus
  whatever context the app bakes in for the model - the memory and skills snapshots - so
  a loaded chat showed whole dumps of those snapshots inside the user's own bubble. The
  typed text is now stored next to the baked text and is what the chat window renders;
  sessions written before this are split on the known snapshot headers, which are
  unambiguous. Nothing about what is sent to the model changed, so the prompt cache still
  sees byte-identical history.

- **Cyrillic console output no longer arrives as mojibake.** `run_shell_command`
  output written in the OEM (CP866) or ANSI (CP1251) code pages - `tasklist` on a
  Russian system, Russian installers, legacy CLIs - came back as garbage. Strict
  UTF-8 (BOM or not) is still detected first; everything else is now scored by the
  bundled [Ude](https://github.com/nickg/IUde) (Universal Decoding Engine, MPL-1.1)
  library against Russian letter and punctuation statistics, so CP866 and CP1251
  output decodes to real Unicode instead of the old hand-rolled scorer misreading
  it. An explicit `--encoding` argument still wins over detection.

- **Scrolling up while the model streams no longer snaps back to the bottom.**
  `ScrollChatToEnd` ran on every flush, so the viewport was forced to the bottom
  ~20 times a second and earlier text could not be read mid-generation. A user
  scroll now pauses autoscroll; it resumes automatically once the user scrolls back
  to within 24 px of the bottom.

### Performance

- **Long turns stream cheaper.** Every flush re-lays out the whole `FlowDocument`,
  so a fixed 50 ms flush interval cost quadratically over a long turn. The interval
  now grows with the active turn's length from 50 ms up to a 250 ms cap and returns
  to 50 ms for the next turn; short turns stream exactly as before.

- **Software-rendered displays get GDI-style text metrics.** WPF pays for the
  high-quality text path on every layout, which is brutal on GPUs without hardware
  acceleration - the common case on XP. When `RenderCapability.Tier` reports
  software rendering, the window switches to `TextFormattingMode.Display`, which is
  markedly cheaper and renders sharper.

### Tests

- Four decoder unit tests plus a regression test that feeds a captured real CP866
  `tasklist` header through the exact decode path the tool uses, and the CLI suite now
  copies the bundled `Ude.dll` into its tool sandboxes like the build scripts do.
  ShellTools grows from 13 to 27 tests. Two further tests cover the stored-context split
  above: a session round-trip that checks the file keeps the typed text next to the baked
  one, and a GUI load of a crafted session file that must not render the snapshot as the
  user's message.

Tested on Windows 11 (x64): FileTools 19 pass, MemoryTools 3, SkillTools 2, ShellTools
27, PythonTools 2, WebTools 4 pass + 1 skip, DesktopTools 30, CLI 60, GUI 19 - 166 pass,
0 fail. Windows XP SP3 (x86) has not been re-checked for this release yet.

## [1.0.3] - 2026-09-28

Two runaway-generation fixes, found when a model looped and the GUI stopped responding,
plus two prompt-cache fixes, found by reading the server's own `prompt_tokens` counter.

### Fixed

- **The GUI no longer burns a CPU core on a fast stream.** Every stdout chunk was queued
  onto the dispatcher and appended to the chat document on its own, and each append
  re-measures the whole `FlowDocument` plus a full autoscroll. A long turn therefore cost
  quadratically, and a model streaming quickly saturated the UI thread badly enough to look
  hung. Streamed text is now buffered and rendered in one append every 50 ms, and flushed
  at every turn boundary (generation complete, ready, tool approval) so no tail is lost.

- **A model that loops is stopped instead of streaming forever.** Repetition produced a
  continuous stream, so the stall watchdog could not see it, and because no output cap is
  sent to the server nothing else bounded the response: it ran until the 300 s request
  timeout with an ever-growing conversation. A new guard detects a repeated block in the
  tail of the response and aborts the request, reporting that the model was repeating
  itself rather than blaming the user for pressing Stop. Set `repetitionguard=0` to turn
  it off. The check keeps a fixed-size window rather than the whole response, so its cost
  does not grow with the answer.

Both of these were unverifiable from a test run alone, so the cache fixes below were
measured instead: a prompt is only served from the server's cache while it stays a
byte-for-byte prefix of the previous one, and any change near its end invalidates all of it.
Every number below is the server's reported `prompt_tokens` minus the tokens the server
had cached, on a real 200k-context model.

### Fixed

- **A context injector no longer invalidates the cache on every turn.** When a tool package
  declares a `context_injector`, its snapshot was re-fetched each turn and appended to the
  serialized copy of the last user message, leaving the stored message untouched. The next
  request therefore ended in a different final user turn, nothing before it was a prefix any
  more, and the entire conversation was reprocessed. The snapshot is now baked into the
  stored user message when the turn is created, so the same bytes are re-sent for the rest of
  the conversation. Measured on a two-turn exchange: about 148 recomputed tokens where the
  old code spent 46. The injector still runs once per user turn, so the context it reports is
  no fresher than before; the conversation now keeps one snapshot per turn instead of only
  the newest.

- **A reasoning model's thinking is sent back to it instead of being thrown away.** A model
  that thinks before answering streams that thinking as `reasoning_content`, and the server
  caches the whole stream, not just the visible answer. Only the answer was kept, so the next
  prompt diverged from the cached one immediately after the user message and the history was
  reprocessed from there. The thinking is now accumulated even when the UI hides it, stored on
  the assistant message (tool-call messages included, so an agent loop stays stable too) and
  echoed back in the request; it also survives saving and reloading a session. Measured:
  61-67 recomputed tokens per turn before, 24-25 after. `preserve_thinking` is deliberately
  not asked of the server, since the model produced the thinking either way and the previous
  code simply discarded it. The cost is context: the thinking stays in the conversation.

Tested on Windows 10/11 (x64): FileTools 19 pass, MemoryTools 3, SkillTools 2, ShellTools 13,
PythonTools 2, WebTools 4 pass + 1 skip, DesktopTools 30, CLI 59, GUI 18 - 150 pass, 0 fail.
Windows XP SP3 (x86) has not been re-checked for this release yet.

## [1.0.2] - 2026-09-28

Bug-fix release. Feature set is identical to 1.0.1; verified on Windows 10/11 (x64) and
Windows XP SP3 (x86): FileTools suite 19 pass, CLI suite 46 pass, GUI suite 16 pass.

### Fixed

- **`grep_search` can match non-ASCII patterns again.** The pattern was passed to `grep.exe`
  on the command line, and Windows encodes the command line with the ANSI code page, so a
  Cyrillic pattern reached grep as mojibake bytes and never matched the UTF-8 file contents
  (`No matches found` on files that clearly contained the word). The pattern is now written to
  a temporary file in UTF-8 without a BOM and handed to grep with `-f`, which reads it as raw
  bytes. Literal Cyrillic patterns and character classes now match; Latin patterns are
  unaffected. Inherited from upstream, not a regression of this repository.

  Known limitation, unchanged: the bundled GNU grep 2.5.4 matches bytes rather than
  characters, so a metacharacter placed inside a non-ASCII word (`Арт.м`, `Арт[ёе]м`) still
  does not match. Fixing that needs a grep build with a UTF-8 locale.

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
