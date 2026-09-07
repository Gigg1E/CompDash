using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CompDash.Core;
using WpfBlock = System.Windows.Documents.Block;
using WpfTable = System.Windows.Documents.Table;
using WpfTableRow = System.Windows.Documents.TableRow;
using WpfTableCell = System.Windows.Documents.TableCell;
using CoreBlock = CompDash.Core.Block;
using CoreSpan = CompDash.Core.Span;

namespace CompDash
{
    /// <summary>
    /// A paginated preview of what the Documents tab is about to produce.
    ///
    /// It renders from the same <see cref="CoreBlock"/> list the Word writer consumes, at the
    /// real page geometry, so a mis-parsed table or a missing image shows up here rather than
    /// after a build. It is a preview, not the writer: line breaking is WPF's rather than
    /// Word's, so page counts can differ by one on a full page.
    /// </summary>
    public partial class MainWindow
    {
        const double DipPerInch = 96.0;
        const double DipPerPoint = 96.0 / 72.0;

        DispatcherTimer _previewTimer;
        int _previewPage;
        int _previewPages;
        bool _previewWhole;
        readonly Dictionary<string, BitmapSource> _imageCache = new Dictionary<string, BitmapSource>();

        void InitPreview()
        {
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            _previewTimer.Tick += (a, b) => { _previewTimer.Stop(); RenderPreview(); };
        }

