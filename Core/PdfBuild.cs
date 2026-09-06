using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace CompDash.Core
{
    public sealed class BuildReport
    {
        public bool Ok;
        public string OutPath;
        public int Pages;
        public long Bytes;
        public readonly List<string> Log = new List<string>();
        public readonly List<string> Failures = new List<string>();
    }

    /// <summary>Merges an ordered list of mixed files into one PDF.</summary>
    public static class PdfBuild
    {
        const double MmToPt = 72.0 / 25.4;

        public static XSize PaperPoints(PaperSize p)
        {
            switch (p)
            {
                case PaperSize.Letter: return PageSizeConverter.ToSize(PageSize.Letter);
                case PaperSize.Legal: return PageSizeConverter.ToSize(PageSize.Legal);
                case PaperSize.A3: return PageSizeConverter.ToSize(PageSize.A3);
                case PaperSize.A5: return PageSizeConverter.ToSize(PageSize.A5);
                case PaperSize.Tabloid: return PageSizeConverter.ToSize(PageSize.Tabloid);
                default: return PageSizeConverter.ToSize(PageSize.A4);
            }
        }

        /// <summary>
        /// Estimated page count without doing the work, for the "about N pages" readout.
        /// PDFs are exact; text is a guess from its line count; everything else is one page.
        /// </summary>
        public static int EstimatePages(DocItem it, PdfOptions opt)
        {
            switch (it.Kind)
            {
                case DocKind.Pdf:
                    return it.Pages > 0
                        ? Docs.ParseRange(it.Range, it.Pages).Count
                        : 1;
                case DocKind.Text:
                    try
                    {
                        // Mirror the real renderer's geometry, wrapping included, or the
                        // "about N pages" readout drifts on anything with long lines.
                        var size = opt.Paper == PaperSize.Auto ? PaperPoints(PaperSize.A4) : PaperPoints(opt.Paper);
                        double pw = size.Width, ph = size.Height;
                        if (opt.Orientation == Orient.Landscape) { var t = pw; pw = ph; ph = t; }

                        double margin = Math.Max(6, opt.MarginMm * MmToPt);
                        double fontSize = Math.Max(5, Math.Min(24, opt.TextFontSize));
                        double lineH = fontSize * 1.42;

                        // Consolas advances at roughly 0.55 em; close enough to count pages.
                        int cols = Math.Max(8, (int)((pw - margin * 2) / (fontSize * 0.55)));
                        int perPage = Math.Max(1, (int)((ph - margin * 2 - lineH * 2) / lineH));

                        int wrapped = 0;
                        foreach (var line in Docs.ReadText(it.Path).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                            wrapped += Math.Max(1, (int)Math.Ceiling(line.Length / (double)cols));

                        return Math.Max(1, (int)Math.Ceiling(wrapped / (double)perPage));
                    }
                    catch { return 1; }
                default:
                    return Math.Max(1, it.Pages);
            }
        }

        public static async Task<BuildReport> BuildAsync(IReadOnlyList<DocItem> items, PdfOptions opt,
                                                         Action<double, string> progress,
                                                         CancellationToken ct)
        {
            var rep = new BuildReport { OutPath = opt.OutPath };
            var temps = new List<string>();

            var doc = new PdfDocument();
            doc.Info.Title = string.IsNullOrWhiteSpace(opt.Title)
                ? Path.GetFileNameWithoutExtension(opt.OutPath) : opt.Title;
            if (!string.IsNullOrWhiteSpace(opt.Author)) doc.Info.Author = opt.Author;
            doc.Info.Creator = "CompDash";

            var work = items.Where(i => i.Include).OrderBy(i => i.Order).ToList();
            if (work.Count == 0)
            {
                rep.Failures.Add("Nothing selected to merge.");
                return rep;
            }

            for (int n = 0; n < work.Count; n++)
            {
                ct.ThrowIfCancellationRequested();
                var it = work[n];
                progress?.Invoke(n / (double)work.Count, it.Name);

                int before = doc.PageCount;
                try
                {
                    switch (it.Kind)
                    {
                        case DocKind.Image:
                            AddImagePage(doc, it.Path, opt);
                            break;

                        case DocKind.Text:
                            AddTextPages(doc, it.Path, opt);
                            break;

                        case DocKind.Pdf:
                            ImportPdf(doc, it.Path, it.Range);
                            break;

                        case DocKind.Html:
                            {
                                var tmp = await DocConvert.HtmlToPdfAsync(it.Path, ct).ConfigureAwait(false);
                                temps.Add(tmp);
                                ImportPdf(doc, tmp, it.Range);
                                break;
                            }

                        case DocKind.Office:
                            {
                                var tmp = await DocConvert.OfficeToPdfAsync(it.Path, ct).ConfigureAwait(false);
                                temps.Add(tmp);
                                ImportPdf(doc, tmp, it.Range);
                                break;
                            }

                        default:
                            throw new InvalidOperationException("unsupported file type");
                    }

                    int added = doc.PageCount - before;
                    rep.Log.Add($"{it.Name}  ->  {added} page{(added == 1 ? "" : "s")}");

                    if (opt.Bookmarks && added > 0)
                    {
                        try { doc.Outlines.Add(Path.GetFileNameWithoutExtension(it.Name), doc.Pages[before], true); }
                        catch { }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    var msg = it.Name + ": " + ex.Message;
                    rep.Failures.Add(msg);
                    rep.Log.Add("SKIPPED " + msg);
                }
            }

            if (doc.PageCount == 0)
            {
                rep.Failures.Add("No pages were produced, so nothing was written.");
                Cleanup(temps);
                return rep;
            }

            if (opt.PageNumbers) StampPageNumbers(doc);

            progress?.Invoke(0.97, "writing");
            var dir = Path.GetDirectoryName(opt.OutPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Save() freezes the document, so anything we want to report has to be read first.
            rep.Pages = doc.PageCount;
            doc.Save(opt.OutPath);

            rep.Ok = true;
            rep.Bytes = new FileInfo(opt.OutPath).Length;
            progress?.Invoke(1, "done");

            Cleanup(temps);
            return rep;
        }

        static void Cleanup(List<string> temps)
        {
            foreach (var t in temps) { try { if (File.Exists(t)) File.Delete(t); } catch { } }
        }

        // ------------------------------------------------------------------
        //  images
        // ------------------------------------------------------------------
        static void AddImagePage(PdfDocument doc, string path, PdfOptions opt)
        {
            using var img = XImage.FromFile(path);

            double margin = Math.Max(0, opt.MarginMm) * MmToPt;
            double pw, ph;

            if (opt.Paper == PaperSize.Auto)
            {
                // The page becomes the image's own printed size, plus the margin.
                pw = img.PointWidth + margin * 2;
                ph = img.PointHeight + margin * 2;

                // PDF pages top out at 200 inches; scale the whole thing down if we exceed it.
                const double maxPt = 200 * 72;
                var over = Math.Max(pw / maxPt, ph / maxPt);
                if (over > 1) { pw /= over; ph /= over; }
            }
            else
            {
                var size = PaperPoints(opt.Paper);
                pw = size.Width;
                ph = size.Height;

                bool wantLandscape =
                    opt.Orientation == Orient.Landscape ||
                    (opt.Orientation == Orient.Auto && img.PixelWidth > img.PixelHeight);

                if (wantLandscape) { var t = pw; pw = ph; ph = t; }
            }

            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(pw);
            page.Height = XUnit.FromPoint(ph);

            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawRectangle(XBrushes.White, 0, 0, pw, ph);

            double availW = Math.Max(1, pw - margin * 2);
            double availH = Math.Max(1, ph - margin * 2);
            double iw = Math.Max(1, img.PixelWidth);
            double ih = Math.Max(1, img.PixelHeight);

            double w, h;
            switch (opt.Fit)
            {
                case FitMode.Fill:
                    {
                        var s = Math.Max(availW / iw, availH / ih);
                        w = iw * s; h = ih * s;
                        gfx.IntersectClip(new XRect(margin, margin, availW, availH));
                        break;
                    }
                case FitMode.Actual:
                    {
                        w = img.PointWidth; h = img.PointHeight;
                        if (w > availW || h > availH)      // never let it run off the page
                        {
                            var s = Math.Min(availW / w, availH / h);
                            w *= s; h *= s;
                        }
                        break;
                    }
                default:
                    {
                        var s = Math.Min(availW / iw, availH / ih);
                        w = iw * s; h = ih * s;
                        break;
                    }
            }

            gfx.DrawImage(img, margin + (availW - w) / 2, margin + (availH - h) / 2, w, h);
        }

        // ------------------------------------------------------------------
        //  plain text and code
        // ------------------------------------------------------------------
        static void AddTextPages(PdfDocument doc, string path, PdfOptions opt)
        {
            var text = Docs.ReadText(path).Replace("\r\n", "\n").Replace('\r', '\n');
            var raw = text.Split('\n');

            var size = opt.Paper == PaperSize.Auto ? PaperPoints(PaperSize.A4) : PaperPoints(opt.Paper);
            double pw = size.Width, ph = size.Height;
            if (opt.Orientation == Orient.Landscape) { var t = pw; pw = ph; ph = t; }

            double margin = Math.Max(6, opt.MarginMm * MmToPt);
            double fontSize = Math.Max(5, Math.Min(24, opt.TextFontSize));
            double lineH = fontSize * 1.42;

            var font = MonoFont(fontSize);
            var headFont = new XFont("Segoe UI", Math.Max(7, fontSize * 0.95), XFontStyleEx.Bold);

            // Monospace, so one measurement gives the column width for the whole file.
            double charW;
            var probePage = new PdfDocument().AddPage();
            using (var probe = XGraphics.FromPdfPage(probePage))
                charW = Math.Max(0.1, probe.MeasureString("0000000000", font).Width / 10.0);

            double usableW = pw - margin * 2;
            int cols = Math.Max(8, (int)(usableW / charW));

            var lines = new List<string>();
            foreach (var lineRaw in raw)
            {
                var line = ExpandTabs(lineRaw);
                if (line.Length == 0) { lines.Add(""); continue; }
                for (int i = 0; i < line.Length; i += cols)
                    lines.Add(line.Substring(i, Math.Min(cols, line.Length - i)));
            }

            double headH = lineH * 2;
            int perPage = Math.Max(1, (int)((ph - margin * 2 - headH) / lineH));
            var title = Path.GetFileName(path);

            for (int start = 0; start < Math.Max(1, lines.Count); start += perPage)
            {
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(pw);
                page.Height = XUnit.FromPoint(ph);

                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawRectangle(XBrushes.White, 0, 0, pw, ph);

                gfx.DrawString(title, headFont, XBrushes.Black, margin, margin + fontSize);
                gfx.DrawLine(new XPen(XColors.LightGray, 0.6),
                             margin, margin + headH - lineH * 0.6,
                             pw - margin, margin + headH - lineH * 0.6);

                double y = margin + headH + fontSize;
                for (int i = start; i < Math.Min(lines.Count, start + perPage); i++)
                {
                    if (lines[i].Length > 0)
                        gfx.DrawString(lines[i], font, XBrushes.Black, margin, y);
                    y += lineH;
                }
            }
        }

        static XFont MonoFont(double size)
        {
            foreach (var name in new[] { "Consolas", "Cascadia Mono", "Courier New" })
            {
                try { return new XFont(name, size); } catch { }
            }
            return new XFont("Courier New", size);
        }

        static string ExpandTabs(string s)
        {
            if (s.IndexOf('\t') < 0) return s;
            var sb = new StringBuilder(s.Length + 16);
            foreach (var c in s)
            {
                if (c == '\t') { do { sb.Append(' '); } while (sb.Length % 4 != 0); }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  existing PDFs
        // ------------------------------------------------------------------
        static void ImportPdf(PdfDocument doc, string path, string range)
        {
            using var src = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            foreach (var idx in Docs.ParseRange(range, src.PageCount))
                if (idx >= 0 && idx < src.PageCount)
                    doc.AddPage(src.Pages[idx]);
        }

        public static int PageCountOf(string path)
        {
            try
            {
                using var src = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                return src.PageCount;
            }
            catch { return 0; }
        }

        // ------------------------------------------------------------------
        //  footer page numbers
        // ------------------------------------------------------------------
        static void StampPageNumbers(PdfDocument doc)
        {
            var font = new XFont("Segoe UI", 8);
            for (int i = 0; i < doc.PageCount; i++)
            {
                try
                {
                    var page = doc.Pages[i];
                    using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                    var label = (i + 1) + " / " + doc.PageCount;
                    var w = gfx.MeasureString(label, font).Width;
                    gfx.DrawString(label, font, XBrushes.Gray,
                                   page.Width.Point / 2 - w / 2, page.Height.Point - 14);
                }
                catch { /* a page we cannot draw on just goes unnumbered */ }
            }
        }
    }
}
