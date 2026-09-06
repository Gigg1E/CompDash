using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;

namespace CompDash.Core
{
    public enum DocKind { Image, Pdf, Text, Html, Office, Unsupported }

    public enum PaperSize { Auto, A4, Letter, Legal, A3, A5, Tabloid }
    public enum Orient { Auto, Portrait, Landscape }
    public enum FitMode { Fit, Fill, Actual }

    /// <summary>One file waiting to become part of the merged PDF.</summary>
    public sealed class DocItem : INotifyPropertyChanged
    {
        public string Path { get; set; }
        public string Name => System.IO.Path.GetFileName(Path);
        public DocKind Kind { get; set; }
        public long Size { get; set; }

        int _order;
        public int Order { get => _order; set { _order = value; Raise(nameof(Order)); } }

        bool _include = true;
        public bool Include { get => _include; set { _include = value; Raise(nameof(Include)); } }

        string _status = "";
        public string Status { get => _status; set { _status = value; Raise(nameof(Status)); } }

        bool _problem;
        /// <summary>True when this file cannot be converted with what is installed.</summary>
        public bool HasProblem { get => _problem; set { _problem = value; Raise(nameof(HasProblem)); } }

        string _range = "";
        /// <summary>Page range for PDF sources, e.g. "1-3,7,10-". Empty means all pages.</summary>
        public string Range { get => _range; set { _range = value; Raise(nameof(Range)); } }

        public int Pages { get; set; }

        public string Sub => Docs.KindLabel(Kind) + "  ·  " + Fmt.Size(Size);
        public bool IsPdf => Kind == DocKind.Pdf;

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        public void RaiseAll() { Raise(nameof(Status)); Raise(nameof(Sub)); Raise(nameof(Order)); }
    }

    public sealed class PdfOptions
    {
        public PaperSize Paper = PaperSize.A4;
        public Orient Orientation = Orient.Auto;
        public FitMode Fit = FitMode.Fit;
        public double MarginMm = 10;

        public bool Bookmarks = true;
        public bool PageNumbers = false;
        public string Title = "";
        public string Author = "";

        public double TextFontSize = 9;
        public string OutPath = "";
        public bool OpenWhenDone = true;
    }

    public static class Docs
    {
        static readonly HashSet<string> ImageExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".ico" };

        static readonly HashSet<string> TextExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml",
          ".ini", ".cfg", ".conf", ".reg", ".ps1", ".psm1", ".bat", ".cmd", ".sh", ".sql",
          ".cs", ".js", ".ts", ".jsx", ".tsx", ".py", ".java", ".c", ".cpp", ".h", ".hpp",
          ".go", ".rs", ".rb", ".php", ".css", ".srt", ".vtt" };

        static readonly HashSet<string> HtmlExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".html", ".htm", ".mht", ".mhtml", ".svg" };

        static readonly HashSet<string> OfficeExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".doc", ".docx", ".rtf", ".odt", ".xls", ".xlsx", ".ods", ".ppt", ".pptx", ".odp", ".epub" };

        public static bool Accepts(string path)
        {
            var e = System.IO.Path.GetExtension(path);
            return ImageExt.Contains(e) || TextExt.Contains(e) || HtmlExt.Contains(e) ||
                   OfficeExt.Contains(e) || string.Equals(e, ".pdf", StringComparison.OrdinalIgnoreCase);
        }

        public static DocKind Detect(string path)
        {
            var e = System.IO.Path.GetExtension(path);
            if (string.Equals(e, ".pdf", StringComparison.OrdinalIgnoreCase)) return DocKind.Pdf;
            if (ImageExt.Contains(e)) return DocKind.Image;
            if (HtmlExt.Contains(e)) return DocKind.Html;
            if (TextExt.Contains(e)) return DocKind.Text;
            if (OfficeExt.Contains(e)) return DocKind.Office;
            return DocKind.Unsupported;
        }

        public static string KindLabel(DocKind k)
        {
            switch (k)
            {
                case DocKind.Image: return "image";
                case DocKind.Pdf: return "pdf";
                case DocKind.Text: return "text";
                case DocKind.Html: return "html";
                case DocKind.Office: return "office";
                default: return "unsupported";
            }
        }

        /// <summary>Explains, in the row, whether this file can actually be converted right now.</summary>
        public static string Availability(DocKind k)
        {
            switch (k)
            {
                case DocKind.Html:
                    return DocConvert.EdgePath != null ? "" : "needs Microsoft Edge";
                case DocKind.Office:
                    return DocConvert.LibreOfficePath != null ? "" : "needs LibreOffice";
                case DocKind.Unsupported:
                    return "unsupported file type";
                default:
                    return "";
            }
        }

        // ------------------------------------------------------------------
        //  ordering
        // ------------------------------------------------------------------

        /// <summary>
        /// Compares names the way a person reads them, so page2 sorts before page10.
        /// A plain string sort puts "10" before "2" and scrambles a numbered scan set.
        /// </summary>
        public static int NaturalCompare(string a, string b)
        {
            if (a == null) return b == null ? 0 : -1;
            if (b == null) return 1;

            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;

                    var na = a.Substring(si, i - si).TrimStart('0');
                    var nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length - nb.Length;
                    var c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    var c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i) - (b.Length - j);
        }

        // ------------------------------------------------------------------
        //  page ranges
        // ------------------------------------------------------------------

        /// <summary>
        /// Parses "1-3,7,10-" against a document of <paramref name="total"/> pages.
        /// Returns 0-based page indices in the order written, so "3,1" really does
        /// put page 3 first. An empty or unparseable range means every page.
        /// </summary>
        public static List<int> ParseRange(string range, int total)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(range))
            {
                for (int i = 0; i < total; i++) result.Add(i);
                return result;
            }

            foreach (var partRaw in range.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var part = partRaw.Trim();
                if (part.Length == 0) continue;

                var dash = part.IndexOf('-');
                if (dash < 0)
                {
                    if (int.TryParse(part, out var single) && single >= 1 && single <= total)
                        result.Add(single - 1);
                    continue;
                }

                var lo = part.Substring(0, dash).Trim();
                var hi = part.Substring(dash + 1).Trim();
                int from = lo.Length == 0 ? 1 : (int.TryParse(lo, out var f) ? f : 1);
                int to = hi.Length == 0 ? total : (int.TryParse(hi, out var t) ? t : total);

                from = Math.Max(1, Math.Min(total, from));
                to = Math.Max(1, Math.Min(total, to));

                if (from <= to) for (int i = from; i <= to; i++) result.Add(i - 1);
                else for (int i = from; i >= to; i--) result.Add(i - 1);
            }

            if (result.Count == 0)
                for (int i = 0; i < total; i++) result.Add(i);
            return result;
        }

        /// <summary>Reads a text file, coping with whatever encoding it turns out to be.</summary>
        public static string ReadText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch { return Encoding.Default.GetString(bytes); }
        }
    }
}
