[![SSA Icon](SuperSimpleAgentGUI/ssa.ico)](SuperSimpleAgentGUI/ssa.ico)

### SuperSimpleAgent

*AI Chatbot/Agent for Windows XP+*

## About

SuperSimpleAgent is a lightweight C# CLI and GUI application that makes LLMs accessible on both legacy and modern Windows systems. The software is designed to work with OpenAI-compatible endpoints and can be extended via a tool system.

This project is a continuation of the original [SimpleLLMChat](https://github.com/randomNinja64/SimpleLLMChat) by [randomNinja64](https://github.com/randomNinja64), extended with session management, context compaction, tool approval modes, and hardened tool execution. The fork is maintained by [amzor030285-lab](https://github.com/amzor030285-lab). See [Credits](#credits).

This fork maintains its own version series (starting at 1.0.0), independent of upstream SimpleLLMChat releases.

## Features

-   **Customizable GUI** - The GUI's colors and fonts can be customized, with light/dark themes and several built-in palettes.
-   **GUI/CLI Modes** - The software can be used either via its REPL CLI or a GUI wrapper.
-   **Image Support** - Images can be passed to supported models via both the CLI and GUI.
-   **Markdown Rendering** - The GUI renders Markdown, including copyable code blocks.
-   **OpenAI-Compatible Endpoint Support** - In addition to OpenAI, any service/server offering the OpenAI v1 chat endpoint can be used, including [llama.cpp](https://github.com/ggerganov/llama.cpp) and [LM Studio](https://lmstudio.ai/).
-   **Retrieval Augmented Generation (RAG)** - Retrieve information from documents via a local knowledge folder (see [RAG](#configuration)).
-   **Script-Friendly** - Output without tool calls, tool outputs, and reasoning can be piped out of the CLI using the `-o` or `--output-only` flag.
-   **Streaming Responses** - Responses are streamed from the LLM server in real-time.
-   **Context Compaction** - Long chats are managed automatically before the context window fills: old tool output is dropped first, then the old part of the conversation is summarized into a recap (the newest ~8k tokens are kept verbatim) before anything is destroyed, with bounded truncation only as a last resort. `/compact` and `/compactfull` run the same passes on demand.
-   **Session Management** - Sessions can be saved, loaded, renamed and deleted (`/save`, `/load`, `/sessions`, `/rename`, `/delete`). The current chat is mirrored to a `lastsession` file after every turn and restored automatically on the next launch.
-   **Scheduled Tasks** - The GUI runs prompts on a schedule (`at <time|offset>`, `every <interval>`, or a 5-field cron expression / `@hourly`-style alias). Each task runs as a separate headless CLI pass into its own session file, independent of the live chat, and keeps a per-task run history with the last output.
-   **Tool Approval** - Tool calls can be gated: `config` (default, prompts for the tools listed in `toolsrequiringapproval`), `ask` (prompts for every tool call), `auto` (never prompts), or `plan` (read-only). Set via `--approval` (CLI), `/approval` or `/plan on|off` (REPL), or the toggle button in the GUI, which cycles `ask` -> `auto` -> `config` -> `plan`.
-   **Plan Mode** - A read-only phase for planning work. The model may read files, search the web, inspect windows and query memory or skills; the only thing it may write is `plans\plan.md` next to the executable, where it puts the plan for you to read and approve. Anything that could change state - shell commands, scripts, downloads, edits, deletes, window clicks and keystrokes - is refused before the tool starts, so a plan cannot quietly become an edit. Turning plan mode off returns the button to `ask`.
-   **Encrypted Settings** - API keys are stored on disk protected with Windows DPAPI instead of plain text.
-   **Bounded Execution** - File reads and process output are capped so a single huge result cannot exhaust memory, and every tool runs in a Windows job object so cancel or timeout kills the whole process tree, not just the direct child.
-   **Tools** - A modular, extensible tool system is available. For information about the included tools, see [Tools](#tools).

## Requirements

-   Access to the OpenAI API or a compatible LLM server
    -   **Note**: If your server or endpoint requires modern TLS that the OS does not support, a `curl.exe` and `curl-ca-bundle.crt` can be placed in the same folder as the application executable as a fallback (already included in the release build).
-   Windows XP (SP3 recommended) or later

## Setup

To use the application, download the latest release, extract it, and run the appropriate executable from the `SuperSimpleAgent` folder.

On first launch of `SuperSimpleAgentGUI.exe`, if `LLMSettings.ini` does not exist next to the executable, a setup wizard walks through System settings. Settings can also be changed later via Options.

Once configured, the software can be used via the `SuperSimpleAgentCLI.exe` or `SuperSimpleAgentGUI.exe` executables.

## Building

The project can be built via the included `build.bat` script (Visual Studio/Visual Studio Build Tools/MSBuild required).

Alternatively, the project can be built using the `SuperSimpleAgent.sln` file directly in Visual Studio.

Both methods require the .NET 4.0 Targeting Pack to be installed.

On a fresh clone run `nuget restore SuperSimpleAgent.sln` once: the projects reference Newtonsoft.Json 13.0.4 through `packages.config` into the gitignored `packages\` folder.

**Note**: Builds generated using these methods include only the core executables by default. Optional binaries (`7za.exe`, `grep.exe` and its DLLs, `curl.exe`, `curl-ca-bundle.crt`, `yt-dlp.exe`) are packaged when present under `deps\` or can be manually placed beside the matching tool's executable. Optional binaries are specified in [Tools](#tools).

**Note**: There is no CI. The target is the .NET 4.0 Client Profile (Windows XP+), which GitHub-hosted runners cannot build; `build-and-test.bat` from the repo root is the canonical pipeline, and `AGENTS.md` documents the full build/test protocol and known harness quirks.

## Tests

Test projects live under `Tests\` and are not included in release builds. There are ten suites: FileTools, MemoryTools, SkillTools, ShellTools, PythonTools, WebTools, DesktopTools, SearchTools, SuperSimpleAgentCLI, and SuperSimpleAgentGUI. From the repo root, run:

```
build-and-test.bat
```

which builds the product and test projects, stages the release, and runs every suite. To run a single suite, pass its name (`build-and-test.bat SuperSimpleAgentCLI`). Alternatively, `build-tests.bat` rebuilds and stages the test binaries and `run-tests.bat [Suite]` runs the already-built suites; logs are written under `Tests\logs\`.

**Note**: For cases that need external executables (7za, grep, cURL, YT-DLP), either put them in `deps\` before `build-tests.bat`, or place them beside the matching tool executable before `run-tests.bat`.

## Configuration

On first use, if `LLMSettings.ini` does not exist, the GUI setup wizard creates it from System settings. Appearance, RAG, Tools, and other options can be changed later via Options or by editing the INI manually. The options available within the INI file are as follows:

**Appearance**

-   `assistantname`: Display name for assistant responses (default `LLM`)
-   `codeblockfontfamily`: Font used for rendering code blocks in the GUI
-   `customfontfamily`: Font used for all non-code block text in the GUI
-   `fontsize`: Font size for chat and input text
-   `markdownparsing`: Enables/disables Markdown rendering in the GUI (`1` or `0`)
-   `thinkingdisplay`: Model reasoning appearance (`shown`, `collapsed`, or `hidden`; default `collapsed`)
-   `toolcalldisplay`: Model tool call appearance (`shown`, `collapsed`, or `hidden`; default `collapsed`)
-   `tooloutputdisplay`: Tool result appearance (`shown`, `collapsed`, or `hidden`; default `shown`)

**RAG**

-   `ragenabled`: Enables RAG (`1` or `0`, default off)
-   `ragallowedextensions`: Comma-separated file extensions to index (blank = built-in defaults)
-   `indexchunkoverlap`: Chunk overlap in lines (default `10`)
-   `indexchunklines`: Chunk size in lines (default `60`)
-   `embeddingsapikey`: Embeddings API key (`apikey` if blank)
-   `embeddingsendpoint`: Embeddings API base URL (`llmserver` if blank)
-   `embeddingsmodel`: Embeddings model name (required)
-   `ragknowledgepath`: Knowledge folder path (`knowledge` folder next to EXE by default)
-   `ragmaxsnippetlength`: Max characters per chunk for embedding/injection (default `2000`)
-   `ragmaxresults`: Number of chunks to retrieve per search (default `5`)
-   `ragretrievemode`: When to retrieve information from knowledge (`everyturn` or `newchat`) (default: `newchat`)

**System**

-   `apikey`: API key (if required by model provider)
-   `chatpath`: Chat completions path appended to `llmserver`. Leave empty and the CLI detects it from the endpoint, which is right for every provider that keeps the version in the base URL
-   `llmserver`: Base URL of the OpenAI-compatible endpoint
-   `model`: Model name to use for text generation (if supported by the endpoint)
-   `sysprompt`: System prompt for the LLM
-   `contextWindowSize`: If known, can be set here to enable automatic context management when context fills
-   `approvalmode`: Tool approval mode (`ask`, `auto`, `config`, or `plan`; default `config`)
-   `streamstalltimeout`: Seconds a stream may stay silent before it is abandoned (default `120`)
-   `requesttimeout`: Seconds before the whole request is abandoned (default `300`)
-   `repetitionguard`: Stop a generation that has degenerated into repeating the same block of text (default `0`, off). How prone a model is to loop is a property of the model, so this is off by default and enabled per model in `Options` -> `System`, next to `Context Window Size`

**Tools**

-   `tools`: Comma-separated list of tools the AI is allowed to use
-   `toolsrequiringapproval`: Comma-separated list of tools that require manual approval for the AI to use (default: `run_shell_command`, `download_file`)
-   `maxconsecutivefailedtoolcalls`: Maximum number of tool calls that can fail in a row before the turn is stopped (default `10`; `0` = off)
-   `autoguardenabled`: Safety guard for unattended tool execution in `auto`/`config` modes: a deterministic rule set in `guardrules.json` blocks dangerous shell commands (disk formatting, shutdown, remote-code runners, registry/service/user changes, pipe-to-shell and exfiltration patterns) and file operations on protected system paths. A blocked call is not executed - the model gets an error with the rule id and a reason and continues. Three blocks of the same reversible rule stop the turn; an irreversible/remote-execution block taints the turn, and the next block of any kind stops it (default `1`, on; `0` = off). Never active in `ask` mode (the human is the gate) or `plan` mode
-   `tooltimeout.<toolname>`: Timeout for a tool call in seconds

Tool-specific configuration options for the included tools are available in the [Tools](#tools) section.

## Usage

**GUI**: To launch the GUI interface, open `SuperSimpleAgentGUI.exe`.

-   To adjust application settings, select `Options`. The category list on the left switches between pages; the System page holds the model, its context size, saved presets for a provider plus model combination, and the per-model `Stop repetitive output automatically` guard.
-   To attach an image, press the image attachment button (camera icon) and choose a file using the file picker.
-   To clear the chat, press `Clear Chat`.
-   To send a message, type it into the input box and press `Enter` or `Send`. To create multi-line messages, press `Shift+Enter`.
-   The button in the bottom-left corner cycles the tool approval mode (`ask` -> `auto` -> `config` -> `plan`); the compaction button in the header runs a full compaction (equivalent to `/compactfull`).
-   To manage scheduled tasks, press the clock icon (`⏰`) in the header. Add a task by choosing a name, a schedule and a prompt. The schedule is picked with a small builder rather than typed in: one-shot (`at`, either "after N" or an exact local date/time), recurring (`every N`), or cron (daily / weekdays / weekly / monthly presets, or a custom 5-field expression). It validates as you type and shows the exact expression plus the next run time before you can add. Then enable/disable, remove, or inspect a task's next run, last status and recent runs.

**CLI**: To run the CLI in interactive/REPL mode, open `SuperSimpleAgentCLI.exe` directly or from a terminal.

Interactive mode provides the following commands:

-   `/approval [ask|auto|config|plan]`: Gets or sets the tool approval mode
-   `/buildindex`: Reconciles the knowledge index (adds/updates/removes changed files)
-   `/clear`: Clears chat context
-   `/clearindex`: Clears the on-disk knowledge index
-   `/compact`: Drops old tool messages
-   `/compactfull`: Full compaction (micro-compaction plus summarization of the old conversation)
-   `/delete name`: Deletes a saved session
-   `/exit`: Exits the application
-   `/image`: Sends an image to the model alongside the next message, used as `/image "path" prompt`
-   `/load keyword`: Loads a saved session
-   `/plan [on|off]`: Shortcut for read-only plan mode (`/plan` alone reports the current state)
-   `/reasoning`: Sets reasoning effort for supported models (`none`, `minimal`, `low`, `medium`, `high`, `xhigh`); `/reasoning` alone uses the API default
-   `/reload`: Reloads configuration in place (doesn't clear chat)
-   `/rename old new`: Renames a saved session
-   `/save [name]`: Saves the current session
-   `/sessions`: Lists saved sessions

The application can also be used in a non-interactive mode.

For a single prompt, use `SuperSimpleAgentCLI.exe <prompt>`.

To pass in an image alongside a prompt, use `SuperSimpleAgentCLI.exe --image <path> <prompt>`.

CLI mode provides the following additional flags:

-   `--no-banners`: Suppresses instructional prompts
-   `--session name`: For a one-shot prompt, loads `name` (instead of the default `lastsession`) and saves the conversation back to it when done, so a scheduled or scripted run does not disturb the interactive session
-   `-o`, `--output-only`: Outputs only the model's response text (no thinking, tool-call, or tool-output UI). Tools that do not require approval still run silently. If the model calls a tool that requires approval, the session ends with `Error: Model called <tool> which requires approval`.
-   `--reasoning-effort`: Sets initial reasoning effort for supported models (`none`, `minimal`, `low`, `medium`, `high`, `xhigh`)
-   `--approval ask|auto|config|plan`: Sets the initial tool approval mode

## Tools

SuperSimpleAgent includes the following 7 tool packages:

-   File Tools
    -   Tools
        -   `copy_file`: Copies a file from one location to another
        -   `delete_file`: Deletes a file from the file system
        -   `edit_file`: Replaces exact text in an existing file via one or more old\_string/new\_string edits
        -   `extract_file`: Extracts an archive to a destination directory
        -   `grep_search`: Recursively searches files for a regex pattern
        -   `list_directory`: Lists all files and subdirectories in a given directory
        -   `move_file`: Moves a file from one location to another
        -   `read_file`: Reads the contents of a local file (bounded excerpt of a configurable size)
        -   `write_file`: Writes contents to a local file
    -   Configuration Options
        -   `maxFileContentLength`: Maximum number of characters to read from a file or return from `grep_search` (default: `8000`)
    -   Dependencies
        -   `7za.exe`: used to extract archives
        -   `grep.exe` (+ `libiconv2.dll`, `libintl3.dll`, `pcre3.dll`, `regex2.dll`): used by `grep_search` (GnuWin32 GNU grep)
-   Memory Tools
    -   Tools
        -   `delete_memory`: Deletes a saved memory entry
        -   `list_memories`: Lists the names of all saved memory entries
        -   `recall_memory`: Reads a saved memory entry
        -   `save_memory`: Saves or updates a memory entry
        -   `search_memories`: Searches all memory entries by keyword
    -   Configuration Options
        -   `memoriesDirectory`: Directory for memory `.md` files (default: `memories` folder next to `MemoryTools.exe`)
        -   `maxContentLength`: Maximum length of a memory entry (in characters) (default: `2000`)
        -   `maxMemories`: Maximum number of memories to store (default: `50`)
    -   Context Injector
        -   `get_memory_context`: Creates a summary of saved memories to be injected into the system prompt
-   Python Tools
    -   Tools
        -   `run_python_script`: Creates and executes a Python script
    -   Dependencies
        -   Python must be installed and available on PATH.
-   Shell Tools
    -   Tools
        -   `run_shell_command`: Executes a shell command
-   Desktop Tools
    -   Tools
        -   `list_windows`: Lists visible top-level windows; optional `window_title` filter. Use for browsing or disambiguation - other tools accept `window_title` directly.
        -   `list_controls`: Lists controls with element indices (`#N`), roles and capture-relative rects (`@x,y` same space as a window screenshot)
        -   `navigate`: Pointer/focus - `click`, `double_click`, `scroll`, `focus` (target via `element`, `hwnd`, or `x`/`y`); `drag` with start (`element` or `x`/`y`) and end (`to_element` or `to_x`/`to_y`); optional `modifiers` (`ctrl`/`shift`/`alt`/`win`) on click/drag. `x`/`y` use the same pixel space as `screenshot` for that scope (window image if `hwnd`/`window_title` is set; desktop image if not).
        -   `set_control_text`: Sets text in a control (target via `element` or `hwnd`)
        -   `send_keys`: Sends keystrokes to the foreground window
        -   `screenshot`: Capture a window or the virtual desktop as JPEG; click with `navigate` using image `x`/`y` and the same scope
    -   Configuration Options
        -   `maxTreeDepth`: Maximum depth when listing control trees (default: `8`)
        -   `maxControls`: Maximum number of controls returned per query (default: `80`)
    -   Context Injector
        -   `get_desktop_context`: Injects a one-line summary of open windows (up to 5 titles, then "and N more")
-   Skill Tools
    -   Tools
        -   `create_skill`: Creates a new agent skill
        -   `edit_skill`: Updates an existing agent skill (description and/or instructions; omitted fields unchanged)
        -   `edit_skill_file`: Creates or overwrites a supporting file under an existing skill (e.g. scripts, references)
        -   `remove_skill`: Deletes an agent skill
        -   `view_skill`: Loads a skill's full `SKILL.md`, absolute path to the skill folder, and relative paths for linked files (optional `relative_path` loads one relative file)
    -   Configuration Options
        -   `skillsDirectory`: Directory to scan recursively for skill folders (folders containing 'SKILL.md') (default: `skills` folder next to `SkillTools.exe`)
    -   Context Injector
        -   `get_skills_context`: Injects the skills directory path, skill names and descriptions, and instructions to load matching skills via `view_skill`, run scripts as shell commands under the skill root, and author skills with `create_skill` / `edit_skill` / `edit_skill_file` / `remove_skill`.
-   Web Tools
    -   Tools
        -   `download_file`: Downloads a file using cURL
        -   `download_video`: Downloads an online video using YT-DLP and saves it to the user's desktop
        -   `read_website`: Reads a webpage as Title/Description, body text (image alt+src kept), and a Links section with a configurable cap
        -   `run_web_search`: Searches the web using SearXNG, with Firecrawl, DDG, and Wiby as fallbacks
    -   Configuration Options
        -   `maxSearchResults`: Maximum number of search results to retrieve (default: `20`)
        -   `maxWebContentLength`: Maximum number of characters to return when reading a webpage (default: `20000`; `0` = no limit)
        -   `maxLinks`: Maximum number of unique links to include at the end of a webpage read (default: `40`)
        -   `FirecrawlEndpoint`: Firecrawl API base URL (e.g. `http://localhost:3002`). If set, `read_website` will attempt to use it to view page content. (default: none)
        -   `FirecrawlApiKey`: Firecrawl API key (optional) (default: none)
        -   `SearXNGInstance`: SearXNG instance to use for running web searches (must support JSON API) (default: none)
        -   `userAgent`: User agent to use when making web requests (default: `Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36`)
    -   Dependencies
        -   `curl-ca-bundle.crt`: used for cURL
        -   `curl.exe`: used to download web content
        -   `yt-dlp.exe`: used to download online videos

**Note**: Dependencies should be placed alongside tool executables. The release builds already include these dependencies, with the exclusion of Python.

Third-party tools can be installed by extracting them into the `tools` folder. For more information on developing/distributing custom tools, see [SimpleLLMChat-Tool-SDK](https://github.com/randomNinja64/SimpleLLMChat-Tool-SDK)

## License

SuperSimpleAgent itself is licensed under the [MIT License](LICENSE).

## Third-party components

The tools shell out to external programs that are **not** covered by the MIT license above. They are redistributed unmodified, each with its license text in [THIRD_PARTY_LICENSES](THIRD_PARTY_LICENSES) (shipped in the release ZIP as well):

| Component | Bundled as | License |
| --- | --- | --- |
| [Markdig](https://github.com/xoofx/markdig) | `Markdig.dll` | BSD-3-Clause |
| [Newtonsoft.Json](https://www.newtonsoft.com/json) | `Newtonsoft.Json.dll` | MIT |
| [cURL](https://curl.se/) (XP builds by LoRd\_MuldeR) | `curl.exe` | curl license |
| [cURL CA bundle](https://curl.se/docs/ca-bundle.html) | `curl-ca-bundle.crt` | MPL-2.0 |
| [7-Zip](https://www.7-zip.org/) | `7za.exe` | 7-Zip license (LGPL-2.1-or-later plus the unRAR restriction) |
| [GNU grep](https://www.gnu.org/software/grep/) 2.5.4, GnuWin32 port | `grep.exe`, `libiconv2.dll`, `libintl3.dll`, `pcre3.dll`, `regex2.dll` | GPL-3.0-or-later |
| [YT-DLP](https://github.com/yt-dlp/yt-dlp) (XP builds by nicolaasjan) | `yt-dlp.exe` | The Unlicense (public domain) |
| OpenSSL, libpsl, libidn2, zlib | linked into the bundled cURL build | Apache-2.0, MIT, LGPL-3.0-or-later, zlib |

Every one of these stays a separate, unmodified executable: the tools start them as child processes and never link against their code, so the MIT terms above cover SuperSimpleAgent only and no GPL obligation extends to it. Keep it that way - do not patch these binaries or build their code into the tools, since that would create a combined work with different licensing consequences. The `grep.txt` notice also carries the GPL-3 Section 6 source offer for GNU grep.

When adding or removing a bundled binary, keep `deps\`, `build.bat` and `THIRD_PARTY_LICENSES` in sync.

## Credits

Thank you to the following for all of their contributions:

-   [randomNinja64](https://github.com/randomNinja64) for the original SimpleLLMChat project, which this repository continues
-   [amzor030285-lab](https://github.com/amzor030285-lab) for the SuperSimpleAgent fork: session management, context compaction, in-app scheduled tasks
-   [michaeldhopkins/safe-chains](https://github.com/michaeldhopkins/safe-chains), [agentkit's shellguard](https://pkg.go.dev/github.com/vinayprograms/agentkit/shellguard) and [All-Hands-AI/OpenHands](https://github.com/All-Hands-AI/OpenHands) for the command-safety rule patterns adapted into the safety guard (ideas and rule structure only - no code or data copied; see `SECURITY.md`)
-   [LoRd\_MuldeR](https://github.com/lordmulder/cURL-build-win32) for the cURL builds for XP
-   [nicolaasjan](https://github.com/nicolaasjan) for the YT-DLP builds for XP
