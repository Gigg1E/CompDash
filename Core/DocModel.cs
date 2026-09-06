using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CompDash.Core
{
    public enum BlockKind
    {
        Paragraph, Heading1, Heading2, Heading3,
        Bullet, Number, Quote, Code, Rule, Image, Table
    }

    public enum CellAlign { Left, Center, Right }

    /// <summary>A run of text with its formatting. Links keep their target.</summary>
    public sealed class Span
    {
        public string Text = "";
        public bool Bold, Italic, Code;
        public string Link;

        public Span() { }
        public Span(string text) { Text = text; }
    }

    public sealed class TableRow
    {
        public readonly List<List<Span>> Cells = new List<List<Span>>();
    }

    /// <summary>
    /// One block of a document, independent of the format it will be written to.
    /// Markdown and plain text parse into this; the Word writer consumes it.
    /// </summary>
    public sealed class Block
    {
        public BlockKind Kind = BlockKind.Paragraph;
        public readonly List<Span> Spans = new List<Span>();
        public int ListLevel;
        public string ImagePath;
        public string RawText;                       // code blocks keep their text verbatim

        public readonly List<TableRow> Rows = new List<TableRow>();
        public readonly List<CellAlign> Aligns = new List<CellAlign>();
        public bool HeaderRow;

        public static Block Text(BlockKind kind, string text)
        {
            var b = new Block { Kind = kind };
            b.Spans.Add(new Span(text));
            return b;
        }

        public string PlainText()
        {
            if (RawText != null) return RawText;
            var sb = new StringBuilder();
            foreach (var s in Spans) sb.Append(s.Text);
            return sb.ToString();
        }
    }

    /// <summary>
    /// A deliberately small Markdown reader: the subset that shows up in notes and
    /// assignment drafts. Anything it does not recognise stays as literal text rather
    /// than disappearing.
    /// </summary>
    public static class MarkdownLite
    {
        /// <param name="markdown">
        /// False treats the input as plain prose: blank lines separate paragraphs and
        /// nothing is interpreted, so a .txt full of asterisks survives intact.
        /// </param>
        /// <param name="baseDir">
        /// Where relative image paths are resolved from — normally the folder the
        /// Markdown file itself lives in.
        /// </param>
        public static List<Block> Parse(string text, bool markdown, string baseDir = null)
        {
            var blocks = new List<Block>();
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            // The block currently being built. A plain line following a list item or a
            // quote continues it rather than starting something new — hard-wrapped
            // Markdown is normal, and splitting it produces a mangled document.
            BlockKind openKind = BlockKind.Paragraph;
            int openLevel = 0;
            var open = new StringBuilder();

            void Flush()
            {
                if (open.Length == 0) return;
                var joined = open.ToString().Trim();
                open.Clear();
                if (joined.Length == 0) return;

                var b = new Block { Kind = openKind, ListLevel = openLevel };
                if (markdown) b.Spans.AddRange(Inline(joined));
                else b.Spans.Add(new Span(joined));
                blocks.Add(b);

                openKind = BlockKind.Paragraph;
                openLevel = 0;
            }

            void Start(BlockKind kind, int level, string body)
            {
                Flush();
                openKind = kind;
                openLevel = level;
                open.Append(body);
            }

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();

                if (trimmed.Length == 0) { Flush(); continue; }

                if (!markdown) { Append(open, trimmed); continue; }

                // fenced code
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    Flush();
                    var code = new StringBuilder();
                    i++;
                    while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    {
                        code.AppendLine(lines[i]);
                        i++;
                    }
                    blocks.Add(new Block { Kind = BlockKind.Code, RawText = code.ToString().TrimEnd('\n') });
                    continue;
                }

                // table: a pipe row whose next line is the dashed separator
                if (trimmed.IndexOf('|') >= 0 && i + 1 < lines.Length && IsSeparatorRow(lines[i + 1]))
                {
                    Flush();
                    var table = ReadTable(lines, ref i);
                    if (table != null) { blocks.Add(table); continue; }
                }

                if (trimmed == "---" || trimmed == "***" || trimmed == "___")
                {
                    Flush();
                    blocks.Add(new Block { Kind = BlockKind.Rule });
                    continue;
                }

                // a line that is nothing but an image
                var img = LoneImage(trimmed);
                if (img != null)
                {
                    Flush();
                    blocks.Add(new Block { Kind = BlockKind.Image, ImagePath = Resolve(img, baseDir) });
                    continue;
                }

                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    int level = 0;
                    while (level < trimmed.Length && trimmed[level] == '#') level++;
                    if (level <= 6 && level < trimmed.Length && trimmed[level] == ' ')
                    {
                        Flush();
                        var kind = level == 1 ? BlockKind.Heading1
                                 : level == 2 ? BlockKind.Heading2 : BlockKind.Heading3;
                        var b = new Block { Kind = kind };
                        b.Spans.AddRange(Inline(trimmed.Substring(level + 1).Trim()));
                        blocks.Add(b);
                        continue;
                    }
                }

                if (trimmed.StartsWith("> ", StringComparison.Ordinal) || trimmed == ">")
                {
                    Start(BlockKind.Quote, 0, trimmed.Length > 1 ? trimmed.Substring(2) : "");
                    continue;
                }

                var bullet = BulletBody(trimmed);
                if (bullet != null) { Start(BlockKind.Bullet, IndentLevel(line), bullet); continue; }

                var numbered = NumberBody(trimmed);
                if (numbered != null) { Start(BlockKind.Number, IndentLevel(line), numbered); continue; }

                // Anything else continues whatever is open.
                Append(open, trimmed);
            }

            Flush();
            return blocks;
        }

        static void Append(StringBuilder sb, string line)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(line);
        }

        static string Resolve(string path, string baseDir)
        {
            try
            {
                if (Path.IsPathRooted(path) || string.IsNullOrEmpty(baseDir)) return path;
                return Path.GetFullPath(Path.Combine(baseDir, path));
            }
            catch { return path; }
        }

        /// <summary>Recognises a line that is only an image, e.g. ![caption](shot.png).</summary>
        static string LoneImage(string t)
        {
            if (!t.StartsWith("![", StringComparison.Ordinal) || !t.EndsWith(")", StringComparison.Ordinal))
                return null;
            var close = t.IndexOf("](", StringComparison.Ordinal);
            if (close < 0) return null;
            var path = t.Substring(close + 2, t.Length - close - 3).Trim();
            var space = path.IndexOf(" \"", StringComparison.Ordinal);   // drop a title if present
            if (space > 0) path = path.Substring(0, space).Trim();
            return path.Length == 0 ? null : path;
        }

        // ------------------------------------------------------------------
        //  tables
        // ------------------------------------------------------------------
        static bool IsSeparatorRow(string line)
        {
            var cells = SplitRow(line);
            if (cells.Count == 0) return false;
            foreach (var c in cells)
            {
                var t = c.Trim();
                if (t.Length == 0) return false;
                bool sawDash = false;
                for (int i = 0; i < t.Length; i++)
                {
                    if (t[i] == '-') sawDash = true;
                    else if (t[i] != ':') return false;
                }
                if (!sawDash) return false;
            }
            return true;
        }

        static List<string> SplitRow(string line)
        {
            var t = line.Trim();
            if (t.StartsWith("|", StringComparison.Ordinal)) t = t.Substring(1);
            if (t.EndsWith("|", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 1);

            var cells = new List<string>();
            var cur = new StringBuilder();
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == '\\' && i + 1 < t.Length && t[i + 1] == '|') { cur.Append('|'); i++; continue; }
                if (t[i] == '|') { cells.Add(cur.ToString().Trim()); cur.Clear(); continue; }
                cur.Append(t[i]);
            }
            cells.Add(cur.ToString().Trim());
            return cells;
        }

        static Block ReadTable(string[] lines, ref int i)
        {
            var headerCells = SplitRow(lines[i]);
            var aligns = new List<CellAlign>();
            foreach (var sep in SplitRow(lines[i + 1]))
            {
                var t = sep.Trim();
                bool left = t.StartsWith(":", StringComparison.Ordinal);
                bool right = t.EndsWith(":", StringComparison.Ordinal);
                aligns.Add(left && right ? CellAlign.Center : right ? CellAlign.Right : CellAlign.Left);
            }

            var table = new Block { Kind = BlockKind.Table };
            table.Aligns.AddRange(aligns);

            // A header of entirely empty cells is a layout table, not a titled one.
            table.HeaderRow = headerCells.Exists(c => c.Length > 0);
            if (table.HeaderRow) table.Rows.Add(MakeRow(headerCells));

            i += 2;
            while (i < lines.Length)
            {
                var line = lines[i];
                if (line.Trim().Length == 0 || line.IndexOf('|') < 0) break;
                table.Rows.Add(MakeRow(SplitRow(line)));
                i++;
            }
            i--;   // the loop that called us will move on

            return table.Rows.Count > 0 ? table : null;
        }

        static TableRow MakeRow(List<string> cells)
        {
            var row = new TableRow();
            foreach (var c in cells) row.Cells.Add(Inline(c));
            return row;
        }

        // ------------------------------------------------------------------
        static int IndentLevel(string line)
        {
            int spaces = 0;
            foreach (var c in line)
            {
                if (c == ' ') spaces++;
                else if (c == '\t') spaces += 4;
                else break;
            }
            return Math.Min(3, spaces / 2);
        }

        static string BulletBody(string t)
        {
            if (t.Length < 2) return null;
            var c = t[0];
            if ((c == '-' || c == '*' || c == '+') && t[1] == ' ') return t.Substring(2).Trim();
            return null;
        }

        static string NumberBody(string t)
        {
            int i = 0;
            while (i < t.Length && char.IsDigit(t[i])) i++;
            if (i == 0 || i + 1 >= t.Length) return null;
            if ((t[i] == '.' || t[i] == ')') && t[i + 1] == ' ') return t.Substring(i + 2).Trim();
            return null;
        }

        /// <summary>Handles **bold**, *italic*, _italic_, `code` and [text](url).</summary>
        public static List<Span> Inline(string s)
        {
            var spans = new List<Span>();
            var buf = new StringBuilder();
            bool bold = false, italic = false;

            void Flush()
            {
                if (buf.Length == 0) return;
                spans.Add(new Span(buf.ToString()) { Bold = bold, Italic = italic });
                buf.Clear();
            }

            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length && "*_`[\\".IndexOf(s[i + 1]) >= 0)
                {
                    buf.Append(s[i + 1]);
                    i++;
                    continue;
                }

                if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    Flush(); bold = !bold; i++;
                    continue;
                }

                if ((s[i] == '*' || s[i] == '_') && !(i + 1 < s.Length && s[i + 1] == s[i]))
                {
                    // an underscore inside a word is part of the word, not emphasis
                    bool wordInner = s[i] == '_' && i > 0 && i + 1 < s.Length &&
                                     char.IsLetterOrDigit(s[i - 1]) && char.IsLetterOrDigit(s[i + 1]);
                    if (!wordInner) { Flush(); italic = !italic; continue; }
                }

                if (s[i] == '`')
                {
                    var end = s.IndexOf('`', i + 1);
                    if (end > i)
                    {
                        Flush();
                        spans.Add(new Span(s.Substring(i + 1, end - i - 1)) { Code = true, Bold = bold, Italic = italic });
                        i = end;
                        continue;
                    }
                }

                if (s[i] == '[')
                {
                    var close = s.IndexOf(']', i + 1);
                    if (close > i && close + 1 < s.Length && s[close + 1] == '(')
                    {
                        var paren = s.IndexOf(')', close + 2);
                        if (paren > close)
                        {
                            Flush();
                            spans.Add(new Span(s.Substring(i + 1, close - i - 1))
                            {
                                Link = s.Substring(close + 2, paren - close - 2),
                                Bold = bold,
                                Italic = italic
                            });
                            i = paren;
                            continue;
                        }
                    }
                }

                buf.Append(s[i]);
            }

            Flush();
            if (spans.Count == 0) spans.Add(new Span(""));
            return spans;
        }
    }
}