        /// <summary>Ask for a redraw soon. Safe to call from every option handler.</summary>
        void QueuePreview()
        {
            if (!_pdfReady || _previewTimer == null) return;
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        void OnPreviewScope(object s, RoutedEventArgs e)
        {
            if (!_pdfReady) return;
            _previewWhole = ReferenceEquals(s, ChipScopeAll);
            ChipScopeFile.IsChecked = !_previewWhole;
            ChipScopeAll.IsChecked = _previewWhole;
            _previewPage = 0;
            RenderPreview();
        }

        void OnPreviewPrev(object s, RoutedEventArgs e) => GoToPage(_previewPage - 1);
        void OnPreviewNext(object s, RoutedEventArgs e) => GoToPage(_previewPage + 1);

        void GoToPage(int page)
        {
            if (_previewPages <= 0) return;
            _previewPage = Math.Max(0, Math.Min(_previewPages - 1, page));
            PreviewPage.PageNumber = _previewPage;
            UpdatePageChrome();
        }

        void UpdatePageChrome()
        {
            PreviewPageNo.Text = _previewPages > 0 ? $"{_previewPage + 1} / {_previewPages}" : "—";
            BtnPrevPage.IsEnabled = _previewPage > 0;
            BtnNextPage.IsEnabled = _previewPage < _previewPages - 1;
        }

        // ==================================================================
        //  the render pass
        // ==================================================================
        void RenderPreview()
        {
            if (!_pdfReady || PreviewPage == null) return;

            CollectPdf();
            CollectDocx();

            var items = _previewWhole
                ? _docs.Where(x => x.Include && !x.HasProblem).OrderBy(x => x.Order).ToList()
                : new List<DocItem>();

            if (!_previewWhole)
            {
                var sel = DocList.SelectedItem as DocItem;
                if (sel != null) items.Add(sel);
            }

            if (items.Count == 0)
            {
                PreviewPage.DocumentPaginator = null;
                PreviewBox.Visibility = Visibility.Collapsed;
                PreviewEmpty.Visibility = Visibility.Visible;
                PreviewEmpty.Text = _docs.Count == 0
                    ? "add some files to see how they will come out"
                    : "select a file to preview it";
                _previewPages = 0;
                PreviewCaption.Text = "";
                UpdatePageChrome();
                return;
            }

            PreviewBusy.Visibility = Visibility.Visible;
            try
            {
                var doc = BuildFlow(items);
                var paginator = ((IDocumentPaginatorSource)doc).DocumentPaginator;
                paginator.PageSize = new Size(doc.PageWidth, doc.PageHeight);
                paginator.ComputePageCount();

                _previewPages = Math.Max(1, paginator.PageCount);
                _previewPage = Math.Max(0, Math.Min(_previewPages - 1, _previewPage));

                PreviewPage.DocumentPaginator = paginator;
                PreviewPage.PageNumber = _previewPage;

                PreviewBox.Visibility = Visibility.Visible;
                PreviewEmpty.Visibility = Visibility.Collapsed;
                PreviewCaption.Text = Caption(items);
            }
            catch (Exception ex)
            {
                PreviewPage.DocumentPaginator = null;
                PreviewBox.Visibility = Visibility.Collapsed;
                PreviewEmpty.Visibility = Visibility.Visible;
                PreviewEmpty.Text = "could not preview this: " + ex.Message;
                _previewPages = 0;
            }
            finally
            {
                PreviewBusy.Visibility = Visibility.Collapsed;
                UpdatePageChrome();
            }
        }

        string Caption(List<DocItem> items)
        {
            var what = _previewWhole
                ? items.Count + (items.Count == 1 ? " file" : " files")
                : items[0].Name;

            if (_target == MergeTarget.Docx)
                return $"{what}  ·  Word  ·  {_docxOpt.Paper}, {_docxOpt.MarginIn:0.##}\" margins, " +
                       $"{_docxOpt.FontName} {_docxOpt.FontSize:0.#}pt";

            var paper = _pdf.Paper == PaperSize.Auto ? "page per image" : _pdf.Paper.ToString();
            return $"{what}  ·  PDF  ·  {paper}, {_pdf.MarginMm:0}mm margins";
        }

        // ==================================================================
        //  page geometry
        // ==================================================================
        FlowDocument NewFlowDocument(out double contentWidth, out double contentHeight)
        {
            double pw, ph, pad, fontDip, lineHeight;
            string font;

            if (_target == MergeTarget.Docx)
            {
                PaperInches(_docxOpt.Paper, out var wIn, out var hIn);
                pw = wIn * DipPerInch;
                ph = hIn * DipPerInch;
                pad = _docxOpt.MarginIn * DipPerInch;
                font = _docxOpt.FontName;
                fontDip = _docxOpt.FontSize * DipPerPoint;
                // Word's "double" is two single lines; a single line runs about 1.15em.
                lineHeight = fontDip * 1.15 * Math.Max(1, _docxOpt.LineSpacing);
            }
            else
            {
                var paper = _pdf.Paper == PaperSize.Auto ? PaperSize.A4 : _pdf.Paper;
                PaperInches(paper, out var wIn, out var hIn);
                if (_pdf.Orientation == Orient.Landscape) { var t = wIn; wIn = hIn; hIn = t; }
                pw = wIn * DipPerInch;
                ph = hIn * DipPerInch;
                pad = Math.Max(6, _pdf.MarginMm * DipPerInch / 25.4);
                font = "Consolas";
                fontDip = _pdf.TextFontSize * DipPerPoint;
                lineHeight = fontDip * 1.42;
            }

            contentWidth = Math.Max(24, pw - pad * 2);
            contentHeight = Math.Max(24, ph - pad * 2);

            return new FlowDocument
            {
                PageWidth = pw,
                PageHeight = ph,
                PagePadding = new Thickness(pad),
                ColumnWidth = pw,                 // force a single column
                ColumnGap = 0,
                FontFamily = new FontFamily(font),
                FontSize = fontDip,
                Foreground = Brushes.Black,
                Background = Brushes.White,
                LineHeight = lineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextAlignment = TextAlignment.Left
            };
        }

        static void PaperInches(PaperSize p, out double w, out double h)
        {
            switch (p)
            {
                case PaperSize.A4: w = 8.27; h = 11.69; break;
                case PaperSize.Legal: w = 8.5; h = 14; break;
                case PaperSize.A3: w = 11.69; h = 16.54; break;
                case PaperSize.A5: w = 5.83; h = 8.27; break;
                case PaperSize.Tabloid: w = 11; h = 17; break;
                default: w = 8.5; h = 11; break;
            }
        }

        // ==================================================================
        //  building the document
        // ==================================================================
        FlowDocument BuildFlow(List<DocItem> items)
        {
            var doc = NewFlowDocument(out var contentW, out var contentH);
            bool word = _target == MergeTarget.Docx;

            // The title block sits above the first file's content, not on its own page.
            if (word && _docxOpt.Style != AcademicStyle.Plain &&
                (_previewWhole || items[0].Order == 1))
            {
                AddTitleBlock(doc);
            }

            bool firstFile = true;
            foreach (var it in items)
            {
                bool breakBefore = !firstFile && (word ? _docxOpt.PageBreakBetweenFiles : true);
                int before = doc.Blocks.Count;

                switch (it.Kind)
                {
                    case DocKind.Text:
                        if (word) AddBlocks(doc, ReadBlocksFor(it), contentW, contentH);
                        else AddMonospace(doc, it);
                        break;

                    case DocKind.Image:
                        AddImageBlock(doc, it.Path, contentW, contentH);
                        break;

                    default:
                        AddPassThroughCard(doc, it);
                        break;
                }

                if (doc.Blocks.Count > before)
                {
                    if (breakBefore)
                    {
                        var start = doc.Blocks.ElementAt(before);
                        start.BreakPageBefore = true;
                    }
                    firstFile = false;
                }
            }

            if (doc.Blocks.Count == 0)
                doc.Blocks.Add(Note("nothing to show for this file"));

            return doc;
        }

        List<CoreBlock> ReadBlocksFor(DocItem it)
        {
            var ext = Path.GetExtension(it.Path);
            bool md = ext.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                      ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase) ||
                      _docxOpt.TreatTxtAsMarkdown;
            return MarkdownLite.Parse(Docs.ReadText(it.Path), md, Path.GetDirectoryName(it.Path));
        }

