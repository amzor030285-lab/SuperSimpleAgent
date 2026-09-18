# Task brief: WPF .NET 4.0 chat app — scroll jump on text click + flat markdown lists

You are asked for **design + code-level suggestions** on two problems in a WPF
desktop chat app. Read the code excerpts attached below, then answer the
questions in section 5. Be concrete: exact hooks, exact code, call chains with
source references.

---

## 1. Project context

- WPF app ("SimpleLLMChat GUI"), **.NET Framework 4.0 Client Profile**, no NuGet,
  old-style .csproj. Code is written in **C# 5 style** (no string interpolation,
  no expression-bodied members, no async). Keep all suggestions C#5/.NET4-compatible.
- Chat UI: a `ListBox` subclass (`ChatListBox`) whose item containers
  (`ChatListBoxItem : ListBoxItem`) override `OnMouseLeftButtonDown` to mark the
  event handled **without** calling base — so clicking an item does NOT select
  it (selection was the original source of a big scroll jump). Verified working.
- Each chat turn is a `ChatTurnView` (a `Border`) that hosts a **read-only
  RichTextBox containing a FlowDocument** built from markdown. Virtualization:
  off-screen turns detach their document (height preserved via a placeholder);
  when a detached row's height changes after remeasure, a queued correction
  (`QueueScrollCorrection`, attached DPs on the ScrollViewer) re-anchors the
  visible content by adjusting `ScrollToVerticalOffset`.
- Markdown rendering: `MarkdownHandler.ProcessMarkdown(FlowDocument,
  ref startBlockIndex)` is **incremental**: each pass processes only paragraphs
  from `startBlockIndex` to end, then sets `startBlockIndex = blocks.Count`.
  A "block conversion" replaces a range of paragraphs with ONE hosted block
  (`BlockUIContainer` wrapping a `Grid`/`StackPanel`): pipe tables, lists, and
  fenced code blocks (header row with language label + Copy button over a
  read-only `TextBox` code body).
- **Text selectability** was intentionally added: plain list items, plain table
  cells and code bodies are hosted as **read-only `TextBox`** (so the user can
  select/copy part of the text). Text with inline markdown stays a non-selectable
  `TextBlock` (hosted TextBlocks in WPF are not selectable).
- Copy buttons (code block + per-turn) are `Focusable = false` — clicking them no
  longer scrolls the list. Verified by a UIA test that performs **real mouse
  clicks** (user32 `mouse_event`/`SetCursorPos`) and measures list offset deltas.
- GUI test suite: `run-tests.bat SimpleLLMChatGUI` — 11 tests, all green. Any fix
  must keep them green.

## 2. Problem A — clicking list/table/code text still scrolls the list a little

**Repro.** Long chat (virtualized, many turns). Scroll so a list item's text is
in view. **Click on the text** (a hosted read-only `TextBox` created by
`CreateListTextBox` / `CreateTableCell` / code body). The chat list **jumps a
few dozen pixels** (the clicked control gets brought into view). Same effect for
table cell text and code body text. Clicking a Copy button does NOT jump (fixed
via `Focusable=false`).

**Working hypothesis.** The click moves keyboard focus to the `TextBox`, and WPF
automatically scrolls the newly focused element into view (a focus-driven
`BringIntoView` somewhere in `FocusManager`/`UIElement`/`ScrollViewer`). The exact
call chain in .NET 4 WPF reference source has NOT been pinned down yet.

**Questions.**
1. Pin the exact mechanism in WPF 4.0 source: which call brings the focused
   element into view when focus changes (Tab, click, programmatic `Focus()`)?
   Cite the types/methods.
2. Given the mechanism, what is the cleanest way to **suppress the auto-scroll
   while keeping mouse text selection and Ctrl+C working** in these read-only
   `TextBox`es hosted inside a virtualized list? Evaluate:
   - `SelectableChatTextBox : TextBox` overriding `BringIntoView()` as no-op
     (only works if the focus path actually calls `BringIntoView` on the element);
   - preventing the focus move itself while preserving selection;
   - restoring the previous `ScrollViewer.VerticalOffset` on `GotFocus`
     (race conditions?);
   - anything else you know (`FocusManager` properties, `KeyboardNavigation`,
     `ScrollViewer` hooks, `FocusVisual` state, input manager tricks).
3. Provide the concrete code for the chosen approach.

## 3. Problem B — nested markdown structures render flat

The parser is flat: `TryParseListItem` gets **trimmed** text, so indentation is
ignored; `TryConvertList` collects "consecutive items of one kind";
`BuildListContainer` renders a single-level two-column (marker | text) stack.

Consequences on the sample document in section 4:

1. **Nested bullet list** (2/4-space indents) renders as one **flat** list —
   all 12 items with the same bullet, no indentation.
2. **Numbered sub-items** like `1.1.` / `2.4.` are NOT parsed as list items
   (regex `^(\d{1,9})[.)]\s+` requires a space right after `N.`), so they drop
   out of the list and render as plain paragraphs; each top-level number
   (`1.` … `4.`) becomes its own one-item list.
3. **Nested blockquote** `> > text` strips only one `>`, leaving a literal `>`
   in the text.
4. Fenced code with an inner fence of the same length (```` ```markdown … ```c
   … ``` ````) currently renders correctly (closing fence requires backticks
   only, no info string) — keep it that way.

**Questions.**
1. Design indent-aware list parsing for a **streaming CommonMark subset**:
   - item = (indent, kind unordered/ordered, marker, text);
   - dotted markers like `1.1.` / `2.4.1.` should parse as ordered items with the
     dotted number displayed as the marker;
   - a stack/recursive algorithm mapping the item sequence to a nested tree
     (children attach to the previous shallower item; malformed indents get a
     sane fallback);
   - rendering: how to nest the two-column rows (e.g. per-level `StackPanel`
     with left margin) so wrapped lines hang under the text, keeping existing
     visual style (bullets `•`, numbers as literals).
2. **Streaming reconversion** (the big one). Today, once paragraphs are
   converted into a container, later appended lines are NOT merged into it: a
   list streamed line-by-line ends up as N separate one-item containers; a
   pipe table whose separator and rows arrive in different chunks loses its
   rows. `ProcessMarkdown` never revisits converted blocks. Evaluate and pick:
   - (a) re-parse a tail region of the document on every streaming chunk
     (cost analysis — how big can the tail be, what can be invalidated cheaply);
   - (b) track "pending markdown regions" that may be re-converted when more
     lines arrive;
   - (c) defer all block conversions until the turn completes (plain paragraphs
     while streaming, one visual reflow at the end);
   - (d) hybrid or something better.
   The constraint: streaming delivers many small text chunks on the UI thread;
   the turn can be long (50+ paragraphs); off-screen turns are detached
   (documents are re-parsed from raw source on attach — so a completed turn is
   always fully re-parsed fresh, which is the natural place for a full
   re-conversion).
3. Nested blockquotes: worth doing, or out of scope for the subset? (My lean:
   out of scope — one level of `>` is enough; say if you disagree.)

## 4. Sample document used as the test case

The model output below is the canonical test input. Expected: nested bullet
list with 3 visual levels; numbered list with indented `1.1.`/`1.2.`/`2.x`
sub-items; two correct tables; blockquotes with left bar (nested one is ok to
stay flat); all code blocks in one container each (the ```markdown block
contains an inner ```c fence — it must stay inside); links as blue underlined
hyperlinks.

```markdown
## Примеры рендеринга (дополненные)

### 1. Таблицы

**Простая таблица:**

| ID | Модель | Разработчик | Параметры | Контекст | Цена |
|---|---|---|---|---|---|
| 1 | GPT-6 Astra | OpenAI | не объявлено | — | подписка |
| 2 | Qwen3.8-Max-0902 | Alibaba | 2.4T MoE | 1M токенов | $2/млн |
| 3 | Gemini 3.8 Flash | Google | — | — | бесплатно |
| 4 | Muse Spark 1.3 | Meta | — | — | 62 балла AA |
| 5 | DeepSeek-V4.1-Flash | DeepSeek | — | — | — |

**Сравнение возможностей:**

| Возможность | GPT-6 Astra | Qwen3.8-Max | Claude | Gemini 3.8 |
|---|---|---|---|---|
| FrontierMath T4 | 98% | — | — | — |
| ARC-AGI-3 | 99.9% | — | — | — |
| ExploitBench | 100% | — | — | — |
| Lean-доказательства | — | — | да (Ферма) | — |
| Код-агенты | да | да | да | да |
| Omni (звук/видео) | — | да (Flash) | — | да |

---

### 2. Списки

**Маркированный (вложенный):**

- Фронтенд
  - WPF FlowDocument
    - Table → Grid
    - List → List
    - Blockquote → Border + Margin
    - Link → Hyperlink (синий, подчёркнутый)
  - Code block
    - Заголовок с языком
    - Кнопка Copy
    - Monospace (Consolas)
- Бэкенд
  - Markdown parser
    - CommonMark subset
    - Пайп-таблицы
    - Setext vs ATX заголовки
  - LLM-клиент
    - REST API
    - Streaming (SSE)
    - Ретраи и таймауты

**Нумерованный (вложенный):**

1. Разбор входного текста
   1.1. Убрать BOM и нормализовать CRLF
   1.2. Разбить на блоки (строки → элементы)
2. Определение типа блока
   2.1. `#` → заголовок
   2.2. `|` → таблица
   2.3. `>` → цитата
   2.4. `` ``` `` → код
   2.5. `-` / `*` → маркированный список
   2.6. `1.` → нумерованный список
   2.7. `[текст](url)` → ссылка
3. Сборка FlowDocument
   3.1. Block → Paragraph
   3.2. Inline → Run / Hyperlink
4. Рендер и отладка

---

### 3. Blockquote

> Простая цитата. Это строка с отступом и полосой слева.

> **Жирный заголовок внутри цитаты.**
> Далее обычный текст второй строки.
> Третья строка: `инлайн-код` тоже может быть.

> > Вложенный блокquote — второй уровень отступа.
> > Так можно цитировать цитату.

> Ссылка внутри цитаты: [Anthropic — Fermat proof](https://www.anthropic.com/research/formalizing-fermats-last-theorem) — 11 дней в Lean.

---

### 4. Ссылки

- [OpenAI — GPT-6 Astra announcement](https://openai.com/index/gpt-6-astra/)
- [Anthropic — Fermat formalization](https://www.anthropic.com/research/formalizing-fermats-last-theorem)
- [Qwen — Qwen3.8-Max-0902](https://www.qwencloud.com/models/qwen3.8-max-0902)
- [Google — WeatherNext 3](https://blog.google)
- [Harvard — RLE-Bench](https://seas.harvard.edu/news/can-your-ai-engineer-robot)
- [DeepMind — DeepMind Institute](https://deepmind.google)

**Формат:** `[видимый текст](URL)` → синий цвет, подчёркивание, курсор-рука, при клике — `Process.Start(url)` в WPF.

---

### 5. Code Block

```csharp
// C# — WPF, рендер блока кода
private static Block BuildCodeBlock(string lang, string code)
{
    var border = new Border
    {
        BorderBrush = Brushes.DarkGray,
        BorderThickness = new Thickness(1),
        Background = new SolidColorBrush(Color.FromRgb(0x27, 0x28, 0x22)),
        Padding = new Thickness(8)
    };
    // … (full body in the attached code samples)
}
```

```python
# Python — разбор пайп-таблицы
def parse_table(lines):
    rows = [l.strip().strip('|').split('|') for l in lines]
    header, sep, body = rows[0], rows[1], rows[2:]
    assert all(set(c.strip()) <= set('-: ') for c in sep)
    widths = [max(len(h) for h in col) for col in zip(header, *body)]
    return header, body, widths
```
```

## 5. Deliverables expected from you

1. **Problem A**: the confirmed focus→scroll call chain (with source cites) +
   the concrete suppression code that keeps selection/copy working.
2. **Problem B**:
   - data structures + algorithm (pseudocode acceptable) for indent-aware
     nested list parsing/rendering, including dotted markers;
   - the chosen streaming reconversion strategy with a complexity/risk
     analysis and the exact integration points in `ProcessMarkdown` /
     `ChatTurn`;
   - a one-paragraph verdict on nested blockquotes (in or out of scope).
3. Call out any .NET 4.0 / C# 5 / old-XAML pitfalls your solution depends on.
   Known quirk (already worked around): the legacy markup compiler will NOT
   resolve a new same-assembly local type for a **named** XAML element — the
   field silently goes missing. New local controls in XAML must stay unnamed
   and be found in code-behind via a visual-tree walk.

## 6. Code excerpts to attach to this brief

| File | What to include |
|---|---|
| `SimpleLLMChatGUI/MarkdownHandler.cs` | patterns (lines 19–47), `ProcessMarkdown` (58–183), `TryConvertPipeTable` (286–314), `TryConvertList` (322–348), `BuildListContainer` (476–521), `BuildCodeBlockContainer` (528–604), `CreateListTextBox` (624–642), `HasInlineMarkdown` (649–652), `TryParseListItem` (659–686), `TryStripBlockquoteMarker` (691–700), `ParagraphText` (784–789) |
| `SimpleLLMChatGUI/ChatTurnView.cs` | whole file (~300 lines): attach/detach virtualization, `QueueScrollCorrection` |
| `SimpleLLMChatGUI/ChatListBox.cs` | whole file (35 lines) |
| `SimpleLLMChatGUI/ChatTurn.cs` | `Document` getter (96–144), `Complete` (146–153), `ProcessMarkdown` (155–160), streaming append path |
| `SimpleLLMChatGUI/MainWindow.xaml.cs` | `ScrollChatToEnd`, turn size/margin logic (optional) |
| `SimpleLLMChatGUI/SimpleLLMChatGUI.csproj` | first ~40 lines (target framework, no packages) |
