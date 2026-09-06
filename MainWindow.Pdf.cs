using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CompDash.Core;
using Microsoft.Win32;

namespace CompDash
{
    /// <summary>The PDF tab: order a pile of mixed files and merge them into one document.</summary>
    public partial class MainWindow
    {
        readonly ObservableCollection<DocItem> _docs = new ObservableCollection<DocItem>();
        readonly PdfOptions _pdf = new PdfOptions();
        readonly DocxOptions _docxOpt = new DocxOptions();
        MergeTarget _target = MergeTarget.Pdf;
        CancellationTokenSource _pdfCts;
        bool _pdfBusy;

        /// <summary>
        /// False until InitPdfTab runs. The combo boxes raise SelectionChanged while the XAML is
        /// still being parsed (their IsSelected="True" item), at which point the controls declared
        /// after them do not exist yet, so every option handler has to sit out until then.
        /// </summary>
        bool _pdfReady;
        Point _dragStart;
        DocItem _dragItem;

        void InitPdfTab()
        {
            _pdfReady = true;
            DocList.ItemsSource = _docs;
            DocConvert.Locate();

            PdfOutBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "merged.pdf");

            CollectPdf();
            CollectDocx();
            ApplyTarget();          // fills in the converter list for whichever target is current
            UpdateDocSummary();
        }

        // ==================================================================
        //  PDF or Word
        // ==================================================================
        void OnTargetChip(object s, RoutedEventArgs e)
        {
            if (!_pdfReady) return;
            var chip = (System.Windows.Controls.Primitives.ToggleButton)s;
            _target = (MergeTarget)Enum.Parse(typeof(MergeTarget), (string)chip.Tag);
            ApplyTarget();
        }

        void ApplyTarget()
        {
            bool word = _target == MergeTarget.Docx;

            ChipToPdf.IsChecked = !word;
            ChipToDocx.IsChecked = word;
            PdfOnlyPanel.Visibility = word ? Visibility.Collapsed : Visibility.Visible;
            DocxOnlyPanel.Visibility = word ? Visibility.Visible : Visibility.Collapsed;

            DocHeading.Text = word ? "Convert into one Word document" : "Merge into one PDF";
            DocSubHeading.Text = word
                ? "text, Markdown, images and Word files — in the order you number them"
                : "images, PDFs, text and web pages — in the order you number them";
            BtnBuildPdf.Content = word ? "Build Word document" : "Build PDF";

            // Keep the extension honest when the target changes.
            var path = PdfOutBox.Text.Trim();
            if (path.Length > 0)
            {
                var want = word ? ".docx" : ".pdf";
                var have = Path.GetExtension(path);
                if (!string.Equals(have, want, StringComparison.OrdinalIgnoreCase))
                {
                    var dir = Path.GetDirectoryName(path);
                    var stem = Path.GetFileNameWithoutExtension(path);
                    PdfOutBox.Text = string.IsNullOrEmpty(dir) ? stem + want : Path.Combine(dir, stem + want);
                }
            }

            var lines = new List<string>();
            if (word)
            {
                lines.Add("text, Markdown and images — built in");
                lines.Add(".docx — read directly");
                lines.Add(DocConvert.LibreOfficePath != null
                    ? ".doc, .odt, .rtf, HTML — LibreOffice"
                    : ".doc, .odt, .rtf, HTML — need LibreOffice:\n     winget install TheDocumentFoundation.LibreOffice");
                lines.Add("PDF cannot become Word — that needs layout reconstruction.");
                lines.Add("Google Docs: use File → Download → Microsoft Word instead of exporting text.");
            }
            else
            {
                lines.Add("images, PDF, text and code — built in");
                lines.Add(DocConvert.EdgePath != null
                    ? "HTML and SVG — Microsoft Edge"
                    : "HTML and SVG — needs Microsoft Edge (not found)");
                lines.Add(DocConvert.LibreOfficePath != null
                    ? "Word, Excel, PowerPoint — LibreOffice"
                    : "Word, Excel, PowerPoint — need LibreOffice:\n     winget install TheDocumentFoundation.LibreOffice");
            }
            PdfSupportText.Text = string.Join("\n", lines);

            // What a row can do depends on where it is going, so re-check every one.
            foreach (var it in _docs)
            {
                var why = Docs.Availability(it.Kind, _target, it.Path);
                it.HasProblem = why.Length > 0;
                it.Status = it.HasProblem ? why : PageLabel(it);
            }

            UpdateDocSummary();
        }