        bool AddTitleBlock(FlowDocument doc)
        {
            var header = new[] { _docxOpt.StudentName, _docxOpt.Instructor, _docxOpt.Course, _docxOpt.DateLine }
                .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var hasTitle = !string.IsNullOrWhiteSpace(_docxOpt.TitleText);
            if (header.Count == 0 && !hasTitle) return false;

            var align = _docxOpt.Style == AcademicStyle.APA ? TextAlignment.Center : TextAlignment.Left;
            foreach (var line in header)
                doc.Blocks.Add(new Paragraph(new Run(line)) { TextAlignment = align, Margin = new Thickness(0) });

            if (hasTitle)
                doc.Blocks.Add(new Paragraph(new Run(_docxOpt.TitleText))
                {
                    TextAlignment = TextAlignment.Center,
                    FontWeight = _docxOpt.Style == AcademicStyle.APA ? FontWeights.Bold : FontWeights.Normal,
                    Margin = new Thickness(0)
                });

            return true;
        }

        void AddBlocks(FlowDocument doc, List<CoreBlock> blocks, double contentW, double contentH)
        {
            bool academic = _docxOpt.Style != AcademicStyle.Plain;
            double indent = _docxOpt.FirstLineIndent && academic ? 0.5 * DipPerInch : 0;

            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];

                switch (b.Kind)
                {
                    case BlockKind.Heading1:
                    case BlockKind.Heading2:
                    case BlockKind.Heading3:
                        {
                            var p = new Paragraph { Margin = new Thickness(0, academic ? 0 : 8, 0, 0) };
                            p.Inlines.AddRange(Inlines(b.Spans));
                            p.FontWeight = FontWeights.Bold;
                            if (b.Kind == BlockKind.Heading3) p.FontStyle = FontStyles.Italic;
                            if (!academic)
                            {
                                p.FontSize = doc.FontSize * (b.Kind == BlockKind.Heading1 ? 1.35
                                                           : b.Kind == BlockKind.Heading2 ? 1.2 : 1.0);
                                p.Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0x54, 0x96));
                            }
                            else if (b.Kind == BlockKind.Heading1)
                                p.TextAlignment = TextAlignment.Center;
                            doc.Blocks.Add(p);
                            break;
                        }

                    case BlockKind.Bullet:
                    case BlockKind.Number:
                        {
                            // Gather the whole run of list items so they share one list.
                            var kind = b.Kind;
                            var list = new List
                            {
                                MarkerStyle = kind == BlockKind.Bullet
                                    ? TextMarkerStyle.Disc : TextMarkerStyle.Decimal,
                                Margin = new Thickness(0.25 * DipPerInch, 0, 0, 0),
                                Padding = new Thickness(0)
                            };
                            while (i < blocks.Count && blocks[i].Kind == kind)
                            {
                                var para = new Paragraph { Margin = new Thickness(0) };
                                para.Inlines.AddRange(Inlines(blocks[i].Spans));
                                list.ListItems.Add(new ListItem(para));
                                i++;
                            }
                            i--;
                            doc.Blocks.Add(list);
                            break;
                        }

