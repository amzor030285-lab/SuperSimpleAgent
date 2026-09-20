using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Markdig;
using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Mathematics;
using MDInline = Markdig.Syntax.Inlines.Inline;
using MDBlock = Markdig.Syntax.Block;
using MDTable = Markdig.Extensions.Tables.Table;
using MDTableRow = Markdig.Extensions.Tables.TableRow;
using MDTableCell = Markdig.Extensions.Tables.TableCell;

namespace SimpleLLMChatGUI
{
    /// <summary>
    /// Renders a turn's markdown source (parsed by Markdig) into a
    /// <see cref="FlowDocument"/> using the app's chat styling.
    /// </summary>
    public static class MarkdownRenderer
    {
        private static readonly MarkdownPipeline Pipeline =
            new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        /// <summary>
        /// H1–H6 size relative to the document base font size.
        /// </summary>
        private static readonly double[] HeaderMultipliers = { 1.3, 1.2, 1.1, 1.0, 1.0, 1.0 };

        /// <summary>
        /// Floor pixel width of a table column so short cells (e.g. "ID", "1")
        /// keep a usable share of the table width.
        /// </summary>
        private const double TableMinColumnWidth = 40;

        /// <summary>
        /// Cell border padding (6px left + 6px right) added to measured content
        /// widths when computing table column weights.
        /// </summary>
        private const double TableCellPaddingWidth = 12;

        /// <summary>
        /// Parses <paramref name="source"/> and replaces all of the document's
        /// blocks with the rendered result.
        /// </summary>
        public static void Render(FlowDocument document, string source)
        {
            if (document == null || string.IsNullOrEmpty(source))
                return;

            MarkdownDocument parsed = Markdown.Parse(source, Pipeline);
            double baseFontSize = document.FontSize > 0 ? document.FontSize : FontHandler.GetFontSize();

            document.Blocks.Clear();
            for (int i = 0; i < parsed.Count; i++)
                RenderBlock(document, parsed[i], baseFontSize);

            // A FlowDocument must keep at least one block.
            if (document.Blocks.Count == 0)
                document.Blocks.Add(new Paragraph());
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

            foreach (System.Windows.Documents.Block block in document.Blocks)
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

        private static void RenderBlock(FlowDocument document, MDBlock block, double baseFontSize)
        {
            // MathBlock is a FencedCodeBlock subclass — match it first.
            MathBlock math = block as MathBlock;
            if (math != null)
            {
                document.Blocks.Add(BuildCodeBlockContainer("math", CodeLines(math), baseFontSize));
                return;
            }

            FencedCodeBlock fence = block as FencedCodeBlock;
            if (fence != null)
            {
                string language = fence.Info != null ? fence.Info.Trim() : null;
                document.Blocks.Add(BuildCodeBlockContainer(language, CodeLines(fence), baseFontSize));
                return;
            }

            CodeBlock code = block as CodeBlock;
            if (code != null)
            {
                document.Blocks.Add(BuildCodeBlockContainer(null, CodeLines(code), baseFontSize));
                return;
            }

            HeadingBlock heading = block as HeadingBlock;
            if (heading != null)
            {
                var paragraph = new Paragraph
                {
                    Tag = heading.Level,
                    FontSize = baseFontSize * HeaderMultipliers[Math.Min(heading.Level, 6) - 1],
                    FontWeight = FontWeights.Bold
                };
                AppendInlines(paragraph.Inlines, heading.Inline, baseFontSize);
                document.Blocks.Add(paragraph);
                return;
            }

            ThematicBreakBlock rule = block as ThematicBreakBlock;
            if (rule != null)
            {
                document.Blocks.Add(BuildHorizontalRule());
                return;
            }

            MDTable table = block as MDTable;
            if (table != null)
            {
                System.Windows.Documents.Table docTable = BuildTableContainer(table, baseFontSize, TableAvailableWidth(document));
                if (docTable != null)
                    document.Blocks.Add(docTable);
                return;
            }

            ListBlock list = block as ListBlock;
            if (list != null)
            {
                document.Blocks.Add(BuildListContainer(list, baseFontSize));
                return;
            }

            QuoteBlock quote = block as QuoteBlock;
            if (quote != null)
            {
                for (int i = 0; i < quote.Count; i++)
                    RenderQuotedChild(document, quote[i], baseFontSize);
                return;
            }

            ParagraphBlock paragraphBlock = block as ParagraphBlock;
            if (paragraphBlock != null)
            {
                var paragraph = new Paragraph();
                AppendInlines(paragraph.Inlines, paragraphBlock.Inline, baseFontSize);
                document.Blocks.Add(paragraph);
                return;
            }

            HtmlBlock html = block as HtmlBlock;
            if (html != null)
            {
                // Raw HTML blocks render as their source text, one paragraph per
                // line (the legacy renderer showed them as-is too).
                List<string> lines = CodeLines(html);
                for (int i = 0; i < lines.Count; i++)
                {
                    var paragraph = new Paragraph();
                    paragraph.Inlines.Add(new Run(lines[i]));
                    document.Blocks.Add(paragraph);
                }
                return;
            }

            // LinkReferenceDefinitionGroup, YamlFrontMatterBlock, blank lines and
            // anything else: nothing to render.
        }

        private static void RenderQuotedChild(FlowDocument document, MDBlock block, double baseFontSize)
        {
            ParagraphBlock paragraphBlock = block as ParagraphBlock;
            if (paragraphBlock == null)
            {
                // Non-text quoted content (tables, code, lists) renders as-is.
                RenderBlock(document, block, baseFontSize);
                return;
            }

            var paragraph = new Paragraph
            {
                Padding = new Thickness(10, 0, 0, 0),
                BorderThickness = new Thickness(3, 0, 0, 0)
            };
            paragraph.SetResourceReference(Border.BorderBrushProperty, "RoleLabelBrush");
            AppendInlines(paragraph.Inlines, paragraphBlock.Inline, baseFontSize);
            document.Blocks.Add(paragraph);
        }

        /// <summary>
        /// Code/math/html source lines without trailing blank lines.
        /// </summary>
        private static List<string> CodeLines(LeafBlock block)
        {
            var lines = new List<string>();
            StringLineGroup group = block.Lines;
            foreach (StringLine line in group.Lines)
                lines.Add(line.ToString().TrimEnd('\r', '\n'));

            while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0)
                lines.RemoveAt(lines.Count - 1);

            return lines;
        }