        void OnDocxOption(object s, SelectionChangedEventArgs e)
        {
            if (!_pdfReady) return;
            CollectDocx();
        }

        void OnDocxOptionToggle(object s, RoutedEventArgs e)
        {
            if (!_pdfReady) return;
            CollectDocx();
        }

        void CollectDocx()
        {
            if (!_pdfReady) return;

            _docxOpt.Style = ParseTag(StyleCombo, AcademicStyle.MLA);
            _docxOpt.FontName = TagText(DocxFontCombo, "Times New Roman");
            _docxOpt.FontSize = ParseNum(DocxSizeCombo, 12);
            _docxOpt.LineSpacing = ParseNum(DocxSpacingCombo, 2);
            _docxOpt.Paper = ParseTag(DocxPaperCombo, PaperSize.Letter);
            _docxOpt.MarginIn = ParseNum(DocxMarginCombo, 1);
            _docxOpt.FirstLineIndent = IndentCheck.IsChecked == true;
            _docxOpt.PageNumbers = DocxPageNumCheck.IsChecked == true;
            _docxOpt.PageBreakBetweenFiles = PageBreakCheck.IsChecked == true;
            _docxOpt.TreatTxtAsMarkdown = TxtMarkdownCheck.IsChecked == true;
            _docxOpt.StudentName = NameBox.Text;
            _docxOpt.Instructor = InstructorBox.Text;
            _docxOpt.Course = CourseBox.Text;
            _docxOpt.DateLine = DateBox.Text;
            _docxOpt.TitleText = EssayTitleBox.Text;
            _docxOpt.OutPath = PdfOutBox.Text.Trim();
            _docxOpt.OpenWhenDone = OpenWhenDoneCheck.IsChecked == true;
        }

        static string TagText(ComboBox c, string dflt) =>
            (c?.SelectedItem as ComboBoxItem)?.Tag as string ?? dflt;

        static double ParseNum(ComboBox c, double dflt)
        {
            var t = (c?.SelectedItem as ComboBoxItem)?.Tag as string;
            return double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : dflt;
        }

        // ==================================================================
        //  adding files
        // ==================================================================
        void OnPdfAddFiles(object s, RoutedEventArgs e)
        {
            var d = new OpenFileDialog
            {
                Multiselect = true,
                Title = "Add files to merge",
                Filter = "Everything supported|*.pdf;*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.gif;*.tif;*.tiff;*.webp;" +
                         "*.txt;*.md;*.log;*.csv;*.json;*.xml;*.html;*.htm;*.svg;" +
                         "*.doc;*.docx;*.rtf;*.odt;*.xls;*.xlsx;*.ppt;*.pptx" +
                         "|PDF|*.pdf|Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp" +
                         "|Documents|*.doc;*.docx;*.rtf;*.odt;*.xls;*.xlsx;*.ppt;*.pptx" +
                         "|Text|*.txt;*.md;*.log;*.csv;*.json;*.xml|All files|*.*"
            };
            if (d.ShowDialog(this) == true) AddDocs(d.FileNames, sortNew: true);
        }

        void OnPdfAddFolder(object s, RoutedEventArgs e)
        {
            var d = new OpenFolderDialog { Title = "Add every supported file in a folder" };
            if (d.ShowDialog(this) != true) return;
            try { AddDocs(Directory.GetFiles(d.FolderName), sortNew: true); }
            catch (Exception ex) { PdfLogLine("could not read folder: " + ex.Message); }
        }