                    case BlockKind.Quote:
                        {
                            var p = new Paragraph
                            {
                                Margin = new Thickness(0.5 * DipPerInch, 0, 0, 0),
                                FontStyle = FontStyles.Italic
                            };
                            p.Inlines.AddRange(Inlines(b.Spans));
                            doc.Blocks.Add(p);
                            break;
                        }

                    case BlockKind.Code:
                        {
                            var p = new Paragraph
                            {
                                FontFamily = new FontFamily("Consolas"),
                                FontSize = Math.Max(7, doc.FontSize - 2),
                                Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)),
                                Margin = new Thickness(0.25 * DipPerInch, 2, 0, 2),
                                Padding = new Thickness(4),
                                LineHeight = Math.Max(8, doc.FontSize * 1.2)
                            };
                            var lines = (b.RawText ?? "").Split('\n');
                            for (int k = 0; k < lines.Length; k++)
                            {
                                if (k > 0) p.Inlines.Add(new LineBreak());
                                p.Inlines.Add(new Run(lines[k].TrimEnd()));
                            }
                            doc.Blocks.Add(p);
                            break;
                        }

                    case BlockKind.Rule:
                        doc.Blocks.Add(new Paragraph
                        {
                            BorderBrush = Brushes.Silver,
                            BorderThickness = new Thickness(0, 0, 0, 1),
                            Margin = new Thickness(0, 4, 0, 4),
                            LineHeight = 2
                        });
                        break;

                    case BlockKind.Image:
                        AddImageBlock(doc, b.ImagePath, contentW, contentH);
                        break;

                    case BlockKind.Table:
                        doc.Blocks.Add(BuildTable(b, doc));
                        break;

                    default:
                        {
                            var p = new Paragraph { Margin = new Thickness(0), TextIndent = indent };
                            p.Inlines.AddRange(Inlines(b.Spans));
                            doc.Blocks.Add(p);
                            break;
                        }
                }
            }
        }

        IEnumerable<Inline> Inlines(List<CoreSpan> spans)
        {
            foreach (var s in spans)
            {
                if (string.IsNullOrEmpty(s.Text)) continue;
                var run = new Run(s.Text);
                if (s.Bold) run.FontWeight = FontWeights.Bold;
                if (s.Italic) run.FontStyle = FontStyles.Italic;
                if (s.Code) run.FontFamily = new FontFamily("Consolas");
                if (s.Link != null)
                {
                    run.Foreground = new SolidColorBrush(Color.FromRgb(0x05, 0x63, 0xC1));
                    run.TextDecorations = TextDecorations.Underline;
                }
                yield return run;
            }
        }

        WpfTable BuildTable(CoreBlock b, FlowDocument doc)
        {
            var table = new WpfTable
            {
                CellSpacing = 0,
                Margin = new Thickness(0, 4, 0, 4)
            };

            int columns = b.Rows.Max(r => r.Cells.Count);
            for (int c = 0; c < columns; c++) table.Columns.Add(new TableColumn());

            var group = new TableRowGroup();
            table.RowGroups.Add(group);

            for (int r = 0; r < b.Rows.Count; r++)
            {
                bool header = b.HeaderRow && r == 0;
                var row = new WpfTableRow();

                for (int c = 0; c < columns; c++)
                {
                    var spans = c < b.Rows[r].Cells.Count ? b.Rows[r].Cells[c] : new List<CoreSpan>();
                    var para = new Paragraph
                    {
                        Margin = new Thickness(0),
                        LineHeight = doc.FontSize * 1.2,
                        FontWeight = header ? FontWeights.Bold : FontWeights.Normal
                    };
                    para.Inlines.AddRange(Inlines(spans));

                    var align = c < b.Aligns.Count ? b.Aligns[c] : CellAlign.Left;
                    para.TextAlignment = align == CellAlign.Center ? TextAlignment.Center
                                       : align == CellAlign.Right ? TextAlignment.Right
                                       : TextAlignment.Left;

                    row.Cells.Add(new WpfTableCell(para)
                    {
                        BorderBrush = Brushes.Silver,
                        BorderThickness = new Thickness(0.5),
                        Padding = new Thickness(4, 2, 4, 2)
                    });
                }

                group.Rows.Add(row);
            }

            return table;
        }

        void AddImageBlock(FlowDocument doc, string path, double contentW, double contentH)
        {
            var bmp = LoadPreviewImage(path);
            if (bmp == null)
            {
                doc.Blocks.Add(Note("could not read the image: " + Path.GetFileName(path ?? "")));
                return;
            }

            double w = bmp.PixelWidth, h = bmp.PixelHeight;
            double dpiX = bmp.DpiX > 1 ? bmp.DpiX : 96, dpiY = bmp.DpiY > 1 ? bmp.DpiY : 96;

            double wDip, hDip;
            if (_target == MergeTarget.Docx || _pdf.Fit == FitMode.Actual)
            {
                wDip = w / dpiX * DipPerInch;
                hDip = h / dpiY * DipPerInch;
            }
            else
            {
                wDip = w; hDip = h;
            }

            // Fill crops to the page; everything else fits inside it.
            double scale = _target == MergeTarget.Pdf && _pdf.Fit == FitMode.Fill
                ? Math.Max(contentW / wDip, contentH / hDip)
                : Math.Min(1.0, Math.Min(contentW / wDip, contentH / hDip));
            if (_target == MergeTarget.Pdf && _pdf.Fit == FitMode.Fit)
                scale = Math.Min(contentW / wDip, contentH / hDip);

            var image = new Image
            {
                Source = bmp,
                Width = Math.Max(8, wDip * scale),
                Height = Math.Max(8, hDip * scale),
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            // Vertical alignment must be explicit: a Stretch-aligned child of a
            // BlockUIContainer expands to the whole page and pushes a blank page after it.
            var host = new Border
            {
                Child = image,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                ClipToBounds = true,
                MaxWidth = contentW,
                MaxHeight = contentH
            };

            doc.Blocks.Add(new BlockUIContainer(host) { Margin = new Thickness(0, 4, 0, 4) });
        }

        void AddMonospace(FlowDocument doc, DocItem it)
        {
            var header = new Paragraph(new Run(it.Name))
            {
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0, 0, 0, 0.6),
                Margin = new Thickness(0, 0, 0, 6)
            };
            doc.Blocks.Add(header);

            string text;
            try { text = Docs.ReadText(it.Path); }
            catch (Exception ex) { doc.Blocks.Add(Note("could not read: " + ex.Message)); return; }

            var p = new Paragraph { Margin = new Thickness(0) };
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) p.Inlines.Add(new LineBreak());
                p.Inlines.Add(new Run(lines[i].TrimEnd()));
            }
            doc.Blocks.Add(p);
        }

        /// <summary>
        /// PDFs and Office files are handed to another converter untouched, so there is
        /// nothing to render. Say what will happen instead of showing a blank page.
        /// </summary>
        void AddPassThroughCard(FlowDocument doc, DocItem it)
        {
            string what;
            switch (it.Kind)
            {
                case DocKind.Pdf:
                    var pages = it.Pages > 0 ? Docs.ParseRange(it.Range, it.Pages).Count : 0;
                    what = string.IsNullOrWhiteSpace(it.Range)
                        ? $"all {it.Pages} pages copied across unchanged"
                        : $"pages {it.Range} — {pages} of {it.Pages} — copied across unchanged";
                    break;
                case DocKind.Office:
                    what = Docs.IsDocx(it.Path) && _target == MergeTarget.Docx
                        ? "read straight into the document, formatting and images included"
                        : "converted by LibreOffice, then brought in";
                    break;
                case DocKind.Html:
                    what = _target == MergeTarget.Docx
                        ? "converted by LibreOffice, then brought in"
                        : "rendered by headless Edge, then brought in";
                    break;
                default:
                    what = "not something this can take in";
                    break;
            }

            doc.Blocks.Add(new Paragraph(new Run(it.Name))
            {
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 6)
            });
            doc.Blocks.Add(Note(what));
            doc.Blocks.Add(Note("There is nothing to preview for this one — it is not being " +
                                "re-typeset, so it will look exactly as it does now."));
        }

        static Paragraph Note(string text) => new Paragraph(new Run(text))
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Foreground = Brushes.Gray,
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 0, 0, 4),
            LineHeight = 15
        };

        // ==================================================================
        //  self test
        // ==================================================================
        /// <summary>
        /// Renders every queued file through the real preview path and reports what came out,
        /// so a file that silently produces a blank page is visible without opening the window.
        /// </summary>
        internal void SelfTestPreview(List<string> problems, List<string> report)
        {
            foreach (var scope in new[] { false, true })
            {
                _previewWhole = scope;
                ChipScopeFile.IsChecked = !scope;
                ChipScopeAll.IsChecked = scope;

                foreach (var it in _docs.ToList())
                {
                    if (!scope) DocList.SelectedItem = it;
                    _previewPage = 0;

                    try
                    {
                        RenderPreview();

                        var label = (scope ? "whole" : it.Name);
                        if (_previewPages <= 0)
                        {
                            problems.Add("Preview: " + label + " rendered no pages");
                            continue;
                        }

                        // Look inside the document to be sure the content really landed.
                        var items = scope
                            ? _docs.Where(x => x.Include && !x.HasProblem).OrderBy(x => x.Order).ToList()
                            : new List<DocItem> { it };
                        var flow = BuildFlow(items);
                        int tables = 0, images = 0, paras = 0, lists = 0, chars = 0;
                        foreach (var el in Walk(flow.Blocks))
                        {
                            if (el is WpfTable) tables++;
                            else if (el is BlockUIContainer) images++;
                            else if (el is List) lists++;
                            else if (el is Paragraph p)
                            {
                                paras++;
                                foreach (var r in p.Inlines.OfType<Run>()) chars += (r.Text ?? "").Length;
                            }
                        }

                        report.Add($"Preview[{(scope ? "all" : "one")}] {label}: {_previewPages}p " +
                                   $"{paras} para {lists} list {tables} tbl {images} img {chars} chars");

                        if (scope) break;   // the whole-document render is the same for every row
                    }
                    catch (Exception ex)
                    {
                        problems.Add("Preview: " + it.Name + " threw " + ex.GetType().Name + " " + ex.Message);
                    }
                }
            }

            _previewWhole = false;
            ChipScopeFile.IsChecked = true;
            ChipScopeAll.IsChecked = false;
        }

        static IEnumerable<object> Walk(IEnumerable<WpfBlock> blocks)
        {
            foreach (var b in blocks)
            {
                yield return b;
                if (b is List list)
                    foreach (var li in list.ListItems)
                        foreach (var inner in Walk(li.Blocks)) yield return inner;
                else if (b is WpfTable t)
                    foreach (var g in t.RowGroups)
                        foreach (var r in g.Rows)
                            foreach (var c in r.Cells)
                                foreach (var inner in Walk(c.Blocks)) yield return inner;
            }
        }

        // ==================================================================
        //  images
        // ==================================================================
        BitmapSource LoadPreviewImage(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_imageCache.TryGetValue(path, out var cached)) return cached;

            BitmapSource bmp = null;
            try
            {
                if (File.Exists(path))
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bi.UriSource = new Uri(path);
                    bi.EndInit();
                    bi.Freeze();
                    bmp = bi;
                }
            }
            catch { bmp = null; }

            // WebP and friends need no WIC codec if ffmpeg can rewrite them as PNG.
            if (bmp == null && File.Exists(path) && Ff.FfmpegPath != null)
            {
                try
                {
                    var tmp = Path.Combine(Ff.TempDir,
                        "prevmd_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
                    var args = new List<string> { "-y", "-i", path, "-frames:v", "1", tmp };
                    if (Ff.QuietAsync(args, CancellationToken.None).GetAwaiter().GetResult() && File.Exists(tmp))
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                        bi.UriSource = new Uri(tmp);
                        bi.EndInit();
                        bi.Freeze();
                        bmp = bi;
                        try { File.Delete(tmp); } catch { }
                    }
                }
                catch { }
            }

            _imageCache[path] = bmp;
            return bmp;
        }
    }
}
