# AGENTS.md

## Canonical build & test workflow

One command does the whole pipeline (product build + stage, test build + package, run all suites):

    cmd /c build-and-test.bat

Optionally pass a suite name to run only that suite in step 3:

    cmd /c build-and-test.bat SuperSimpleAgentGUI

### Order matters — do not skip steps
- `run-tests.bat` only runs EXEs that already exist under `Tests-Build\`; it builds nothing.
- `build-tests.bat` rebuilds product + test projects AND copies fresh product EXEs (SuperSimpleAgentGUI.exe, SuperSimpleAgentCLI.exe, Newtonsoft.Json.dll, Markdig.dll) into every `Tests-Build\<Suite>\`. Without it, suites run against stale binaries.
- `build.bat` alone = product build + staging into `SuperSimpleAgent\` only (no tests).
- After any source change, always use `build-and-test.bat` (or at least `build-tests.bat`) before `run-tests.bat`.

### MSBuild / process hygiene
- All MSBuild invocations use `/nodeReuse:false`; parallel worker nodes do not linger after builds.
- If orphan `MSBuild.exe` processes are ever found (e.g. after an interrupted build), kill them: `Stop-Process -Name MSBuild -Force`.

## Environment constraints
- Target: .NET Framework 4.0 (Client profile), C# 5-compatible scoping. A local variable name may not be reused in a nested block if it was declared in an enclosing method scope (CS0136 with this toolchain).
- No post-4.0 BCL APIs (e.g. no `StreamReader.ReadAsync`, no `string.Contains(char)`).
- Files are CRLF, UTF-8 without BOM (enforced for tracked text files by `.gitattributes` with `eol=crlf`). After editing, verify CRLF was preserved and fix if an editor flipped the file to LF.
- Build toolchain: VS2019 Build Tools MSBuild (located via vswhere in the .bat scripts).
- Do not normalize line endings with an inline PowerShell regex over source files. Defender on this machine flags that command shape (`Trojan:Win32/FileFix.BBA!MTB`). Use the git round-trip in the checklist below.

## Checks you must run before declaring work done
Run them in order. Every item here exists because skipping it has already produced a wrong result in this repository.

### 1. Line endings — use the git round-trip, not an editor
File-writing tools emit LF. For every file you created or edited:

    git add -A -- <files>
    Remove-Item <file> -Force; git checkout -- <file>
    git ls-files --eol <files>      # every line must read w/crlf

The round-trip also normalizes a file that was added with the wrong endings. For a file that is
not tracked by git (e.g. anything under `release\`), state the ending you used instead of claiming it was verified.

### 2. A new source file is not compiled until a .csproj lists it
MSBuild silently ignores a `.cs` that no project includes, so the feature simply never runs and
the build stays green.
- product file: add `<Compile Include="Name.cs" />` to that tool's .csproj
- `Shared\` file: add a linked `<Compile Include="..\Shared\Name.cs">` to EVERY project that uses it —
  `SuperSimpleAgentCLI.csproj`, `SuperSimpleAgentGUI.csproj`, and any test project that exercises it
- a tool `.json` manifest is content, not source: it ships next to the `.exe`, so it only changes in the release after step 5

### 3. Signature change: enumerate the call sites before editing
    Select-String -Path <sources> -Recurse -Pattern "MethodName\("
Count declaration + call sites, change all of them, then run the same search and confirm the count
is unchanged and every site compiles. A missed site is a build break at the least.

### 4. A new bundled dependency must reach the test environment
If a tool starts an external program, add it to BOTH `build.bat` (release staging) and
`build-tests.bat` (into `Tests-Build\<Suite>\`), including every runtime DLL it needs.
A test that skips when the binary is missing is hiding a failure of this step — treat such a skip as
a build bug, not as a tolerated condition.

### 5. `SuperSimpleAgent\` is generated — never edit it by hand
`build.bat` deletes and recreates that folder; only sources at the repo root are authoritative.
The folder is gitignored: the release artifact is the ZIP from `package.bat`, not the tree.
If a document, license, manifest or `.json` changes AFTER `package.bat` has run, re-run
`cmd /c build.bat` and `cmd /c package.bat <version>`, otherwise the archive ships stale text that
no longer matches the repository.

### 6. Tag last, and only after the archive has been verified
    git add -A
    git commit
    cmd /c build.bat
    cmd /c package.bat <version>            # the version argument is required; no arg exits 1
    <unpack release\SuperSimpleAgent-<version>.zip and confirm the new bits are inside>
    git tag -f <version>
The tag must point at the commit whose tree produced the ZIP. Moving a tag to an older commit, or
packaging before the last commit, leaves the release describing code it does not contain.

### 7. Confirm the behavior is really in the shipped binary
.NET string literals live in the metadata heap as UTF-16LE. A naive byte scan reports false
negatives, so either decode as UTF-16 or, better, prove it behaviorally with a test that calls the
code. "It compiled" is not evidence that the staged `.exe` was rebuilt.

### 8. Before `git commit`
- `git status --short` lists only the files you meant to touch
- `release\` and `Tests\logs\` are gitignored — never `git add -f` them
- the `SuperSimpleAgent\` staging folder is gitignored: never commit it; the release artifact is
  `release\SuperSimpleAgent-<version>.zip`, uploaded to GitHub Releases manually
- a staged document must be byte-identical to its root copy (`build.bat` is the only writer)

### 9. A user-visible change is documented in the same commit
`README.md`, `CHANGELOG.md` and `SECURITY.md` are maintained in this repo, and nothing in the build
catches them going stale — three commits here shipped a default change with the README still
promising the old one, and left `chatpath`, `streamstalltimeout`, `requesttimeout` and the presets
undocumented.

- a new or changed INI key goes into the README `Configuration` list with its real default, and
  changing a default is a README edit in the same commit, not a code-only change;
- `CHANGELOG.md` gets an `[Unreleased]` section (`Added` / `Changed` / `Fixed`) in the same commit;
  the version heading is filled in at release time;
- `SECURITY.md` when a credential path, trust boundary or vulnerability is touched;
- `readme_documents_every_ini_key` (GUI suite) derives the key list from a config actually written
  by `WriteInitialConfig` and fails if any key is missing from the README. Run it after touching
  `SettingsIniWriter`, and treat its failure as the real defect it is, not as a test to relax.

## Comparing against upstream
`upstream` is the author's repository: `https://github.com/randomNinja64/SimpleLLMChat.git`.
Use git, not the GitHub API — the API costs one fetch per file and a large amount of context:

    git fetch upstream --tags
    git log --oneline v1.4.5..v1.5                 # what a release range changed
    git diff --stat upstream/v1.5 -- <path>         # how far this fork has moved
    git show <commit> -- <path>                     # one commit's diff

