using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SimpleLLMChatGUI
{
    public class MarkdownHandler
    {
        private static readonly Regex HeaderPattern = new Regex(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);
        // Bold italic: requires word boundaries (not adjacent to word characters including underscore)
        private static readonly Regex BoldItalicPattern = new Regex(@"(?<![a-zA-Z0-9_])(\*\*\*|___)(.+?)\1(?![a-zA-Z0-9_])", RegexOptions.Compiled);
        // Bold: **text** or __text__, not adjacent to word characters or the same marker character
        private static readonly Regex BoldPattern = new Regex(@"(?<![a-zA-Z0-9_])(?<!\*)\*\*(.+?)\*\*(?![a-zA-Z0-9_])(?!\*)|(?<![a-zA-Z0-9_])(?<!_)__(.+?)__(?![a-zA-Z0-9_])(?!_)", RegexOptions.Compiled);
        // Italic: *text* or _text_, not adjacent to word characters or the same marker character
        private static readonly Regex ItalicPattern = new Regex(@"(?<![a-zA-Z0-9_])(?<!\*)\*(.+?)\*(?![a-zA-Z0-9_])(?!\*)|(?<![a-zA-Z0-9_])(?<!_)_(.+?)_(?![a-zA-Z0-9_])(?!_)", RegexOptions.Compiled);
        private static readonly Regex StrikethroughPattern = new Regex(@"~~(.+?)~~", RegexOptions.Compiled);
        private static readonly Regex InlineCodePattern = new Regex(@"`([^`]+)`", RegexOptions.Compiled);
        // Markers that ApplyInlineFormatting would render (used to decide whether
        // hosted text must stay in a formatted TextBlock instead of a selectable
        // TextBox).
        private static readonly Regex InlineMarkdownPattern = new Regex(@"(\*\*|__|~~|`|\[[^\]]+\]\()", RegexOptions.Compiled);
        // Bold italic link: ***[text](url)*** or ___[text](url)___
        private static readonly Regex BoldItalicLinkPattern = new Regex(@"(?<![a-zA-Z0-9_])(\*\*\*|___)\[([^\[\]]+)\]\(([^()]+)\)\1(?![a-zA-Z0-9_])", RegexOptions.Compiled);
        // Bold link: **[text](url)** or __[text](url)__
        private static readonly Regex BoldLinkPattern = new Regex(@"(?<![a-zA-Z0-9_])(?<!\*)\*\*\[([^\[\]]+)\]\(([^()]+)\)\*\*(?![a-zA-Z0-9_])(?!\*)|(?<![a-zA-Z0-9_])(?<!_)__\[([^\[\]]+)\]\(([^()]+)\)__(?![a-zA-Z0-9_])(?!_)", RegexOptions.Compiled);
        private static readonly Regex LinkPattern = new Regex(@"\[([^\[\]]+)\]\(([^()]+)\)", RegexOptions.Compiled);
        private static readonly Regex BareUrlPattern = new Regex(@"https?://[^\s<>""'()\[\]{}]+(?<![.,;:!?])", RegexOptions.Compiled);
        private static readonly Regex HorizontalRulePattern = new Regex(@"^(?:(?:-\s*){3,}|(?:\*\s*){3,}|(?:_\s*){3,})$", RegexOptions.Compiled);
        private static readonly Regex BacktickFencePattern = new Regex(@"^([^`]*:\s*)?(`{3,})([^`]*)$", RegexOptions.Compiled);
        // One markdown table separator cell: dashes with optional alignment colons (---, :---, ---:, :---:).
        private static readonly Regex TableSeparatorCellPattern = new Regex(@"^:?-+:?$", RegexOptions.Compiled);
        // Unordered list item marker: "-", "*", "+".
        private static readonly Regex UnorderedListItemPattern = new Regex(@"^([-*+])\s+(\S.*)$", RegexOptions.Compiled);
        // Ordered list item marker: "1.", "2)", or dotted numbers like "1.1." / "2.4.1."
        // (dotted markers are displayed literally).
        private static readonly Regex OrderedListItemPattern = new Regex(@"^(\d{1,9}(?:\.\d{1,9})*[.)])\s+(\S.*)$", RegexOptions.Compiled);
        // Blockquote line: one or more leading ">" markers followed by the quoted text
        // (nested markers collapse into a single-level quote).
        private static readonly Regex BlockquotePattern = new Regex(@"^(?:>\s?)+(.+)$", RegexOptions.Compiled);

        /// <summary>
        /// H1–H6 size relative to the document base font size.
        /// </summary>
        private static readonly double[] HeaderMultipliers = { 1.3, 1.2, 1.1, 1.0, 1.0, 1.0 };

        /// <summary>
        /// Renders markdown in blocks at or after <paramref name="startBlockIndex"/>, then
        /// advances that index to the document's block count so later passes skip already-handled content.
        /// </summary>
        public static void ProcessMarkdown(FlowDocument document, ref int startBlockIndex)
        {
            if (document == null)
                return;

            List<Block> blocks = document.Blocks.ToList();
            if (startBlockIndex < 0)
                startBlockIndex = 0;
            if (startBlockIndex > blocks.Count)
                startBlockIndex = blocks.Count;

            int activeBacktickFenceLength = 0;
            int activeCodeBlockStart = -1;
            string activeCodeLanguage = null;

            for (int i = startBlockIndex; i < blocks.Count; i++)
            {
                Paragraph paragraph = blocks[i] as Paragraph;
                if (paragraph == null)
                    continue;

                string paragraphText = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
                string trimmedText = paragraphText.Trim();

                if (TryParseBacktickFence(trimmedText, out int fenceLength, out bool hasInfoString, out string infoString))
                {
                    if (activeBacktickFenceLength == 0)
                    {
                        // Opening fence can include an optional language/info string.
                        activeBacktickFenceLength = fenceLength;
                        activeCodeBlockStart = i;
                        activeCodeLanguage = infoString;
                        int backtickIdx = paragraphText.IndexOf('`');
                        string prefix = backtickIdx > 0 ? paragraphText.Substring(0, backtickIdx).TrimEnd() : null;
                        paragraph.Inlines.Clear();
                        if (!string.IsNullOrEmpty(prefix))
                            paragraph.Inlines.Add(new Run(prefix));
                        continue;
                    }

                    // Closing fence must be backticks only and at least the opening fence length.
                    if (!hasInfoString && fenceLength >= activeBacktickFenceLength)
                    {
                        int start = activeCodeBlockStart;
                        string language = activeCodeLanguage;
                        activeBacktickFenceLength = 0;
                        activeCodeBlockStart = -1;
                        activeCodeLanguage = null;

                        // Fall back to plain fence removal if a non-paragraph block
                        // (e.g. a thinking/tool expander) ended up inside the fence.
                        bool plain = start >= 0;
                        for (int k = start + 1; plain && k < i; k++)
                            plain = blocks[k] is Paragraph;

                        if (!plain)
                        {
                            paragraph.Inlines.Clear();
                            continue;
                        }

                        // Collect the code lines between the fences, keeping indentation.
                        var codeLines = new List<string>();
                        for (int k = start + 1; k < i; k++)
                        {
                            Paragraph codeParagraph = blocks[k] as Paragraph;
                            string line = new TextRange(codeParagraph.ContentStart, codeParagraph.ContentEnd).Text;
                            codeLines.Add(line.TrimEnd('\r', '\n'));
                        }

                        // Keep a non-empty prefix paragraph (text before the opening fence).
                        int rangeStart = !string.IsNullOrEmpty(ParagraphText(blocks[start] as Paragraph)) ? start + 1 : start;

                        double baseFontSize = document.FontSize > 0 ? document.FontSize : FontHandler.GetFontSize();
                        Block container = BuildCodeBlockContainer(language, codeLines, baseFontSize);
                        blocks = ReplaceBlockRange(document, blocks, rangeStart, i, container);
                        i = rangeStart;
                        continue;
                    }
                }

                // Skip processing inside fenced code blocks (thinking/tool bodies are
                // BlockUIContainer expanders, not paragraphs, so they are skipped above).
                if (activeBacktickFenceLength > 0)
                {
                    paragraph.SetResourceReference(TextElement.BackgroundProperty, "CodeBlockBackgroundColorBrush");
                    ApplyCodeBlockFont(paragraph);
                    continue;
                }

                // Markdown pipe tables: a row paragraph followed by a separator
                // paragraph (plus any data rows) becomes a WPF Table block.
                if (TryConvertPipeTable(document, i, ref blocks))
                    continue;

                // Process horizontal rules as paragraph-level separators
                if (ProcessHorizontalRule(paragraph, trimmedText))
                    continue;

                // Markdown lists: consecutive list-item paragraphs of one kind become
                // a bullet/numbered block.
                if (TryConvertList(document, i, ref blocks))
                    continue;

                // Markdown blockquote: a leading "> " marker becomes a left bar with padding.
                string quoteText;
                if (TryStripBlockquoteMarker(trimmedText, out quoteText))
                {
                    paragraph.Inlines.Clear();
                    paragraph.Inlines.Add(new Run(quoteText));
                    paragraph.Padding = new Thickness(10, 0, 0, 0);
                    paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
                    paragraph.SetResourceReference(Border.BorderBrushProperty, "RoleLabelBrush");
                }

                // Streaming AppendText leaves many Runs per line; merge before regex matching.
                ConsolidateRuns(paragraph);
                trimmedText = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Trim();

                // Process headers first (paragraph-level formatting)
                ProcessHeaders(paragraph, trimmedText, document);

                ApplyInlineFormatting(paragraph.Inlines);
            }

            startBlockIndex = blocks.Count;
        }

        /// <summary>
        /// Streaming <see cref="TextRange"/> insertion freezes the chat font onto
        /// each <see cref="Run"/> as a local value; clear those so inlines inherit
        /// the paragraph's <see cref="FontHandler.CodeBlockFontFamilyKey"/> binding.
        /// </summary>
        private static void ApplyCodeBlockFont(Paragraph paragraph)
        {
            if (paragraph == null)
                return;

            paragraph.SetResourceReference(TextElement.FontFamilyProperty, FontHandler.CodeBlockFontFamilyKey);
            foreach (Inline inline in paragraph.Inlines)
                inline.ClearValue(TextElement.FontFamilyProperty);
        }

        private static void ConsolidateRuns(Paragraph paragraph)
        {
            List<Run> runs = paragraph.Inlines.OfType<Run>().ToList();
            if (runs.Count <= 1 || runs.Count != paragraph.Inlines.Count)
                return;

            var sb = new System.Text.StringBuilder();
            foreach (Run run in runs)
                sb.Append(run.Text);

            paragraph.Inlines.Clear();
            paragraph.Inlines.Add(new Run(sb.ToString()));
        }

        private static void ProcessHeaders(Paragraph paragraph, string trimmedText, FlowDocument document)
        {
            Match headerMatch = HeaderPattern.Match(trimmedText);
            if (!headerMatch.Success)
                return;

            int headerLevel = headerMatch.Groups[1].Value.Length; // Number of # characters
            string headerText = headerMatch.Groups[2].Value.Trim();

            // Tag so ApplyHeaderFontSizes can rescale when the chat font size changes.
            paragraph.Tag = headerLevel;
            double baseFontSize = document != null && document.FontSize > 0
                ? document.FontSize
                : FontHandler.GetFontSize();
            paragraph.FontSize = baseFontSize * HeaderMultipliers[Math.Min(headerLevel, 6) - 1];
            paragraph.FontWeight = FontWeights.Bold;

            // Replace the paragraph content with just the header text (without the # markers)
            // This allows inline formatting to be processed on the header text afterwards
            paragraph.Inlines.Clear();
            paragraph.Inlines.Add(new Run(headerText));
        }

        /// <summary>
        /// Inline markdown for a paragraph's or text block's inlines: code, links,
        /// bold/italic/strikethrough.
        /// </summary>
        private static void ApplyInlineFormatting(InlineCollection inlines)
        {
            // Process inline code blocks first to split them out
            ReplaceInRuns(inlines, InlineCodePattern, CreateInlineCodeSpan);

            // Emphasized links before plain links (e.g. **[text](url)** / ***[text](url)***).
            ReplaceInRuns(inlines, BoldItalicLinkPattern, match =>
                (Inline)CreateHyperlink(match.Groups[2].Value, match.Groups[3].Value.Trim(), FontWeights.Bold, FontStyles.Italic)
                ?? new Run(match.Value));
            ReplaceInRuns(inlines, BoldLinkPattern, match =>
                (Inline)CreateHyperlink(BoldLinkText(match), BoldLinkUrl(match), FontWeights.Bold)
                ?? new Run(match.Value));
            // Plain links; unwrap [**text**](url) / [***text***](url) into emphasized hyperlinks.
            ReplaceInRuns(inlines, LinkPattern, match =>
                (Inline)CreateLinkInline(match.Groups[1].Value, match.Groups[2].Value.Trim()) ?? new Run(match.Value));
            ReplaceInRuns(inlines, BareUrlPattern, match =>
                (Inline)CreateHyperlink(match.Value, match.Value) ?? new Run(match.Value));

            // Process formatting in order: bold italic, bold, italic, strikethrough
            ReplaceInRuns(inlines, BoldItalicPattern, match => new Run(match.Groups[2].Value) { FontWeight = FontWeights.Bold, FontStyle = FontStyles.Italic });
            ReplaceInRuns(inlines, BoldPattern, match => new Run(FirstCapturedGroup(match)) { FontWeight = FontWeights.Bold });
            ReplaceInRuns(inlines, ItalicPattern, match => new Run(FirstCapturedGroup(match)) { FontStyle = FontStyles.Italic });
            ReplaceInRuns(inlines, StrikethroughPattern, match => new Run(match.Groups[1].Value) { TextDecorations = TextDecorations.Strikethrough });
        }

        private static Inline CreateInlineCodeSpan(Match match)
        {
            var codeRun = new Run(match.Groups[1].Value);
            var codeSpan = new Span(codeRun);
            codeSpan.SetResourceReference(TextElement.BackgroundProperty, "CodeBlockBackgroundColorBrush");
            codeSpan.SetResourceReference(TextElement.FontFamilyProperty, FontHandler.CodeBlockFontFamilyKey);
            codeRun.ClearValue(TextElement.FontFamilyProperty);
            return codeSpan;
        }

        /// <summary>
        /// If block <paramref name="i"/> begins a markdown pipe table (header row,
        /// separator row, then data rows), replaces those paragraphs with a table
        /// block and refreshes <paramref name="blocks"/>. The caller's
        /// <c>continue</c> then resumes at the first block after the table. The
        /// table is a <see cref="System.Windows.Controls.Grid"/> of bordered cells
        /// wrapped in a <see cref="BlockUIContainer"/> because the .NET 4.0 Client
        /// Profile does not expose the WPF <c>System.Windows.Documents</c> table
        /// types (<c>Grid</c>, <c>Row</c>, <c>Cell</c>).
        /// </summary>
        private static bool TryConvertPipeTable(FlowDocument document, int i, ref List<Block> blocks)
        {
            Paragraph headerParagraph = blocks[i] as Paragraph;
            if (headerParagraph == null || i + 1 >= blocks.Count)
                return false;

            string headerText = ParagraphText(headerParagraph);
            List<string> headerCells;
            if (!TryParseTableRow(headerText, out headerCells))
                return false;

            Paragraph separatorParagraph = blocks[i + 1] as Paragraph;
            if (separatorParagraph == null)
                return false;

            List<TextAlignment> alignments;
            if (!TryParseTableSeparator(ParagraphText(separatorParagraph), out alignments))
                return false;

            int lastRow = i + 1;
            while (lastRow + 1 < blocks.Count && IsTableDataRow(ParagraphText(blocks[lastRow + 1] as Paragraph)))
                lastRow++;

            double baseFontSize = document.FontSize > 0 ? document.FontSize : FontHandler.GetFontSize();
            Block container = BuildTableContainer(headerCells, alignments, blocks, i + 2, lastRow, baseFontSize);

            blocks = ReplaceBlockRange(document, blocks, i, lastRow, container);
            return true;
        }

        /// <summary>
        /// If block <paramref name="i"/> begins a markdown list (consecutive items,
        /// nesting via indentation), replaces those paragraphs with a bullet/numbered
        /// block wrapped in a <see cref="BlockUIContainer"/>. The caller's
        /// <c>continue</c> then resumes at the first block after the list.
        /// </summary>
        private static bool TryConvertList(FlowDocument document, int i, ref List<Block> blocks)
        {
            int baseIndent;
            bool baseOrdered;
            string marker;
            string itemText;
            if (!TryParseListItem(RawParagraphText(blocks[i] as Paragraph), out baseIndent, out baseOrdered, out marker, out itemText))
                return false;

            var items = new List<ListItemNode>();
            items.Add(new ListItemNode { Indent = baseIndent, Ordered = baseOrdered, Marker = marker, Text = itemText });

            int lastItem = i;
            while (lastItem + 1 < blocks.Count)
            {
                int nextIndent;
                bool nextOrdered;
                string nextMarker;
                string nextText;
                if (!TryParseListItem(RawParagraphText(blocks[lastItem + 1] as Paragraph), out nextIndent, out nextOrdered, out nextMarker, out nextText))
                    break;
                // A same-or-shallower item of a different kind starts a new list.
                if (nextIndent <= baseIndent && nextOrdered != baseOrdered)
                    break;
                lastItem++;
                items.Add(new ListItemNode { Indent = nextIndent, Ordered = nextOrdered, Marker = nextMarker, Text = nextText });
            }

            double baseFontSize = document.FontSize > 0 ? document.FontSize : FontHandler.GetFontSize();
            Block container = BuildListContainer(BuildListTree(items), baseFontSize);

            blocks = ReplaceBlockRange(document, blocks, i, lastItem, container);
            return true;
        }

        /// <summary>
        /// Builds a table as an auto-sized <see cref="System.Windows.Controls.Grid"/>
        /// of bordered cell <see cref="Border"/>s, wrapped in a
        /// <see cref="BlockUIContainer"/>. Columns size to their content (capped so
        /// long text wraps instead of widening the table). The outer frame supplies
        /// the outer border; each cell supplies only its right/bottom border so
        /// inner lines are 1px.
        /// </summary>
        private static Block BuildTableContainer(List<string> headerCells, List<TextAlignment> alignments,
            List<Block> blocks, int firstDataRow, int lastRow, double baseFontSize)
        {
            int columnCount = headerCells.Count;
            int rowCount = (lastRow - firstDataRow + 1) + 1;

            var grid = new System.Windows.Controls.Grid();
            for (int c = 0; c < columnCount; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int r = 0; r < rowCount; r++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int c = 0; c < columnCount; c++)
                grid.Children.Add(CreateTableCell(headerCells[c], CellAlignment(alignments, c), true,
                    baseFontSize, c, columnCount, 0, rowCount));

            for (int i = firstDataRow; i <= lastRow; i++)
            {
                int r = i - firstDataRow + 1;
                List<string> cells = SplitTableCells(StripOuterPipes(ParagraphText(blocks[i] as Paragraph) ?? ""));
                for (int c = 0; c < columnCount; c++)
                    grid.Children.Add(CreateTableCell(c < cells.Count ? cells[c] : string.Empty, CellAlignment(alignments, c), false,
                        baseFontSize, c, columnCount, r, rowCount));
            }

            var frame = new Border
            {
                BorderThickness = new Thickness(1),
                Child = grid
            };
            frame.SetResourceReference(Border.BorderBrushProperty, "TableBorderBrush");

            return new BlockUIContainer(frame)
            {
                Margin = new Thickness(0, 4, 0, 4)
            };
        }

        private static Border CreateTableCell(string text, TextAlignment alignment, bool isHeader,
            double baseFontSize, int column, int columnCount, int row, int rowCount)
        {
            var cellBorder = new Border
            {
                Padding = new Thickness(6, 3, 6, 3),
                BorderThickness = new Thickness(0, 0, column == columnCount - 1 ? 0 : 1, row == rowCount - 1 ? 0 : 1)
            };
            cellBorder.SetResourceReference(Border.BorderBrushProperty, "TableBorderBrush");
            if (isHeader)
                cellBorder.SetResourceReference(Border.BackgroundProperty, "TableHeaderBackgroundBrush");

            System.Windows.Controls.Grid.SetColumn(cellBorder, column);
            System.Windows.Controls.Grid.SetRow(cellBorder, row);

            // Plain cells become a read-only TextBox so part of the cell can be
            // selected and copied; cells with inline markdown stay formatted
            // (hosted TextBlocks are not selectable in WPF).
            FrameworkElement content;
            if (HasInlineMarkdown(text))
            {
                var textBlock = new TextBlock
                {
                    TextAlignment = alignment,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = baseFontSize,
                    LineHeight = ChatTurn.LineHeightMultiplier,
                    MaxWidth = TableCellMaxWidth
                };
                textBlock.SetResourceReference(TextElement.ForegroundProperty, "ChatTextColorBrush");
                if (isHeader)
                    textBlock.FontWeight = FontWeights.Bold;

                textBlock.Inlines.Add(new Run(text));
                ApplyInlineFormatting(textBlock.Inlines);
                content = textBlock;
            }
            else
            {
                var textBox = new NoScrollTextBox
                {
                    Text = text,
                    IsReadOnly = true,
                    IsReadOnlyCaretVisible = true,
                    IsTabStop = false,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    FontSize = baseFontSize,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = alignment,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    MaxWidth = TableCellMaxWidth
                };
                textBox.SetResourceReference(TextElement.ForegroundProperty, "ChatTextColorBrush");
                if (isHeader)
                    textBox.FontWeight = FontWeights.Bold;
                content = textBox;
            }

            cellBorder.Child = content;
            return cellBorder;
        }

        private static TextAlignment CellAlignment(List<TextAlignment> alignments, int column)
        {
            return column < alignments.Count ? alignments[column] : TextAlignment.Left;
        }

        /// <summary>
        /// Maximum pixel width of a table column; longer text wraps instead of
        /// stretching the table.
        /// </summary>
        private const double TableCellMaxWidth = 300;

        /// <summary>
        /// One parsed list item; <see cref="Children"/> holds the items indented
        /// under it, <see cref="Parent"/> the nearest shallower item.
        /// </summary>
        private sealed class ListItemNode
        {
            public int Indent;
            public bool Ordered;
            public string Marker;
            public string Text;
            public ListItemNode Parent;
            public readonly List<ListItemNode> Children = new List<ListItemNode>();
        }

        /// <summary>
        /// Maps a flat sequence of indented items to a nested tree: each item
        /// attaches to the nearest preceding item with a smaller indent (indent
        /// stack), which also absorbs malformed indentation sanely.
        /// </summary>
        private static List<ListItemNode> BuildListTree(List<ListItemNode> items)
        {
            var root = new ListItemNode { Indent = -1 };
            var stack = new List<ListItemNode>();

            for (int i = 0; i < items.Count; i++)
            {
                ListItemNode item = items[i];

                while (stack.Count > 0 && stack[stack.Count - 1].Indent > item.Indent)
                    stack.RemoveAt(stack.Count - 1);

                ListItemNode parent;
                if (stack.Count == 0)
                {
                    parent = root;
                }
                else if (stack[stack.Count - 1].Indent == item.Indent)
                {
                    parent = stack[stack.Count - 1].Parent ?? root;
                }
                else
                {
                    parent = stack[stack.Count - 1];
                }

                item.Parent = parent;
                parent.Children.Add(item);
                stack.Add(item);
            }

            return root.Children;
        }

        /// <summary>
        /// Builds a (nested) list as a vertical stack of two-column rows (marker,
        /// then wrapping text), wrapped in a <see cref="BlockUIContainer"/> so
        /// wrapped lines hang under the text instead of the marker. Nested levels
        /// are indented inside the previous level's text column.
        /// </summary>
        private static Block BuildListContainer(List<ListItemNode> topLevel, double baseFontSize)
        {
            return new BlockUIContainer(BuildListLevel(topLevel, baseFontSize))
            {
                Margin = new Thickness(0, 4, 0, 4)
            };
        }

        private static StackPanel BuildListLevel(List<ListItemNode> items, double baseFontSize)
        {
            var stack = new StackPanel { Orientation = Orientation.Vertical };

            for (int i = 0; i < items.Count; i++)
            {
                ListItemNode item = items[i];

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var markerBlock = CreateListTextBlock(baseFontSize, new Thickness(0, 1, 8, 1));
                markerBlock.Inlines.Add(new Run(item.Marker));
                Grid.SetColumn(markerBlock, 0);
                row.Children.Add(markerBlock);

                var content = new StackPanel { Orientation = Orientation.Vertical };

                // Plain items become a read-only TextBox so part of the item can be
                // selected and copied; items with inline markdown stay formatted
                // (hosted TextBlocks are not selectable in WPF).
                if (HasInlineMarkdown(item.Text))
                {
                    var textBlock = CreateListTextBlock(baseFontSize, new Thickness(0, 1, 0, 1));
                    textBlock.Inlines.Add(new Run(item.Text));
                    ApplyInlineFormatting(textBlock.Inlines);
                    content.Children.Add(textBlock);
                }
                else
                {
                    content.Children.Add(CreateListTextBox(item.Text, baseFontSize, new Thickness(0, 1, 0, 1)));
                }

                if (item.Children.Count > 0)
                {
                    var childPanel = BuildListLevel(item.Children, baseFontSize);
                    childPanel.Margin = new Thickness(Math.Max(12.0, baseFontSize), 2, 0, 2);
                    content.Children.Add(childPanel);
                }

                Grid.SetColumn(content, 1);
                row.Children.Add(content);

                stack.Children.Add(row);
            }

            return stack;
        }

        /// <summary>
        /// Builds a fenced code block: a header row (language label + Copy button)
        /// over a monospace code area, wrapped in a
        /// <see cref="BlockUIContainer"/>.
        /// </summary>
        private static Block BuildCodeBlockContainer(string language, List<string> codeLines, double baseFontSize)
        {
            string codeText = string.Join("\n", codeLines);

            var root = new StackPanel { Orientation = Orientation.Vertical };

            var label = new TextBlock
            {
                Text = string.IsNullOrEmpty(language) ? "code" : language,
                FontSize = baseFontSize,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.SetResourceReference(TextElement.ForegroundProperty, "ChatTextColorBrush");
            var labelArea = new Border
            {
                Padding = new Thickness(8, 3, 8, 3),
                Child = label,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            labelArea.SetResourceReference(Border.BackgroundProperty, "CodeBlockHeaderBrush");

            var copyButton = new Button
            {
                Content = "Copy",
                MinWidth = 55,
                MinHeight = 22,
                Margin = new Thickness(6, 2, 2, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false
            };
            AutomationProperties.SetAutomationId(copyButton, "codeCopyButton");
            var copyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            copyTimer.Tick += (s, e) =>
            {
                copyButton.Content = "Copy";
                copyTimer.Stop();
            };
            copyButton.Click += (s, e) =>
            {
                try { Clipboard.SetText(codeText); } catch { }
                copyButton.Content = "Copied";
                copyTimer.Stop();
                copyTimer.Start();
            };

            var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
            headerRow.Children.Add(labelArea);
            headerRow.Children.Add(copyButton);

            // A read-only TextBox (not a TextBlock) so the user can select part of
            // the code and copy the selection.
            var code = new NoScrollTextBox
            {
                Text = codeText,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                IsTabStop = false,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                FontSize = baseFontSize,
                TextWrapping = TextWrapping.Wrap
            };
            code.SetResourceReference(TextElement.FontFamilyProperty, FontHandler.CodeBlockFontFamilyKey);
            code.SetResourceReference(TextElement.ForegroundProperty, "ChatTextColorBrush");
            var codeArea = new Border { Padding = new Thickness(8, 6, 8, 6), Child = code };
            codeArea.SetResourceReference(Border.BackgroundProperty, "CodeBlockBackgroundColorBrush");

            root.Children.Add(headerRow);
            root.Children.Add(codeArea);

            return new BlockUIContainer(root)
            {
                Margin = new Thickness(0, 4, 0, 4)
            };
        }

        private static TextBlock CreateListTextBlock(double baseFontSize, Thickness margin)
        {
            var textBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = baseFontSize,
                LineHeight = ChatTurn.LineHeightMultiplier,
                Margin = margin
            };
            textBlock.SetResourceReference(TextElement.ForegroundProperty, "ChatTextColorBrush");
            return textBlock;
        }

        /// <summary>
        /// Plain list item text as a read-only selectable
        /// <see cref="TextBox"/> (same metrics as
        /// <see cref="CreateListTextBlock"/>).
        /// </summary>
        private static TextBox CreateListTextBox(string text, double baseFontSize, Thickness margin)
        {
            var textBox = new NoScrollTextBox
            {
                Text = text,
                Margin = margin,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                IsTabStop = false,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                FontSize = baseFontSize,
                TextWrapping = TextWrapping.Wrap,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            textBox.SetResourceReference(TextElement.ForegroundProperty, "ChatTextColorBrush");
            return textBox;
        }

        /// <summary>
        /// True when the text contains inline markdown markers that
        /// <see cref="ApplyInlineFormatting"/> would render, so the text must stay
        /// in a formatted (non-selectable) TextBlock.
        /// </summary>
        private static bool HasInlineMarkdown(string text)
        {
            return text != null && InlineMarkdownPattern.IsMatch(text);
        }

        /// <summary>
        /// A list item with its indentation: <c>-</c>/<c>*</c>/<c>+</c> text
        /// (unordered) or <c>1.</c>/<c>1.1.</c>/<c>1)</c> text (ordered).
        /// <paramref name="indent"/> is the leading whitespace (tabs count as
        /// 4 spaces); <paramref name="marker"/> is the displayed bullet or the
        /// literal item number.
        /// </summary>
        private static bool TryParseListItem(string rawText, out int indent, out bool ordered, out string marker, out string text)
        {
            indent = 0;
            ordered = false;
            marker = null;
            text = null;
            if (string.IsNullOrEmpty(rawText))
                return false;

            int pos = 0;
            while (pos < rawText.Length && (rawText[pos] == ' ' || rawText[pos] == '\t'))
            {
                indent += rawText[pos] == '\t' ? 4 : 1;
                pos++;
            }

            string rest = rawText.Substring(pos).TrimEnd();
            if (rest.Length == 0)
                return false;

            Match orderedMatch = OrderedListItemPattern.Match(rest);
            if (orderedMatch.Success)
            {
                ordered = true;
                marker = orderedMatch.Groups[1].Value;
                if (marker.EndsWith(")", StringComparison.Ordinal))
                    marker = marker.Substring(0, marker.Length - 1) + ".";
                text = orderedMatch.Groups[2].Value;
                return true;
            }

            Match unorderedMatch = UnorderedListItemPattern.Match(rest);
            if (unorderedMatch.Success)
            {
                ordered = false;
                marker = "\u2022";
                text = unorderedMatch.Groups[2].Value;
                return true;
            }

            return false;
        }

        /// <summary>
        /// A blockquote line: a leading <c>></c> marker followed by the quoted text.
        /// </summary>
        private static bool TryStripBlockquoteMarker(string trimmedText, out string text)
        {
            text = null;
            Match match = BlockquotePattern.Match(trimmedText);
            if (!match.Success)
                return false;

            text = match.Groups[1].Value;
            return true;
        }

        /// <summary>
        /// Removes blocks first..last, appends <paramref name="block"/>, then
        /// re-appends the trailing blocks (the Client Profile
        /// <see cref="BlockCollection"/> exposes no InsertAt). Returns the
        /// refreshed block list.
        /// </summary>
        private static List<Block> ReplaceBlockRange(FlowDocument document, List<Block> blocks, int first, int last, Block block)
        {
            List<Block> tail = new List<Block>();
            for (int j = last + 1; j < blocks.Count; j++)
                tail.Add(blocks[j]);

            for (int j = first; j < blocks.Count; j++)
                document.Blocks.Remove(blocks[j]);

            document.Blocks.Add(block);
            foreach (Block tailBlock in tail)
                document.Blocks.Add(tailBlock);

            return document.Blocks.ToList();
        }

        /// <summary>
        /// A table row: at least two pipe-separated cells, at least one non-empty.
        /// </summary>
        private static bool TryParseTableRow(string trimmedText, out List<string> cells)
        {
            cells = null;
            if (string.IsNullOrEmpty(trimmedText) || trimmedText.IndexOf('|') < 0)
                return false;

            cells = SplitTableCells(StripOuterPipes(trimmedText));
            if (cells.Count < 2)
                return false;
            if (!cells.Any(cell => !string.IsNullOrWhiteSpace(cell)))
                return false;
            return true;
        }

        /// <summary>
        /// A separator row: every cell is dashes with optional alignment colons
        /// (<c>:---</c> left, <c>---:</c> right, <c>:---:</c> center).
        /// </summary>
        private static bool TryParseTableSeparator(string trimmedText, out List<TextAlignment> alignments)
        {
            alignments = null;
            if (string.IsNullOrEmpty(trimmedText) || trimmedText.IndexOf('|') < 0)
                return false;

            List<string> cells = SplitTableCells(StripOuterPipes(trimmedText));
            if (cells.Count == 0)
                return false;

            alignments = new List<TextAlignment>(cells.Count);
            foreach (string cell in cells)
            {
                if (!TableSeparatorCellPattern.IsMatch(cell))
                    return false;

                bool leftColon = cell[0] == ':';
                bool rightColon = cell.Length > 1 && cell[cell.Length - 1] == ':';
                alignments.Add(leftColon && rightColon ? TextAlignment.Center
                    : rightColon ? TextAlignment.Right
                    : TextAlignment.Left);
            }
            return true;
        }

        /// <summary>
        /// A line that continues an open table: contains a pipe and is not a code fence.
        /// </summary>
        private static bool IsTableDataRow(string trimmedText)
        {
            if (string.IsNullOrEmpty(trimmedText) || trimmedText.IndexOf('|') < 0)
                return false;

            int fenceLength;
            bool hasInfoString;
            string infoString;
            return !TryParseBacktickFence(trimmedText, out fenceLength, out hasInfoString, out infoString);
        }

        private static string ParagraphText(Paragraph paragraph)
        {
            if (paragraph == null)
                return null;
            return new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Trim();
        }

        /// <summary>
        /// Paragraph text with leading whitespace preserved (list indentation).
        /// </summary>
        private static string RawParagraphText(Paragraph paragraph)
        {
            if (paragraph == null)
                return null;
            return new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
        }

        private static string StripOuterPipes(string text)
        {
            if (text.Length > 0 && text[0] == '|')
                text = text.Substring(1);
            if (text.Length > 0 && text[text.Length - 1] == '|')
                text = text.Substring(0, text.Length - 1);
            return text;
        }

        /// <summary>
        /// Splits a table row body on unescaped pipes; <c>\|</c> stays in the cell.
        /// </summary>
        private static List<string> SplitTableCells(string text)
        {
            var cells = new List<string>();
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < text.Length; k++)
            {
                char c = text[k];
                if (c == '\\' && k + 1 < text.Length && text[k + 1] == '|')
                {
                    sb.Append('|');
                    k++;
                }
                else if (c == '|')
                {
                    cells.Add(sb.ToString().Trim());
                    sb.Length = 0;
                }
                else
                {
                    sb.Append(c);
                }
            }
            cells.Add(sb.ToString().Trim());
            return cells;
        }

        /// <summary>
        /// Recomputes markdown header font sizes from the document base size.
        /// Needed because headers store an absolute <see cref="TextElement.FontSize"/> that
        /// does not inherit when <see cref="FlowDocument.FontSize"/> changes.
        /// </summary>
        public static void ApplyHeaderFontSizes(FlowDocument document, double baseFontSize)
        {
            if (document == null || baseFontSize <= 0)
                return;

            foreach (Block block in document.Blocks)
            {
                Paragraph paragraph = block as Paragraph;
                if (paragraph == null || !(paragraph.Tag is int))
                    continue;

                int headerLevel = (int)paragraph.Tag;
                if (headerLevel < 1 || headerLevel > HeaderMultipliers.Length)
                    continue;

                paragraph.FontSize = baseFontSize * HeaderMultipliers[headerLevel - 1];
            }
        }

        private static void ReplaceInRuns(InlineCollection inlines, Regex pattern, Func<Match, Inline> createInline)
        {
            foreach (var run in inlines.OfType<Run>().ToList())
            {
                if (!pattern.IsMatch(run.Text))
                    continue;

                var newInlines = BuildInlines(run.Text, pattern.Matches(run.Text), createInline);

                foreach (var inline in newInlines)
                    inlines.InsertBefore(run, inline);
                inlines.Remove(run);
            }
        }

        private static Hyperlink CreateLinkInline(string displayText, string url)
        {
            string text = displayText;
            FontWeight fontWeight = FontWeights.Normal;
            FontStyle fontStyle = FontStyles.Normal;

            if (TryStripWrappedMarkers(ref text, "***") || TryStripWrappedMarkers(ref text, "___"))
            {
                fontWeight = FontWeights.Bold;
                fontStyle = FontStyles.Italic;
            }
            else if (TryStripWrappedMarkers(ref text, "**") || TryStripWrappedMarkers(ref text, "__"))
            {
                fontWeight = FontWeights.Bold;
            }

            return CreateHyperlink(text, url, fontWeight, fontStyle);
        }

        private static bool TryStripWrappedMarkers(ref string text, string marker)
        {
            if (text == null || marker == null || text.Length <= marker.Length * 2)
                return false;
            if (!text.StartsWith(marker, StringComparison.Ordinal) ||
                !text.EndsWith(marker, StringComparison.Ordinal))
                return false;

            text = text.Substring(marker.Length, text.Length - marker.Length * 2);
            return true;
        }

        private static string BoldLinkText(Match match)
        {
            return !string.IsNullOrEmpty(match.Groups[1].Value)
                ? match.Groups[1].Value
                : match.Groups[3].Value;
        }

        private static string BoldLinkUrl(Match match)
        {
            return !string.IsNullOrEmpty(match.Groups[2].Value)
                ? match.Groups[2].Value.Trim()
                : match.Groups[4].Value.Trim();
        }

        private static Hyperlink CreateHyperlink(
            string displayText,
            string url,
            FontWeight fontWeight = default(FontWeight),
            FontStyle fontStyle = default(FontStyle))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return null;

            if (fontWeight == default(FontWeight))
                fontWeight = FontWeights.Normal;
            if (fontStyle == default(FontStyle))
                fontStyle = FontStyles.Normal;

            var hyperlink = new Hyperlink(new Run(displayText))
            {
                NavigateUri = uri,
                Cursor = Cursors.Hand,
                FontWeight = fontWeight,
                FontStyle = fontStyle
            };
            hyperlink.SetResourceReference(TextElement.ForegroundProperty, "ChatTextColorBrush");
            AttachTooltip(hyperlink, uri.AbsoluteUri);
            hyperlink.PreviewMouseLeftButtonDown += OnHyperlinkClick;
            return hyperlink;
        }

        private static void AttachTooltip(Hyperlink hyperlink, string text)
        {
            var tooltip = new ToolTip { Content = text, Placement = PlacementMode.Mouse };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, _) => { timer.Stop(); tooltip.IsOpen = true; };
            hyperlink.MouseEnter += (s, _) => timer.Start();
            hyperlink.MouseLeave += (s, _) => { timer.Stop(); tooltip.IsOpen = false; };
        }

        private static Popup _copiedPopup;
        private static DispatcherTimer _copiedPopupTimer;

        private static void OnHyperlinkClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Hyperlink hyperlink && hyperlink.NavigateUri != null)
            {
                string url = hyperlink.NavigateUri.AbsoluteUri;
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    // Ctrl+click opens the link in the default browser.
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                else
                {
                    try { Clipboard.SetText(url); } catch { }
                    ShowCopiedPopup();
                }
                e.Handled = true;
            }
        }

        /// <summary>
        /// Shows a floating "Copied" badge near the mouse pointer after a URL is
        /// copied; it stays ~1.2 s, then the next copy re-places it.
        /// </summary>
        private static void ShowCopiedPopup()
        {
            if (_copiedPopup == null)
            {
                var border = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xDD, 30, 30, 30)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 4, 8, 4)
                };
                border.Child = new TextBlock
                {
                    Text = "Copied",
                    Foreground = Brushes.White,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold
                };
                _copiedPopup = new Popup
                {
                    Child = border,
                    Placement = PlacementMode.MousePoint,
                    PlacementTarget = Application.Current != null ? Application.Current.MainWindow : null,
                    StaysOpen = true,
                    AllowsTransparency = true,
                    HorizontalOffset = 12,
                    VerticalOffset = 12
                };
            }

            if (_copiedPopupTimer != null)
            {
                _copiedPopupTimer.Stop();
                _copiedPopup.IsOpen = false;
            }
            _copiedPopup.IsOpen = true;
            _copiedPopupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            _copiedPopupTimer.Tick += (s, args) =>
            {
                if (_copiedPopup != null)
                    _copiedPopup.IsOpen = false;
                _copiedPopupTimer.Stop();
            };
            _copiedPopupTimer.Start();
        }

        private static bool ProcessHorizontalRule(Paragraph paragraph, string trimmedText)
        {
            if (!HorizontalRulePattern.IsMatch(trimmedText))
                return false;

            Brush separatorBrush = paragraph.Foreground
                ?? (Application.Current?.MainWindow?.Foreground as Brush)
                ?? SystemColors.ControlTextBrush;

            paragraph.Inlines.Clear();
            paragraph.Margin = new Thickness(0, 3, 0, 3);
            paragraph.BorderBrush = separatorBrush;
            paragraph.BorderThickness = new Thickness(0, 1, 0, 0);
            paragraph.Padding = new Thickness(0);
            paragraph.LineHeight = 1;
            paragraph.FontSize = 1;
            paragraph.Inlines.Add(new Run(" "));

            return true;
        }

        private static bool TryParseBacktickFence(string trimmedText, out int fenceLength, out bool hasInfoString, out string infoString)
        {
            fenceLength = 0;
            hasInfoString = false;
            infoString = null;

            Match match = BacktickFencePattern.Match(trimmedText);
            if (!match.Success)
                return false;

            fenceLength = match.Groups[2].Value.Length;
            infoString = match.Groups[3].Value.Trim();
            hasInfoString = !string.IsNullOrEmpty(infoString);
            return true;
        }

        private static string FirstCapturedGroup(Match match)
        {
            return !string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[2].Value;
        }

        private static List<Inline> BuildInlines(string text, MatchCollection matches, Func<Match, Inline> createFormattedInline)
        {
            var inlines = new List<Inline>();
            int lastIndex = 0;

            foreach (Match match in matches)
            {
                if (match.Index > lastIndex)
                    inlines.Add(new Run(text.Substring(lastIndex, match.Index - lastIndex)));

                inlines.Add(createFormattedInline(match));
                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < text.Length)
                inlines.Add(new Run(text.Substring(lastIndex)));

            return inlines;
        }

    }
}