        /// <summary>
        /// Builds a fenced code block: a header row (language label + Copy button)
        /// over a monospace code area, wrapped in a
        /// <see cref="BlockUIContainer"/>.
        /// </summary>
        private static BlockUIContainer BuildCodeBlockContainer(string language, List<string> codeLines, double baseFontSize)
        {
            return new BlockUIContainer(BuildCodeBlockContent(language, codeLines, baseFontSize))
            {
                Margin = new Thickness(0, 4, 0, 4)
            };
        }

        /// <summary>
        /// The hosted code UI (header row + code area) as a plain
        /// <see cref="StackPanel"/> — also usable inside list items, where a
        /// <see cref="BlockUIContainer"/> cannot be placed.
        /// </summary>
        private static StackPanel BuildCodeBlockContent(string language, List<string> codeLines, double baseFontSize)
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

            return root;
        }

        private static Paragraph BuildHorizontalRule()
        {
            Brush separatorBrush = (Application.Current?.MainWindow?.Foreground as Brush)
                ?? SystemColors.ControlTextBrush;

            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 3, 0, 3),
                BorderBrush = separatorBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0),
                LineHeight = 1,
                FontSize = 1
            };
            paragraph.Inlines.Add(new Run(" "));
            return paragraph;
        }

        /// <summary>
        /// Width a table can use inside the document: PageWidth minus page
        /// padding (the chat document uses zero padding), with a fallback for
        /// documents whose PageWidth has not been set yet.
        /// </summary>
        private static double TableAvailableWidth(FlowDocument document)
        {
            double width = document.PageWidth - document.PagePadding.Left - document.PagePadding.Right;
            return width > 0 ? width : 800;
        }

        /// <summary>
        /// Builds a markdown table as a native WPF document
        /// <see cref="System.Windows.Documents.Table"/> so its text is part of the
        /// chat document and can be selected and copied like any other text.
        /// Columns use fixed pixel widths proportional to their measured content:
        /// .NET 4 WPF does not distribute star-sized <see cref="TableColumn"/>
        /// widths reliably (unequal stars collapse columns to zero width). If the
        /// measured total exceeds the document width, all columns are scaled down
        /// proportionally so the table always fits and wide text wraps. The table
        /// supplies the outer border; each cell supplies only its right/bottom
        /// border so inner lines are 1px.
        /// </summary>
        private static System.Windows.Documents.Table BuildTableContainer(MDTable table, double baseFontSize, double availableWidth)
        {
            var rows = new List<MDTableRow>();
            for (int i = 0; i < table.Count; i++)
            {
                MDTableRow row = table[i] as MDTableRow;
                if (row != null)
                    rows.Add(row);
            }

            // Markdig 0.18.3 reports ColumnDefinitions.Count as N+1 (an extra
            // empty alignment slot); the real column count is the widest row.
            int columnCount = 0;
            for (int r = 0; r < rows.Count; r++)
                columnCount = Math.Max(columnCount, rows[r].Count);

            int rowCount = rows.Count;
            if (columnCount == 0 || rowCount == 0)
                return null;

            // Column pixel widths: widest cell per column (headers bold, inline
            // bold/code measured in their rendered styles, +10% slack so the
            // column fits its widest cell on one line). The total is scaled down
            // to fit the document width when it overflows.
            var widths = new double[columnCount];
            double total = 0;
            for (int c = 0; c < columnCount; c++)
            {
                double w = MeasureTextWidth(CellText(rows[0], c), baseFontSize, FontWeights.Bold, ChatFontFamily());
                for (int r = 1; r < rowCount; r++)
                    w = Math.Max(w, MeasureCellInlines(CellInlines(rows[r], c), baseFontSize));
                widths[c] = Math.Max(TableMinColumnWidth, w * 1.1 + TableCellPaddingWidth);
                total += widths[c];
            }
            if (availableWidth > 0 && total > availableWidth)
            {
                double k = availableWidth / total;
                for (int c = 0; c < columnCount; c++)
                    widths[c] *= k;
            }

            var docTable = new System.Windows.Documents.Table
            {
                BorderThickness = new Thickness(1),
                CellSpacing = 0,
                Margin = new Thickness(0, 4, 0, 4)
            };
            docTable.SetResourceReference(System.Windows.Documents.Block.BorderBrushProperty, "TableBorderBrush");
            for (int c = 0; c < columnCount; c++)
                docTable.Columns.Add(new TableColumn { Width = new GridLength(widths[c], GridUnitType.Pixel) });

            var rowGroup = new TableRowGroup();
            docTable.RowGroups.Add(rowGroup);

            for (int r = 0; r < rowCount; r++)
            {
                bool isHeader = rows[r].IsHeader;
                var docRow = new System.Windows.Documents.TableRow();
                for (int c = 0; c < columnCount; c++)
                {
                    var cell = new System.Windows.Documents.TableCell
                    {
                        Padding = new Thickness(6, 3, 6, 3),
                        BorderThickness = new Thickness(0, 0, c == columnCount - 1 ? 0 : 1, r == rowCount - 1 ? 0 : 1),
                        TextAlignment = CellAlignment(table, c)
                    };
                    cell.SetResourceReference(System.Windows.Documents.TableCell.BorderBrushProperty, "TableBorderBrush");
                    if (isHeader)
                    {
                        cell.SetResourceReference(TextElement.BackgroundProperty, "TableHeaderBackgroundBrush");
                        cell.FontWeight = FontWeights.Bold;
                    }

                    var paragraph = new Paragraph { Margin = new Thickness(0) };
                    paragraph.SetResourceReference(TextElement.ForegroundProperty, "ChatTextColorBrush");
                    AppendInlines(paragraph.Inlines, CellInlines(rows[r], c), baseFontSize);
                    cell.Blocks.Add(paragraph);
                    docRow.Cells.Add(cell);
                }
                rowGroup.Rows.Add(docRow);
            }

            return docTable;
        }

        private static ContainerInline CellInlines(MDTableRow row, int column)
        {
            if (row == null || column >= row.Count)
                return null;

            MDTableCell cell = row[column] as MDTableCell;
            if (cell == null)
                return null;

            for (int i = 0; i < cell.Count; i++)
            {
                ParagraphBlock paragraph = cell[i] as ParagraphBlock;
                if (paragraph != null)
                    return paragraph.Inline;
            }

            return null;
        }

        private static string CellText(MDTableRow row, int column)
        {
            ContainerInline inlines = CellInlines(row, column);
            if (inlines == null)
                return string.Empty;

            var sb = new StringBuilder();
            MDInline inline = inlines.FirstChild;
            while (inline != null)
            {
                AppendPlainText(sb, inline);
                inline = inline.NextSibling;
            }
            return sb.ToString();
        }

        private static TextAlignment CellAlignment(MDTable table, int column)
        {
            if (column >= table.ColumnDefinitions.Count)
                return TextAlignment.Left;

            TableColumnAlign? alignment = table.ColumnDefinitions[column].Alignment;
            if (alignment == null)
                return TextAlignment.Left;
            if (alignment == TableColumnAlign.Center)
                return TextAlignment.Center;
            if (alignment == TableColumnAlign.Right)
                return TextAlignment.Right;
            return TextAlignment.Left;
        }

        private static double MeasureCellInlines(ContainerInline inlines, double baseFontSize)
        {
            if (inlines == null)
                return 0;

            var sb = new StringBuilder();
            MDInline inline = inlines.FirstChild;
            while (inline != null)
            {
                AppendPlainText(sb, inline);
                inline = inline.NextSibling;
            }
            string text = sb.ToString();
            if (text.Length == 0)
                return 0;

            double w = MeasureTextWidth(text, baseFontSize, FontWeights.Normal, ChatFontFamily());
            if (ContainsBold(inlines))
                w = Math.Max(w, MeasureTextWidth(text, baseFontSize, FontWeights.Bold, ChatFontFamily()));
            if (ContainsCode(inlines))
                w = Math.Max(w, MeasureTextWidth(text, baseFontSize, FontWeights.Normal, CodeFontFamily()));
            return w;
        }

        private static bool ContainsBold(ContainerInline root)
        {
            MDInline inline = root != null ? root.FirstChild : null;
            while (inline != null)
            {
                EmphasisInline emphasis = inline as EmphasisInline;
                if (emphasis != null && emphasis.DelimiterChar != '~' && emphasis.DelimiterCount >= 2)
                    return true;

                ContainerInline container = inline as ContainerInline;
                if (container != null && ContainsBold(container))
                    return true;

                inline = inline.NextSibling;
            }
            return false;
        }

        private static bool ContainsCode(ContainerInline root)
        {
            MDInline inline = root != null ? root.FirstChild : null;
            while (inline != null)
            {
                if (inline is CodeInline)
                    return true;

                ContainerInline container = inline as ContainerInline;
                if (container != null && ContainsCode(container))
                    return true;

                inline = inline.NextSibling;
            }
            return false;
        }

        /// <summary>
        /// Builds a (nested) list as a vertical stack of two-column rows (marker,
        /// then wrapping text), wrapped in a <see cref="BlockUIContainer"/> so
        /// wrapped lines hang under the text instead of the marker. Nested levels
        /// are indented inside the previous level's text column.
        /// </summary>
        private static BlockUIContainer BuildListContainer(ListBlock list, double baseFontSize)
        {
            return new BlockUIContainer(BuildListLevel(list, baseFontSize))
            {
                Margin = new Thickness(0, 4, 0, 4)
            };
        }

        private static StackPanel BuildListLevel(ListBlock list, double baseFontSize)
        {
            var stack = new StackPanel { Orientation = Orientation.Vertical };

            for (int i = 0; i < list.Count; i++)
            {
                ListItemBlock item = list[i] as ListItemBlock;
                if (item == null)
                    continue;

                stack.Children.Add(BuildListItem(item, list.IsOrdered, baseFontSize));
            }

            return stack;
        }

        private static Grid BuildListItem(ListItemBlock item, bool ordered, double baseFontSize)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Ordered markers always display as "N." (the legacy renderer
            // normalized "N)" the same way).
            string marker = ordered ? item.Order + "." : "\u2022";
            var markerBlock = CreateListTextBlock(baseFontSize, new Thickness(0, 1, 8, 1));
            markerBlock.Inlines.Add(new Run(marker));
            Grid.SetColumn(markerBlock, 0);
            row.Children.Add(markerBlock);

            var content = new StackPanel { Orientation = Orientation.Vertical };

            for (int i = 0; i < item.Count; i++)
            {
                MDBlock child = item[i];

                ParagraphBlock paragraph = child as ParagraphBlock;
                if (paragraph != null)
                {
                    // Plain items become a read-only TextBox so part of the item can
                    // be selected and copied; items with inline markdown live in a
                    // read-only rich text host (formatted TextBlocks are not
                    // selectable in WPF).
                    string plainText;
                    if (TryPlainText(paragraph.Inline, out plainText))
                        content.Children.Add(CreateListTextBox(plainText, baseFontSize, new Thickness(0, 1, 0, 1)));
                    else
                        content.Children.Add(CreateRichTextHost(paragraph.Inline, TextAlignment.Left, false,
                            baseFontSize, new Thickness(0, 1, 0, 1)));
                    continue;
                }

                ListBlock nested = child as ListBlock;
                if (nested != null)
                {
                    var childPanel = BuildListLevel(nested, baseFontSize);
                    childPanel.Margin = new Thickness(Math.Max(12.0, baseFontSize), 2, 0, 2);
                    content.Children.Add(childPanel);
                    continue;
                }

                CodeBlock code = child as CodeBlock;
                if (code != null)
                {
                    FencedCodeBlock fence = code as FencedCodeBlock;
                    string language = fence != null && fence.Info != null ? fence.Info.Trim() : null;
                    content.Children.Add(BuildCodeBlockContent(language, CodeLines(code), baseFontSize));
                    continue;
                }

                // Other item children (quotes, math, html) stay unrendered.
            }

            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            return row;
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
        /// Formatted (inline markdown) text as a read-only selectable
        /// <see cref="RichTextBox"/> host: WPF cannot select text inside a hosted
        /// TextBlock, so formatted text lives in its own read-only rich control
        /// (same metrics as the surrounding text).
        /// </summary>
        private static NoScrollRichTextBox CreateRichTextHost(ContainerInline inlines, TextAlignment alignment,
            bool bold, double baseFontSize, Thickness margin)
        {
            var host = new NoScrollRichTextBox
            {
                Margin = margin,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                IsTabStop = false,
                AcceptsReturn = true,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                FontSize = baseFontSize,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            host.SetResourceReference(TextElement.ForegroundProperty, "ChatTextColorBrush");
            if (bold)
                host.FontWeight = FontWeights.Bold;

            // Alignment lives on the paragraph: RichTextBox has no TextAlignment.
            // No LineHeight override: keep the font's natural line height so
            // rich and plain rows come out the same height.
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                TextAlignment = alignment
            };
            AppendInlines(paragraph.Inlines, inlines, baseFontSize);
            // A fresh RichTextBox document already contains an empty paragraph;
            // drop it or every rich cell gets one extra (empty) line.
            host.Document.Blocks.Clear();
            host.Document.Blocks.Add(paragraph);
            return host;
        }

        /// <summary>
        /// Renders a Markdig inline tree into WPF inlines (runs, spans,
        /// hyperlinks, line breaks).
        /// </summary>
        private static void AppendInlines(InlineCollection target, ContainerInline root, double baseFontSize)
        {
            MDInline inline = root != null ? root.FirstChild : null;
            while (inline != null)
            {
                AppendInline(target, inline, baseFontSize);
                inline = inline.NextSibling;
            }
        }

        private static void AppendInline(InlineCollection target, MDInline inline, double baseFontSize)
        {
            LiteralInline literal = inline as LiteralInline;
            if (literal != null)
            {
                target.Add(new Run(literal.Content.ToString()));
                return;
            }

            CodeInline code = inline as CodeInline;
            if (code != null)
            {
                target.Add(CreateCodeSpan(code.Content.ToString()));
                return;
            }

            EmphasisInline emphasis = inline as EmphasisInline;
            if (emphasis != null)
            {
                if (emphasis.DelimiterChar == '~')
                {
                    // Strikethrough: ~~text~~.
                    if (emphasis.DelimiterCount >= 2)
                    {
                        var strikeSpan = new Span();
                        strikeSpan.TextDecorations = TextDecorations.Strikethrough;
                        AppendInlines(strikeSpan.Inlines, emphasis, baseFontSize);
                        target.Add(strikeSpan);
                    }
                    return;
                }

                var span = new Span();
                if (emphasis.DelimiterCount >= 2)
                    span.FontWeight = FontWeights.Bold;
                if (emphasis.DelimiterCount % 2 == 1)
                    span.FontStyle = FontStyles.Italic;
                AppendInlines(span.Inlines, emphasis, baseFontSize);
                target.Add(span);
                return;
            }

            LinkInline link = inline as LinkInline;
            if (link != null)
            {
                if (link.IsImage)
                {
                    // Images render as their alt text.
                    string alt = link.Label != null ? link.Label : string.Empty;
                    if (alt.Length > 0)
                        target.Add(new Run(alt));
                    return;
                }

                Hyperlink hyperlink = CreateHyperlink(link.Url != null ? link.Url.Trim() : null);
                if (hyperlink == null)
                {
                    // Non-http links (mailto:, anchors, relative paths) render
                    // as their label text.
                    AppendInlines(target, link, baseFontSize);
                    return;
                }

                AppendInlines(hyperlink.Inlines, link, baseFontSize);
                target.Add(hyperlink);
                return;
            }

            AutolinkInline autolink = inline as AutolinkInline;
            if (autolink != null)
            {
                Hyperlink hyperlink = CreateHyperlink(autolink.Url);
                if (hyperlink == null)
                {
                    target.Add(new Run(autolink.Url));
                    return;
                }
                hyperlink.Inlines.Add(new Run(autolink.Url));
                target.Add(hyperlink);
                return;
            }

            LineBreakInline lineBreak = inline as LineBreakInline;
            if (lineBreak != null)
            {
                // Soft and hard line breaks both become a WPF line break, keeping
                // the legacy one-line-per-paragraph layout.
                target.Add(new LineBreak());
                return;
            }

            HtmlInline html = inline as HtmlInline;
            if (html != null)
            {
                target.Add(new Run(html.Tag));
                return;
            }

            HtmlEntityInline entity = inline as HtmlEntityInline;
            if (entity != null)
            {
                target.Add(new Run(entity.Original.ToString()));
                return;
            }

            TaskList task = inline as TaskList;
            if (task != null)
            {
                target.Add(new Run(task.Checked ? "[x]" : "[ ]"));
                return;
            }

            MathInline math = inline as MathInline;
            if (math != null)
            {
                target.Add(CreateCodeSpan(math.Content.ToString()));
                return;
            }

            // Unknown inline: fall back to its plain text.
            var sb = new StringBuilder();
            AppendPlainText(sb, inline);
            if (sb.Length > 0)
                target.Add(new Run(sb.ToString()));
        }

        private static Span CreateCodeSpan(string text)
        {
            var codeRun = new Run(text);
            var codeSpan = new Span(codeRun);
            codeSpan.SetResourceReference(TextElement.BackgroundProperty, "CodeBlockBackgroundColorBrush");
            codeSpan.SetResourceReference(TextElement.FontFamilyProperty, FontHandler.CodeBlockFontFamilyKey);
            codeRun.ClearValue(TextElement.FontFamilyProperty);
            return codeSpan;
        }

        /// <summary>
        /// True when the inlines carry no formatting beyond plain text (literals,
        /// line breaks and task markers); <paramref name="text"/> then holds the
        /// rendered text.
        /// </summary>
        private static bool TryPlainText(ContainerInline root, out string text)
        {
            var sb = new StringBuilder();
            MDInline inline = root != null ? root.FirstChild : null;
            while (inline != null)
            {
                if (!AppendPlainFragment(sb, inline))
                {
                    text = null;
                    return false;
                }
                inline = inline.NextSibling;
            }

            text = sb.ToString();
            return true;
        }

        private static bool AppendPlainFragment(StringBuilder sb, MDInline inline)
        {
            LiteralInline literal = inline as LiteralInline;
            if (literal != null)
            {
                sb.Append(literal.Content.ToString());
                return true;
            }

            LineBreakInline lineBreak = inline as LineBreakInline;
            if (lineBreak != null)
            {
                sb.Append('\n');
                return true;
            }

            TaskList task = inline as TaskList;
            if (task != null)
            {
                sb.Append(task.Checked ? "[x]" : "[ ]");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Collects the plain text of an inline subtree (markup discarded), for
        /// column-width measurement and unknown-inline fallbacks.
        /// </summary>
        private static void AppendPlainText(StringBuilder sb, MDInline inline)
        {
            LiteralInline literal = inline as LiteralInline;
            if (literal != null)
            {
                sb.Append(literal.Content.ToString());
                return;
            }

            CodeInline code = inline as CodeInline;
            if (code != null)
            {
                sb.Append(code.Content.ToString());
                return;
            }

            LineBreakInline lineBreak = inline as LineBreakInline;
            if (lineBreak != null)
            {
                sb.Append('\n');
                return;
            }

            HtmlInline html = inline as HtmlInline;
            if (html != null)
            {
                sb.Append(html.Tag);
                return;
            }

            HtmlEntityInline entity = inline as HtmlEntityInline;
            if (entity != null)
            {
                sb.Append(entity.Original.ToString());
                return;
            }

            TaskList task = inline as TaskList;
            if (task != null)
            {
                sb.Append(task.Checked ? "[x]" : "[ ]");
                return;
            }

            MathInline math = inline as MathInline;
            if (math != null)
            {
                sb.Append(math.Content.ToString());
                return;
            }

            ContainerInline container = inline as ContainerInline;
            if (container != null)
            {
                MDInline child = container.FirstChild;
                while (child != null)
                {
                    AppendPlainText(sb, child);
                    child = child.NextSibling;
                }
            }
        }

        private static Hyperlink CreateHyperlink(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return null;

            var hyperlink = new Hyperlink
            {
                NavigateUri = uri,
                Cursor = Cursors.Hand
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
            Hyperlink hyperlink = sender as Hyperlink;
            if (hyperlink != null && hyperlink.NavigateUri != null)
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

        /// <summary>
        /// Chat font family (user override or the system message font).
        /// </summary>
        private static FontFamily ChatFontFamily()
        {
            return FontHandler.TryGetFontFamily(App.Config.GetConfigValue("customfontfamily"))
                ?? SystemFonts.MessageFontFamily;
        }

        /// <summary>
        /// Monospace code font family (user override or Consolas).
        /// </summary>
        private static FontFamily CodeFontFamily()
        {
            return FontHandler.TryGetFontFamily(App.Config.GetConfigValue("codeblockfontfamily"))
                ?? new FontFamily("Consolas");
        }

        /// <summary>
        /// Single-line pixel width of <paramref name="text"/>. Used only for
        /// relative table column weights, so an approximate measurement is fine.
        /// </summary>
        private static double MeasureTextWidth(string text, double fontSize, FontWeight weight, FontFamily family)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, fontSize, Brushes.Black);
            return Math.Max(formatted.Width, formatted.WidthIncludingTrailingWhitespace);
        }
    }
}
