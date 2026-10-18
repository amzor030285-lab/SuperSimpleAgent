using System;
using System.Collections.Generic;
using System.IO;

namespace SuperSimpleAgentCLI
{
    /// <summary>
    /// Interactive line reader for the CLI prompt: Tab completes slash
    /// commands, Up/Down recall recent input, Backspace/Delete/arrows edit
    /// the line. Built directly on Console.ReadKey so it runs on the plain
    /// Windows console (XP conhost included) without any ANSI or VT-100
    /// sequences. When stdin is redirected (pipes, scripted runs, the GUI
    /// driver), reading falls back to plain Console.ReadLine.
    /// </summary>
    static class ConsoleInput
    {
        /// <summary>Everything a Tab may complete. Keep in sync with PrintCliInstructions.</summary>
        static readonly string[] Commands =
        {
            "/approval", "/buildindex", "/clear", "/clearindex", "/compact",
            "/compactfull", "/delete", "/exit", "/help", "/image", "/load",
            "/plan", "/reasoning", "/reload", "/rename", "/save", "/sessions"
        };

        static readonly List<string> History = new List<string>();

        // Flipped the first time Console.KeyAvailable proves stdin is not a
        // keyboard; afterwards every Read takes the plain ReadLine path.
        static bool redirected;

        public static string Read()
        {
            if (!redirected)
            {
                // Console.KeyAvailable throws when stdin is redirected - use
                // that as the keyboard-detection probe (no .NET 4.5+
                // Console.IsInputRedirected on this target).
                try
                {
                    if (Console.KeyAvailable) { }
                    return ReadInteractive();
                }
                catch (InvalidOperationException)
                {
                    redirected = true;
                }
                catch (IOException)
                {
                    redirected = true;
                }
            }
            return Console.ReadLine();
        }

        static string ReadInteractive()
        {
            int startX = Console.CursorLeft;
            int startY = Console.CursorTop;
            int width = Math.Max(1, Console.WindowWidth);

            string text = string.Empty;
            int cursor = 0;
            int oldLen = 0;

            // History: Up steps from newest (Count-1) toward oldest (0);
            // the line being edited is kept as a draft in case Down lands
            // past the newest entry.
            int histIdx = -1;
            string draft = null;

            // Tab state: candidates must be recomputed after any edit; tabIdx
            // cycles through them on repeated Tab presses.
            string[] tabCandidates = null;
            int tabIdx = -1;

            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                string before = text;
                int beforeCursor = cursor;

                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        Redraw(startX, startY, width, text, oldLen, cursor);
                        // ReadKey(true) does not echo Enter; ReadLine would.
                        Console.Out.Write(Environment.NewLine);
                        Console.Out.Flush();
                        if (text.Length > 0
                            && (History.Count == 0 || History[History.Count - 1] != text))
                        {
                            History.Add(text);
                            if (History.Count > 500)
                                History.RemoveAt(0);
                        }
                        return text;

                    case ConsoleKey.Backspace:
                        if (cursor > 0)
                        {
                            text = text.Remove(cursor - 1, 1);
                            cursor--;
                        }
                        break;

                    case ConsoleKey.Delete:
                        if (cursor < text.Length)
                            text = text.Remove(cursor, 1);
                        break;

                    case ConsoleKey.LeftArrow:
                        if (cursor > 0)
                            cursor--;
                        break;

                    case ConsoleKey.RightArrow:
                        if (cursor < text.Length)
                            cursor++;
                        break;

                    case ConsoleKey.Home:
                        cursor = 0;
                        break;

                    case ConsoleKey.End:
                        cursor = text.Length;
                        break;

                    case ConsoleKey.UpArrow:
                        if (History.Count == 0)
                            break;
                        if (histIdx == -1)
                        {
                            draft = text;
                            histIdx = History.Count - 1;
                        }
                        else if (histIdx > 0)
                            histIdx--;
                        text = History[histIdx];
                        cursor = text.Length;
                        break;

                    case ConsoleKey.DownArrow:
                        if (histIdx == -1)
                            break;
                        histIdx++;
                        if (histIdx >= History.Count)
                        {
                            histIdx = -1;
                            text = draft ?? string.Empty;
                        }
                        else text = History[histIdx];
                        cursor = text.Length;
                        break;

                    case ConsoleKey.Tab:
                        // A complete command word: no space before the cursor
                        // and the word starts with "/". Completion replaces the
                        // word from its first character to the cursor.
                        int wordStart = text.LastIndexOf(' ', Math.Max(0, cursor - 1)) + 1;
                        string word = text.Substring(wordStart, cursor - wordStart);
                        if (word.Length == 0 || word[0] != '/')
                            break;
                        if (tabCandidates == null)
                        {
                            List<string> matches = new List<string>();
                            foreach (string cmd in Commands)
                                if (cmd.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                                    matches.Add(cmd);
                            if (matches.Count == 0)
                                break;
                            tabCandidates = matches.ToArray();
                            tabIdx = -1;
                        }
                        tabIdx = (tabIdx + 1) % tabCandidates.Length;
                        text = text.Remove(wordStart, cursor - wordStart)
                                   .Insert(wordStart, tabCandidates[tabIdx]);
                        cursor = wordStart + tabCandidates[tabIdx].Length;
                        break;

                    default:
                        if (!char.IsControl(key.KeyChar))
                        {
                            text = text.Insert(cursor, key.KeyChar.ToString());
                            cursor++;
                        }
                        break;
                }

                bool edited = before != text || beforeCursor != cursor;
                bool tabKey = key.Key == ConsoleKey.Tab;
                if (edited && !tabKey)
                    tabCandidates = null; // new input invalidates the cycle

                if (edited || tabKey)
                    Redraw(startX, startY, width, text, oldLen, cursor);

                oldLen = text.Length;
            }
        }

        /// <summary>
        /// Repaints the editing line in place: rewrites the text over what was
        /// on screen, blanks the leftover tail from the longer previous
        /// content, and parks the caret at the cursor. Pure Console output, so
        /// it works on any Windows console host, XP included.
        /// </summary>
        static void Redraw(int startX, int startY, int width, string text, int oldLen, int cursor)
        {
            try
            {
                if (startX > 0 || startY > 0)
                    Console.SetCursorPosition(startX, startY);
                Console.Write(text);
                Console.Write(new string(' ', Math.Max(0, oldLen - text.Length)));
                // Main replaces Console.Out with a non-auto-flush StreamWriter:
                // SetCursorPosition below runs immediately while the write would
                // sit in the buffer, so flush first or the text lands at the
                // caret's new position and trails duplicated characters.
                Console.Out.Flush();
                int col = (startX + cursor) % width;
                int row = startY + (startX + cursor) / width;
                Console.SetCursorPosition(col, row);
            }
            catch (ArgumentOutOfRangeException)
            {
                // The console scrolled while the line was being edited (long
                // histories of a narrow window): nothing safe to repaint.
            }
            catch (InvalidOperationException)
            {
                // Row off-screen after a scroll: nothing safe to redraw.
            }
        }
    }
}