        /// <param name="sortNew">Put the newly added files in natural name order as they arrive.</param>
        void AddDocs(IEnumerable<string> paths, bool sortNew)
        {
            var fresh = new List<DocItem>();

            foreach (var p in paths)
            {
                try
                {
                    if (Directory.Exists(p))
                    {
                        foreach (var f in Directory.GetFiles(p))
                            if (Docs.Accepts(f)) fresh.Add(MakeDoc(f));
                        continue;
                    }
                    if (!File.Exists(p) || !Docs.Accepts(p)) continue;
                    if (_docs.Any(x => string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
                    fresh.Add(MakeDoc(p));
                }
                catch { }
            }

            if (fresh.Count == 0) return;

            if (sortNew)
                fresh.Sort((a, b) => Docs.NaturalCompare(a.Name, b.Name));

            foreach (var it in fresh) _docs.Add(it);

            Renumber();
            if (DocList.SelectedItem == null) DocList.SelectedIndex = 0;
        }

        DocItem MakeDoc(string path)
        {
            var it = new DocItem
            {
                Path = path,
                Kind = Docs.Detect(path),
            };
            try { it.Size = new FileInfo(path).Length; } catch { }

            if (it.Kind == DocKind.Pdf)
            {
                it.Pages = PdfBuild.PageCountOf(path);
                if (it.Pages == 0) { it.Status = "unreadable PDF"; it.HasProblem = true; return it; }
            }
            else
            {
                it.Pages = PdfBuild.EstimatePages(it, _pdf);
            }

            var why = Docs.Availability(it.Kind, _target, it.Path);
            if (why.Length > 0) { it.Status = why; it.HasProblem = true; }
            else it.Status = PageLabel(it);

            return it;
        }

        string PageLabel(DocItem it)
        {
            var n = PdfBuild.EstimatePages(it, _pdf);
            var approx = it.Kind == DocKind.Pdf ? "" : "~";
            return approx + n + (n == 1 ? " page" : " pages");
        }

        void OnPdfClear(object s, RoutedEventArgs e)
        {
            _docs.Clear();
            UpdateDocSummary();
        }

        // ==================================================================
        //  ordering
        // ==================================================================

        /// <summary>The list is always stored in display order; Order is simply the row number.</summary>
        void Renumber()
        {
            for (int i = 0; i < _docs.Count; i++)
            {
                _docs[i].Order = i + 1;
                if (!_docs[i].HasProblem) _docs[i].Status = PageLabel(_docs[i]);
            }
            UpdateDocSummary();
        }

        void MoveTo(DocItem it, int newIndex)
        {
            var old = _docs.IndexOf(it);
            if (old < 0) return;
            newIndex = Math.Max(0, Math.Min(_docs.Count - 1, newIndex));
            if (newIndex == old) { Renumber(); return; }
            _docs.Move(old, newIndex);
            Renumber();
            DocList.SelectedItem = it;
        }

        static DocItem RowOf(object sender) => (sender as FrameworkElement)?.DataContext as DocItem;

        void OnRowUp(object s, RoutedEventArgs e)
        {
            var it = RowOf(s); if (it == null) return;
            MoveTo(it, _docs.IndexOf(it) - 1);
        }

        void OnRowDown(object s, RoutedEventArgs e)
        {
            var it = RowOf(s); if (it == null) return;
            MoveTo(it, _docs.IndexOf(it) + 1);
        }

        void OnRowRemove(object s, RoutedEventArgs e)
        {
            var it = RowOf(s); if (it == null) return;
            _docs.Remove(it);
            Renumber();
        }

        void OnPdfMoveUp(object s, RoutedEventArgs e)
        {
            if (DocList.SelectedItem is DocItem it) MoveTo(it, _docs.IndexOf(it) - 1);
        }

        void OnPdfMoveDown(object s, RoutedEventArgs e)
        {
            if (DocList.SelectedItem is DocItem it) MoveTo(it, _docs.IndexOf(it) + 1);
        }

        void OnPdfRemove(object s, RoutedEventArgs e)
        {
            if (!(DocList.SelectedItem is DocItem it)) return;
            var at = _docs.IndexOf(it);
            _docs.Remove(it);
            Renumber();
            if (_docs.Count > 0) DocList.SelectedIndex = Math.Min(at, _docs.Count - 1);
        }

        void OnPdfSortName(object s, RoutedEventArgs e) =>
            Reorder(_docs.OrderBy(x => x.Name, Comparer<string>.Create(Docs.NaturalCompare)).ToList());

        void OnPdfSortDate(object s, RoutedEventArgs e) =>
            Reorder(_docs.OrderBy(x =>
            {
                try { return File.GetLastWriteTimeUtc(x.Path); } catch { return DateTime.MaxValue; }
            }).ToList());

        void OnPdfReverse(object s, RoutedEventArgs e) => Reorder(_docs.Reverse().ToList());

        void Reorder(List<DocItem> ordered)
        {
            var sel = DocList.SelectedItem as DocItem;
            _docs.Clear();
            foreach (var it in ordered) _docs.Add(it);
            Renumber();
            if (sel != null) DocList.SelectedItem = sel;
        }

        // typing a position into the row's number box
        void OnOrderKey(object s, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            CommitOrder(s as TextBox);
            e.Handled = true;
        }

        void OnOrderCommit(object s, RoutedEventArgs e) => CommitOrder(s as TextBox);

        void CommitOrder(TextBox box)
        {
            var it = box?.DataContext as DocItem;
            if (it == null) return;

            if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wanted))
                MoveTo(it, wanted - 1);
            else
                box.Text = it.Order.ToString();   // put back what it was
        }