Port behavior, not file layout. The upstream diffs are dominated by file splitting
(`LLMClient.Http`/`LLMClient.Request`, `Options.Settings`/`Options.ToolsUi`,
`ChatTurn.Blocks`/`Tags`/`View`, `CurlClient` into `Shared\CurlHttpsClient`), and this fork's
copies have moved on since. A renamed or split file is not a gap: compare tool manifest entries,
config keys and method signatures before concluding that something is missing. Also check whether
this fork already solved the same problem differently — a straight port can be a regression
(the upstream stderr-deadlock fix is redundant here: both streams were already drained, on top of
job-object teardown and the STOPTOOL abort).

## Tests
- Suites (filter name for `run-tests.bat [Suite]`): FileTools, MemoryTools, SkillTools, ShellTools, PythonTools, WebTools, DesktopTools, SearchTools, SuperSimpleAgentCLI, SuperSimpleAgentGUI.
- `run-tests.bat` builds nothing, but the suite EXE it runs honours `TEST_FILTER`: set it to a
  substring of the case name and only matching cases run. A GUI case takes minutes, the
  feedback loop without this is unusable — run `Tests-Build\<Name>.Tests\<Name>.Tests.exe`
  directly after `build-tests.bat`. Unset it before committing anything to the harness.
- `SuperSimpleAgentGUI` was unreliable on this machine: a rotating handful of cases failed with
  `sendButton never entered Stop state within N ms` (proved at bdda060 with `git stash` + a
  clean-tree run). Root cause was the harness, not the product: `GuiDriver` re-walked the whole
  window tree every 50 ms, so a wait made up to 600 UI Automation tree walks that competed with
  the UI thread being waited on. Fixed on 2026-10-01 - elements are cached between polls and waits
  are state-based ("not generating and the transcript stopped growing"), never a fixed grace
  period. A wait that gives up now reports caption + input state + process liveness.
- Still open, and it looks like a product bug rather than a harness one:
  `gui.stop_button_interrupts` fails ~50% of the time because the reply arrives truncated
  (~200 of 3000 chars) while the fake server keeps the stream open for 15 s. The app's caption
  returns to `Send` as if the turn had ended. Do not "fix" it by widening a timeout or lowering
  the expectation - find out what ends the turn.
- The Options window is a category list, not tabs: a control on the `SystemPage` is only in the
  automation tree after `SelectListItemInWindow("Options", "CategoryListBox", "System", ...)`,
  and reopening Options starts back on category 0. `x:Name` does supply the AutomationId.
- SearchTools needs no external binary: the suite runs its own loopback `HttpListener` stand-in for Everything's HTTP server, so it passes on a machine with no Everything installed.
- GUI suite takes ~4 min; a `SLOW` verdict on `gui.window_and_long_chat` is normal and still passes. Current sizes: FileTools 19, SuperSimpleAgentCLI 88, SuperSimpleAgentGUI 38, DesktopTools 30, ShellTools 27, WebTools 4 (+1 skip), PythonTools 2, MemoryTools 3, SkillTools 2, SearchTools 14. `cli.curl_fallback_stall`, `cli.curl_fallback_connect_timeout` and `cli.tool_tree_killed_on_timeout` are inherently `SLOW` (they wait out the stall/connect/tool timeouts) and still pass. `gui.second_turn_still_streams` sits right on the 5 s `SLOW` line by design — it deliberately streams a long reply so it can assert the text is on screen while the turn is still running.
- Logs: `Tests\logs\<runid>\`.
- One known transient failure: `gui.code_block_copy` can fail with `CLIPBRD_E_CANT_OPEN` if another process holds the clipboard; a re-run passes.

## Git
- Never `git commit` or `git push` without the user's explicit permission or direct order.
  A finished task, a green test run, or approval of a plan is NOT permission; changes stay in
  the working tree until the user says to commit/push.
- The `SuperSimpleAgent\` staging folder and `release\` archives are gitignored; the release ZIP
  is uploaded to GitHub Releases manually.
- Working branch: `main`. No `origin` remote is configured yet — publishing is the user's manual step.
