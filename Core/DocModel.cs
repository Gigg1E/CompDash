using System;
using System.Collections.Generic;
using System.Text;

namespace CompDash.Core
{
    public enum BlockKind
    {
        Paragraph, Heading1, Heading2, Heading3,
        Bullet, Number, Quote, Code, Rule, Image
    }

    /// <summary>A run of text with its formatting. Links keep their target.</summary>
    public sealed class Span
    {
        public string Text = "";
        public bool Bold, Italic, Code;
        public string Link;

        public Span() { }
        public Span(string text) { Text = text; }
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
        public string RawText;          // code blocks keep their text verbatim

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
        public static List<Block> Parse(string text, bool markdown)
        {
            var blocks = new List<Block>();
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var para = new List<string>();

            void FlushParagraph()
            {
                if (para.Count == 0) return;
                var joined = string.Join(" ", para).Trim();
                para.Clear();
                if (joined.Length == 0) return;

                var b = new Block { Kind = BlockKind.Paragraph };
                if (markdown) b.Spans.AddRange(Inline(joined));
                else b.Spans.Add(new Span(joined));
                blocks.Add(b);
            }

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();

                if (trimmed.Length == 0) { FlushParagraph(); continue; }

                if (!markdown) { para.Add(trimmed); continue; }

                // fenced code
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    FlushParagraph();
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

                if (trimmed == "---" || trimmed == "***" || trimmed == "___")
                {
                    FlushParagraph();
                    blocks.Add(new Block { Kind = BlockKind.Rule });
                    continue;
                }

                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    int level = 0;
                    while (level < trimmed.Length && trimmed[level] == '#') level++;
                    if (level <= 6 && level < trimmed.Length && trimmed[level] == ' ')
                    {
                        FlushParagraph();
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
                    FlushParagraph();
                    var b = new Block { Kind = BlockKind.Quote };
                    b.Spans.AddRange(Inline(trimmed.Length > 1 ? trimmed.Substring(2) : ""));
                    blocks.Add(b);
                    continue;
                }

                var bullet = BulletBody(trimmed);
                if (bullet != null)
                {
                    FlushParagraph();
                    var b = new Block { Kind = BlockKind.Bullet, ListLevel = IndentLevel(line) };
                    b.Spans.AddRange(Inline(bullet));
                    blocks.Add(b);
                    continue;
                }

                var numbered = NumberBody(trimmed);
                if (numbered != null)
                {
                    FlushParagraph();
                    var b = new Block { Kind = BlockKind.Number, ListLevel = IndentLevel(line) };
                    b.Spans.AddRange(Inline(numbered));
                    blocks.Add(b);
                    continue;
                }

                para.Add(trimmed);
            }

            FlushParagraph();
            return blocks;
        }

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
                // escaped marker
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