        void OnRangeCommit(object s, RoutedEventArgs e)
        {
            var it = (s as TextBox)?.DataContext as DocItem;
            if (it == null) return;
            if (!it.HasProblem) it.Status = PageLabel(it);
            UpdateDocSummary();
        }

        // ------------------------------------------------------------------
        //  drag a row to reorder
        // ------------------------------------------------------------------
        void OnDocPreviewDown(object s, MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(null);
            _dragItem = (e.OriginalSource as FrameworkElement)?.DataContext as DocItem;
        }

        void OnDocPreviewMove(object s, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null) return;

            var now = e.GetPosition(null);
            if (Math.Abs(now.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(now.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            // A text box or check box inside the row should keep the mouse, not start a drag.
            if (e.OriginalSource is TextBox || e.OriginalSource is System.Windows.Controls.Primitives.ToggleButton)
            { _dragItem = null; return; }

            var item = _dragItem;
            _dragItem = null;
            try { DragDrop.DoDragDrop(DocList, new DataObject(typeof(DocItem), item), DragDropEffects.Move); }
            catch { }
        }

        void OnDocDragOver(object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(DocItem))) e.Effects = DragDropEffects.Move;
            else if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        void OnDocDrop(object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(DocItem)))
            {
                var moved = (DocItem)e.Data.GetData(typeof(DocItem));
                var overItem = (e.OriginalSource as FrameworkElement)?.DataContext as DocItem;

                int target = overItem != null && overItem != moved
                    ? _docs.IndexOf(overItem)
                    : _docs.Count - 1;

                MoveTo(moved, target);
                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                AddDocs((string[])e.Data.GetData(DataFormats.FileDrop), sortNew: true);
                e.Handled = true;
            }
        }

        // ==================================================================
        //  options
        // ==================================================================
        void OnPdfOption(object s, SelectionChangedEventArgs e)
        {
            if (!_pdfReady) return;
            CollectPdf();
            UpdateDocSummary();
        }

        void OnPdfOptionToggle(object s, RoutedEventArgs e)
        {
            if (!_pdfReady) return;
            CollectPdf();
            UpdateDocSummary();
        }

        void OnPdfOptionSlider(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_pdfReady) return;
            CollectPdf();
            Renumber();          // text page counts move with the font size
        }

        void CollectPdf()
        {
            if (!_pdfReady) return;

            _pdf.Paper = ParseTag(PaperCombo, PaperSize.A4);
            _pdf.Orientation = ParseTag(OrientCombo, Orient.Auto);
            _pdf.Fit = ParseTag(FitCombo, FitMode.Fit);
            _pdf.MarginMm = MarginSlider.Value;
            _pdf.Bookmarks = BookmarkCheck.IsChecked == true;
            _pdf.PageNumbers = PageNumCheck.IsChecked == true;
            _pdf.TextFontSize = TextSizeSlider.Value;
            _pdf.Title = PdfTitleBox.Text;
            _pdf.Author = PdfAuthorBox.Text;
            _pdf.OutPath = PdfOutBox.Text.Trim();
            _pdf.OpenWhenDone = OpenWhenDoneCheck.IsChecked == true;
        }

        static T ParseTag<T>(ComboBox c, T dflt) where T : struct
        {
            var tag = (c?.SelectedItem as ComboBoxItem)?.Tag as string;
            return string.IsNullOrEmpty(tag) || !Enum.TryParse<T>(tag, out var v) ? dflt : v;
        }

        void OnPdfBrowseOut(object s, RoutedEventArgs e)
        {
            var d = new SaveFileDialog
            {
                Title = "Save the merged PDF as",
                Filter = "PDF|*.pdf",
                DefaultExt = ".pdf",
                FileName = Path.GetFileName(PdfOutBox.Text)
            };
            try { d.InitialDirectory = Path.GetDirectoryName(PdfOutBox.Text); } catch { }
            if (d.ShowDialog(this) == true) PdfOutBox.Text = d.FileName;
        }

        void UpdateDocSummary()
        {
            if (DocSummary == null) return;

            var live = _docs.Where(x => x.Include && !x.HasProblem).ToList();
            if (_docs.Count == 0)
            {
                DocSummary.Text = "drop files anywhere on the window";
                return;
            }

            int pages = live.Sum(x => PdfBuild.EstimatePages(x, _pdf));
            int skipped = _docs.Count - live.Count;

            var approx = live.All(x => x.Kind == DocKind.Pdf) ? "" : "about ";
            DocSummary.Text =
                $"{live.Count} file{(live.Count == 1 ? "" : "s")}  ·  {approx}{pages} page{(pages == 1 ? "" : "s")}" +
                (skipped > 0 ? $"  ·  {skipped} skipped" : "");
        }

        // ==================================================================
        //  building
        // ==================================================================
        /// <summary>
        /// Exercises the PDF tab the way the buttons do — add, renumber, reorder, sort,
        /// then a real build — so wiring mistakes surface here rather than in front of the user.
        /// </summary>
        internal async Task SelfTestPdfTab(List<string> problems, List<string> report)
        {
            var docs = App.StartupFiles.Where(Docs.Accepts).ToArray();
            if (docs.Length == 0) { report.Add("PDF tab: no mergeable files were passed, skipped"); return; }

            try
            {
                AddDocs(docs, sortNew: true);
                if (_docs.Count == 0) { problems.Add("PDF tab: nothing was added"); return; }

                report.Add("PDF tab: added " + _docs.Count + " -> " +
                           string.Join(", ", _docs.Select(d => d.Order + ":" + d.Name)));

                for (int i = 0; i < _docs.Count; i++)
                    if (_docs[i].Order != i + 1) problems.Add("PDF tab: numbering is out of step at row " + i);

                if (_docs.Count > 1)
                {
                    var last = _docs[_docs.Count - 1];
                    MoveTo(last, 0);                      // the "type 1 into the box" path
                    if (_docs[0] != last || last.Order != 1)
                        problems.Add("PDF tab: moving a row to position 1 did not take");

                    OnPdfReverse(null, null);
                    OnPdfSortName(null, null);
                    if (_docs[0].Order != 1) problems.Add("PDF tab: sort left the numbering wrong");
                    report.Add("PDF tab: after sort -> " +
                               string.Join(", ", _docs.Select(d => d.Order + ":" + d.Name)));
                }

                OpenWhenDoneCheck.IsChecked = false;
                PageNumCheck.IsChecked = true;

                // ---- PDF target ----
                var pdfOut = Path.Combine(Ff.TempDir, "selftest_merge.pdf");
                try { if (File.Exists(pdfOut)) File.Delete(pdfOut); } catch { }
                PdfOutBox.Text = pdfOut;

                OnBuildPdf(null, null);
                for (int i = 0; i < 600 && _pdfBusy; i++) await Task.Delay(100);

                if (_pdfBusy) problems.Add("Documents tab: PDF build did not finish in 60s");
                else if (!File.Exists(pdfOut)) problems.Add("Documents tab: PDF build produced no file");
                else
                {
                    var n = PdfBuild.PageCountOf(pdfOut);
                    if (n <= 0) problems.Add("Documents tab: PDF will not re-open");
                    report.Add($"Documents tab: PDF {n} pages, {Fmt.Size(new FileInfo(pdfOut).Length)}");
                }

                // ---- Word target ----
                ChipToDocx.IsChecked = true;
                OnTargetChip(ChipToDocx, null);
                report.Add("Documents tab: switched to Word, statuses -> " +
                           string.Join(", ", _docs.Select(d => d.Name + "=" + d.Status)));

                NameBox.Text = "Test Student";
                CourseBox.Text = "ENG 101";
                EssayTitleBox.Text = "Self Test";
                CollectDocx();

                var docxOut = Path.Combine(Ff.TempDir, "selftest_merge.docx");
                try { if (File.Exists(docxOut)) File.Delete(docxOut); } catch { }
                PdfOutBox.Text = docxOut;

                OnBuildPdf(null, null);
                for (int i = 0; i < 600 && _pdfBusy; i++) await Task.Delay(100);

                if (_pdfBusy) problems.Add("Documents tab: Word build did not finish in 60s");
                else if (!File.Exists(docxOut)) problems.Add("Documents tab: Word build produced no file");
                else report.Add($"Documents tab: Word {Fmt.Size(new FileInfo(docxOut).Length)}  {docxOut}");

                report.Add("Documents tab: summary reads \"" + DocSummary.Text + "\"");
            }
            catch (Exception ex)
            {
                problems.Add("PDF tab: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        void PdfLogLine(string s)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                PdfLog.AppendText(s + Environment.NewLine);
                PdfLog.ScrollToEnd();
            }));
        }

        void OnCancelPdf(object s, RoutedEventArgs e) => _pdfCts?.Cancel();

        async void OnBuildPdf(object s, RoutedEventArgs e)
        {
            if (_pdfBusy) return;
            CollectPdf();
            CollectDocx();
            bool word = _target == MergeTarget.Docx;

            var usable = _docs.Where(x => x.Include && !x.HasProblem).ToList();
            if (usable.Count == 0)
            {
                PdfLogLine("nothing to merge — add some files, or tick the ones you want");
                return;
            }
            if (string.IsNullOrWhiteSpace(_pdf.OutPath))
            {
                PdfLogLine("pick where to save it first");
                return;
            }
            var wantExt = word ? ".docx" : ".pdf";
            if (!_pdf.OutPath.EndsWith(wantExt, StringComparison.OrdinalIgnoreCase))
            {
                _pdf.OutPath += wantExt;
                PdfOutBox.Text = _pdf.OutPath;
            }
            _docxOpt.OutPath = _pdf.OutPath;

            // Never quietly clobber something that is already there.
            if (File.Exists(_pdf.OutPath))
            {
                var ask = MessageBox.Show(this,
                    Path.GetFileName(_pdf.OutPath) + " already exists. Replace it?",
                    "CompDash", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (ask != MessageBoxResult.OK) return;
            }

            _pdfBusy = true;
            _pdfCts = new CancellationTokenSource();
            BtnBuildPdf.IsEnabled = false;
            BtnCancelPdf.Visibility = Visibility.Visible;

            var sw = Stopwatch.StartNew();
            PdfLogLine("");
            PdfLogLine("=== building " + Path.GetFileName(_pdf.OutPath) + " from " + usable.Count + " files");

            BuildReport rep = null;
            try
            {
                var snapshot = usable.Select(x => x).ToList();
                Action<double, string> onProgress = (p, what) =>
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        PdfProg.Value = p;
                        PdfProgText.Text = what;
                    }));

                if (word)
                {
                    var opt = _docxOpt;
                    rep = await Task.Run(() => DocxBuild.BuildAsync(snapshot, opt, onProgress, _pdfCts.Token),
                                         _pdfCts.Token);
                }
                else
                {
                    var opt = _pdf;
                    rep = await Task.Run(() => PdfBuild.BuildAsync(snapshot, opt, onProgress, _pdfCts.Token),
                                         _pdfCts.Token);
                }
            }
            catch (OperationCanceledException) { PdfLogLine("cancelled"); }
            catch (Exception ex) { PdfLogLine("FAILED: " + ex.Message); }
            finally
            {
                sw.Stop();
                _pdfBusy = false;
                BtnBuildPdf.IsEnabled = true;
                BtnCancelPdf.Visibility = Visibility.Collapsed;
                PdfProg.Value = 0;
            }

            if (rep == null)
            {
                PdfProgText.Text = "cancelled";
                return;
            }

            foreach (var line in rep.Log) PdfLogLine("  " + line);

            if (rep.Ok)
            {
                var unit = word ? "paragraphs" : "pages";
                PdfLogLine($"  {rep.Pages} {unit} · {Fmt.Size(rep.Bytes)} · {sw.Elapsed.TotalSeconds:0.#}s");
                PdfLogLine("  " + rep.OutPath);
                PdfProgText.Text = $"{rep.Pages} {unit} · {Fmt.Size(rep.Bytes)}";

                if (rep.Failures.Count > 0)
                    PdfLogLine("  " + rep.Failures.Count + " file(s) were skipped — see above");

                if (_pdf.OpenWhenDone)
                {
                    try { Process.Start(new ProcessStartInfo(rep.OutPath) { UseShellExecute = true }); }
                    catch (Exception ex) { PdfLogLine("  could not open it: " + ex.Message); }
                }
            }
            else
            {
                PdfProgText.Text = "failed";
                foreach (var f in rep.Failures) PdfLogLine("  " + f);
            }
        }
    }
}
