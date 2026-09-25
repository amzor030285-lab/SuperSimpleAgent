# AGENTS.md

## Canonical build & test workflow

One command does the whole pipeline (product build + stage, test build + package, run all suites):

    cmd /c build-and-test.bat

Optionally pass a suite name to run only that suite in step 3:

    cmd /c build-and-test.bat SimpleLLMChatGUI

### Order matters — do not skip steps
- `run-tests.bat` only runs EXEs that already exist under `Tests-Build\`; it builds nothing.
- `build-tests.bat` rebuilds product + test projects AND copies fresh product EXEs (SimpleLLMChatGUI.exe, SimpleLLMChatCLI.exe, Newtonsoft.Json.dll, Markdig.dll) into every `Tests-Build\<Suite>\`. Without it, suites run against stale binaries.
- `build.bat` alone = product build + staging into `SimpleLLMChat\` only (no tests).
- After any source change, always use `build-and-test.bat` (or at least `build-tests.bat`) before `run-tests.bat`.

### MSBuild / process hygiene
- All MSBuild invocations use `/nodeReuse:false`; parallel worker nodes do not linger after builds.
- If orphan `MSBuild.exe` processes are ever found (e.g. after an interrupted build), kill them: `Stop-Process -Name MSBuild -Force`.

## Environment constraints
- Target: .NET Framework 4.0 (Client profile), C# 5-compatible scoping. A local variable name may not be reused in a nested block if it was declared in an enclosing method scope (CS0136 with this toolchain).
- No post-4.0 BCL APIs (e.g. no `StreamReader.ReadAsync`, no `string.Contains(char)`).
- Files are CRLF, UTF-8 without BOM. After editing, verify CRLF was preserved and fix if an editor flipped the file to LF.
- Build toolchain: VS2019 Build Tools MSBuild (located via vswhere in the .bat scripts).

## Tests
- Suites (filter name for `run-tests.bat [Suite]`): FileTools, MemoryTools, SkillTools, ShellTools, PythonTools, WebTools, DesktopTools, SimpleLLMChatCLI, SimpleLLMChatGUI.
- GUI suite takes ~4 min; a `SLOW` verdict on `gui.window_and_long_chat` is normal and still passes. CLI suite has 32 tests. `cli.curl_fallback_stall` and `cli.curl_fallback_connect_timeout` are inherently `SLOW` (they wait out the stall/connect timeouts) and still pass.
- Logs: `Tests\logs\<runid>\`.
- One known transient failure: `gui.code_block_copy` can fail with `CLIPBRD_E_CANT_OPEN` if another process holds the clipboard; a re-run passes.

## Git
- Staged release binaries are tracked: `SimpleLLMChat\SimpleLLMChatGUI.exe` and `SimpleLLMChat\SimpleLLMChatCLI.exe`. Commit them together with the source changes when they were rebuilt by `build.bat`.
- Working branch in this session: `markdig-render`.
