using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace CompDash.Core
{
    public enum AcademicStyle { Plain, MLA, APA }

    public sealed class DocxOptions
    {
        public PaperSize Paper = PaperSize.Letter;
        public double MarginIn = 1.0;

        public string FontName = "Times New Roman";
        public double FontSize = 12;
        public double LineSpacing = 2.0;          // 1, 1.5 or 2
        public bool FirstLineIndent = true;
        public bool PageNumbers = true;

        public AcademicStyle Style = AcademicStyle.MLA;
        public string StudentName = "";
        public string Instructor = "";
        public string Course = "";
        public string DateLine = "";
        public string TitleText = "";

        public bool PageBreakBetweenFiles = true;
        public bool TreatTxtAsMarkdown = false;
        public string OutPath = "";
        public bool OpenWhenDone = true;
    }

    /// <summary>
    /// Writes a .docx with the Open XML SDK. No Word required — the format is ECMA-376,
    /// a zip of XML parts, and the SDK just builds the parts.
    /// </summary>
    public static class DocxBuild
    {
        const int TwipsPerInch = 1440;
        const long EmuPerInch = 914400;

        // Word only embeds these directly; anything else gets converted first.
        static readonly Dictionary<string, PartTypeInfo> ImageTypes =
            new Dictionary<string, PartTypeInfo>(StringComparer.OrdinalIgnoreCase)
            {
                { ".png", ImagePartType.Png },
                { ".jpg", ImagePartType.Jpeg },
                { ".jpeg", ImagePartType.Jpeg },
                { ".gif", ImagePartType.Gif },
                { ".bmp", ImagePartType.Bmp },
                { ".tif", ImagePartType.Tiff },
                { ".tiff", ImagePartType.Tiff },
            };

        public static async Task<BuildReport> BuildAsync(IReadOnlyList<DocItem> items, DocxOptions opt,
                                                         Action<double, string> progress,
                                                         CancellationToken ct)
        {
            var rep = new BuildReport { OutPath = opt.OutPath };
            var temps = new List<string>();

            var work = items.Where(i => i.Include).OrderBy(i => i.Order).ToList();
            if (work.Count == 0)
            {
                rep.Failures.Add("Nothing selected to convert.");
                return rep;
            }

            var dir = Path.GetDirectoryName(opt.OutPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using (var doc = WordprocessingDocument.Create(opt.OutPath, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body());
                var body = main.Document.Body;

                BuildStyles(main, opt);
                BuildNumbering(main);

                bool first = true;
                if (opt.Style != AcademicStyle.Plain) first = !WriteTitleBlock(body, opt);

                for (int n = 0; n < work.Count; n++)
                {
                    ct.ThrowIfCancellationRequested();
                    var it = work[n];
                    progress?.Invoke(n / (double)work.Count, it.Name);

                    if (!first && opt.PageBreakBetweenFiles)
                        body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));

                    int before = body.ChildElements.Count;
                    try
                    {
                        switch (it.Kind)
                        {
                            case DocKind.Text:
                                WriteBlocks(main, body, ReadBlocks(it, opt), opt, temps);
                                break;

                            case DocKind.Image:
                                WriteImage(main, body, it.Path, opt, temps);
                                break;

                            case DocKind.Office when it.Path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase):
                                ImportDocx(main, body, it.Path);
                                break;

                            case DocKind.Office:
                                {
                                    var conv = await DocConvert.OfficeToDocxAsync(it.Path, ct).ConfigureAwait(false);
                                    temps.Add(conv);
                                    ImportDocx(main, body, conv);
                                    break;
                                }

                            case DocKind.Html:
                                {
                                    var conv = await DocConvert.OfficeToDocxAsync(it.Path, ct).ConfigureAwait(false);
                                    temps.Add(conv);
                                    ImportDocx(main, body, conv);
                                    break;
                                }

                            case DocKind.Pdf:
                                throw new InvalidOperationException(
                                    "PDF to Word needs layout reconstruction, which this does not do. " +
                                    "Use the PDF target, or open the PDF in Word itself.");

                            default:
                                throw new InvalidOperationException("cannot go into a Word document");
                        }

                        rep.Log.Add(it.Name + "  ->  " + (body.ChildElements.Count - before) + " blocks");
                        first = false;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        rep.Failures.Add(it.Name + ": " + ex.Message);
                        rep.Log.Add("SKIPPED " + it.Name + ": " + ex.Message);
                    }
                }

                if (body.ChildElements.Count == 0)
                {
                    rep.Failures.Add("Nothing was written into the document.");
                    Cleanup(temps);
                    return rep;
                }

                if (opt.PageNumbers) AddPageNumberHeader(doc, main, opt);
                body.AppendChild(SectionProperties(opt, main));

                main.Document.Save();
                rep.Pages = body.Descendants<Paragraph>().Count();
            }

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

        static List<Block> ReadBlocks(DocItem it, DocxOptions opt)
        {
            var ext = Path.GetExtension(it.Path);
            bool md = ext.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                      ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase) ||
                      opt.TreatTxtAsMarkdown;
            return MarkdownLite.Parse(Docs.ReadText(it.Path), md, Path.GetDirectoryName(it.Path));
        }

        // ------------------------------------------------------------------
        //  page setup
        // ------------------------------------------------------------------
        static void PaperTwips(PaperSize p, out int w, out int h)
        {
            switch (p)
            {
                case PaperSize.A4: w = 11906; h = 16838; break;
                case PaperSize.Legal: w = 12240; h = 20160; break;
                case PaperSize.A3: w = 16838; h = 23811; break;
                case PaperSize.A5: w = 8391; h = 11906; break;
                case PaperSize.Tabloid: w = 15840; h = 24480; break;
                default: w = 12240; h = 15840; break;   // Letter, and the Auto fallback
            }
        }

        static SectionProperties SectionProperties(DocxOptions opt, MainDocumentPart main)
        {
            PaperTwips(opt.Paper, out var pw, out var ph);
            var m = (uint)Math.Round(Math.Max(0, opt.MarginIn) * TwipsPerInch);

            // Order matters: the schema puts header references before the page size.
            var sp = new SectionProperties();

            var header = main.HeaderParts.FirstOrDefault();
            if (header != null)
                sp.AppendChild(new HeaderReference
                {
                    Type = HeaderFooterValues.Default,
                    Id = main.GetIdOfPart(header)
                });

            sp.AppendChild(new PageSize { Width = (uint)pw, Height = (uint)ph });
            sp.AppendChild(new PageMargin
            {
                Top = (int)m,
                Bottom = (int)m,
                Left = m,
                Right = m,
                Header = (uint)(TwipsPerInch / 2),
                Footer = (uint)(TwipsPerInch / 2),
                Gutter = 0
            });

            return sp;
        }

        /// <summary>MLA and APA both want the page number top-right; MLA prefixes the surname.</summary>
        static void AddPageNumberHeader(WordprocessingDocument doc, MainDocumentPart main, DocxOptions opt)
        {
            var headerPart = main.AddNewPart<HeaderPart>();

            var runs = new List<OpenXmlElement>();

            var surname = LastName(opt.StudentName);
            if (opt.Style == AcademicStyle.MLA && surname.Length > 0)
                runs.Add(new Run(RunProps(opt), new Text(surname + " ") { Space = SpaceProcessingModeValues.Preserve }));

            // A PAGE field, so Word renumbers it for us.
            runs.Add(new Run(RunProps(opt), new FieldChar { FieldCharType = FieldCharValues.Begin }));
            runs.Add(new Run(RunProps(opt), new FieldCode(" PAGE ") { Space = SpaceProcessingModeValues.Preserve }));
            runs.Add(new Run(RunProps(opt), new FieldChar { FieldCharType = FieldCharValues.Separate }));
            runs.Add(new Run(RunProps(opt), new Text("1")));
            runs.Add(new Run(RunProps(opt), new FieldChar { FieldCharType = FieldCharValues.End }));

            var p = new Paragraph(new ParagraphProperties(
                new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto },
                new Justification { Val = JustificationValues.Right }));

            foreach (var r in runs) p.AppendChild(r);

            headerPart.Header = new Header(p);
            headerPart.Header.Save();
        }

        static string LastName(string full)
        {
            if (string.IsNullOrWhiteSpace(full)) return "";
            var parts = full.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts[parts.Length - 1];
        }

        /// <summary>
        /// Run properties in the order the schema demands: fonts, bold, italic, size.
        /// Appending them in a convenient order instead produces a file Word will not open.
        /// </summary>
        static RunProperties RunProps(DocxOptions opt, bool bold = false, bool italic = false)
        {
            var r = new RunProperties();
            r.AppendChild(new RunFonts { Ascii = opt.FontName, HighAnsi = opt.FontName, ComplexScript = opt.FontName });
            if (bold) r.AppendChild(new Bold());
            if (italic) r.AppendChild(new Italic());
            r.AppendChild(new FontSize { Val = ((int)(opt.FontSize * 2)).ToString() });
            return r;
        }

        // ------------------------------------------------------------------
        //  styles
        // ------------------------------------------------------------------
        static void BuildStyles(MainDocumentPart main, DocxOptions opt)
        {
            var part = main.AddNewPart<StyleDefinitionsPart>();
            var styles = new Styles();

            int half = (int)Math.Round(opt.FontSize * 2);
            string line = ((int)Math.Round(opt.LineSpacing * 240)).ToString();

            styles.AppendChild(new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = opt.FontName, HighAnsi = opt.FontName, ComplexScript = opt.FontName },
                    new FontSize { Val = half.ToString() })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "0", Line = line, LineRule = LineSpacingRuleValues.Auto }))));

            styles.AppendChild(MakeStyle("Normal", "Normal", true,
                new StyleParagraphProperties(
                    new SpacingBetweenLines { After = "0", Line = line, LineRule = LineSpacingRuleValues.Auto }),
                new StyleRunProperties(
                    new RunFonts { Ascii = opt.FontName, HighAnsi = opt.FontName },
                    new FontSize { Val = half.ToString() })));

            // Academic writing keeps headings in the body font rather than a blue sans-serif.
            bool academic = opt.Style != AcademicStyle.Plain;
            string headFont = academic ? opt.FontName : "Calibri Light";

            styles.AppendChild(MakeStyle("Heading1", "heading 1", false,
                new StyleParagraphProperties(
                    new KeepNext(),
                    new SpacingBetweenLines { Before = academic ? "0" : "240", After = "0", Line = line, LineRule = LineSpacingRuleValues.Auto },
                    new Justification { Val = academic ? JustificationValues.Center : JustificationValues.Left }),
                new StyleRunProperties(
                    new RunFonts { Ascii = headFont, HighAnsi = headFont },
                    new Bold(),
                    new Color { Val = academic ? "000000" : "2F5496" },
                    new FontSize { Val = (academic ? half : (int)(half * 1.35)).ToString() })));

            styles.AppendChild(MakeStyle("Heading2", "heading 2", false,
                new StyleParagraphProperties(
                    new KeepNext(), new SpacingBetweenLines { Before = academic ? "0" : "200", After = "0", Line = line, LineRule = LineSpacingRuleValues.Auto }),
                new StyleRunProperties(
                    new RunFonts { Ascii = headFont, HighAnsi = headFont },
                    new Bold(),
                    new Color { Val = academic ? "000000" : "2F5496" },
                    new FontSize { Val = (academic ? half : (int)(half * 1.2)).ToString() })));

            styles.AppendChild(MakeStyle("Heading3", "heading 3", false,
                new StyleParagraphProperties(
                    new KeepNext(), new SpacingBetweenLines { Before = academic ? "0" : "160", After = "0", Line = line, LineRule = LineSpacingRuleValues.Auto }),
                new StyleRunProperties(
                    new RunFonts { Ascii = headFont, HighAnsi = headFont },
                    new Bold(), new Italic(),
                    new FontSize { Val = half.ToString() })));

            styles.AppendChild(MakeStyle("Quote", "Quote", false,
                new StyleParagraphProperties(
                    new SpacingBetweenLines { After = "0", Line = line, LineRule = LineSpacingRuleValues.Auto },
                    new Indentation { Left = "720" }),
                new StyleRunProperties(new Italic())));

            styles.AppendChild(MakeStyle("CodeBlock", "Code Block", false,
                new StyleParagraphProperties(
                    new Shading { Val = ShadingPatternValues.Clear, Fill = "F2F2F2" },
                    new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto },
                    new Indentation { Left = "360" }),
                new StyleRunProperties(
                    new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" },
                    new FontSize { Val = Math.Max(14, half - 4).ToString() })));

            part.Styles = styles;
            part.Styles.Save();
        }

        static Style MakeStyle(string id, string name, bool isDefault,
                               StyleParagraphProperties pPr, StyleRunProperties rPr)
        {
            var s = new Style
            {
                Type = StyleValues.Paragraph,
                StyleId = id,
                CustomStyle = id == "CodeBlock" ? true : (bool?)null,
                Default = isDefault ? true : (bool?)null
            };
            s.AppendChild(new StyleName { Val = name });
            if (!isDefault) s.AppendChild(new BasedOn { Val = "Normal" });
            s.AppendChild(new PrimaryStyle());
            if (pPr != null) s.AppendChild(pPr);
            if (rPr != null) s.AppendChild(rPr);
            return s;
        }

        // ------------------------------------------------------------------
        //  numbering for bullets and numbered lists
        // ------------------------------------------------------------------
        const int BulletNumId = 1;
        const int NumberNumId = 2;

        static void BuildNumbering(MainDocumentPart main)
        {
            var part = main.AddNewPart<NumberingDefinitionsPart>();
            var numbering = new Numbering();

            var bulletChars = new[] { "", "o", "" };
            var abstractBullet = new AbstractNum { AbstractNumberId = 0 };
            abstractBullet.AppendChild(new MultiLevelType { Val = MultiLevelValues.HybridMultilevel });
            for (int lvl = 0; lvl < 4; lvl++)
            {
                abstractBullet.AppendChild(new Level(
                    new StartNumberingValue { Val = 1 },
                    new NumberingFormat { Val = NumberFormatValues.Bullet },
                    new LevelText { Val = bulletChars[lvl % 3] },
                    new LevelJustification { Val = LevelJustificationValues.Left },
                    new PreviousParagraphProperties(new Indentation
                    {
                        Left = (720 * (lvl + 1)).ToString(),
                        Hanging = "360"
                    }),
                    new NumberingSymbolRunProperties(new RunFonts { Ascii = "Symbol", HighAnsi = "Symbol", Hint = FontTypeHintValues.Default }))
                { LevelIndex = lvl });
            }

            var formats = new[] { NumberFormatValues.Decimal, NumberFormatValues.LowerLetter, NumberFormatValues.LowerRoman, NumberFormatValues.Decimal };
            var abstractNumber = new AbstractNum { AbstractNumberId = 1 };
            abstractNumber.AppendChild(new MultiLevelType { Val = MultiLevelValues.HybridMultilevel });
            for (int lvl = 0; lvl < 4; lvl++)
            {
                abstractNumber.AppendChild(new Level(
                    new StartNumberingValue { Val = 1 },
                    new NumberingFormat { Val = formats[lvl] },
                    new LevelText { Val = "%" + (lvl + 1) + "." },
                    new LevelJustification { Val = LevelJustificationValues.Left },
                    new PreviousParagraphProperties(new Indentation
                    {
                        Left = (720 * (lvl + 1)).ToString(),
                        Hanging = "360"
                    }))
                { LevelIndex = lvl });
            }

            numbering.AppendChild(abstractBullet);
            numbering.AppendChild(abstractNumber);
            numbering.AppendChild(new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = BulletNumId });
            numbering.AppendChild(new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = NumberNumId });

            part.Numbering = numbering;
            part.Numbering.Save();
        }

        // ------------------------------------------------------------------
        //  the title block
        // ------------------------------------------------------------------
        static bool WriteTitleBlock(Body body, DocxOptions opt)
        {
            var header = new[] { opt.StudentName, opt.Instructor, opt.Course, opt.DateLine }
                .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var hasTitle = !string.IsNullOrWhiteSpace(opt.TitleText);
            if (header.Count == 0 && !hasTitle) return false;

            if (opt.Style == AcademicStyle.MLA)
            {
                foreach (var lineText in header)
                    body.AppendChild(Para(lineText, JustificationValues.Left, false, opt, indent: false));
            }
            else if (opt.Style == AcademicStyle.APA)
            {
                foreach (var lineText in header)
                    body.AppendChild(Para(lineText, JustificationValues.Center, false, opt, indent: false));
            }

            if (hasTitle)
                body.AppendChild(Para(opt.TitleText, JustificationValues.Center,
                                      opt.Style == AcademicStyle.APA, opt, indent: false));

            return true;
        }

        static Paragraph Para(string text, JustificationValues align, bool bold, DocxOptions opt, bool indent)
        {
            var pPr = new ParagraphProperties();
            if (indent && opt.FirstLineIndent)
                pPr.AppendChild(new Indentation { FirstLine = "720" });   // ind comes before jc
            pPr.AppendChild(new Justification { Val = align });

            return new Paragraph(pPr,
                new Run(RunProps(opt, bold), new Text(text ?? "") { Space = SpaceProcessingModeValues.Preserve }));
        }

        // ------------------------------------------------------------------
        //  writing the block model
        // ------------------------------------------------------------------
        static void WriteBlocks(MainDocumentPart main, Body body, List<Block> blocks,
                                DocxOptions opt, List<string> temps)
        {
            foreach (var b in blocks)
            {
                switch (b.Kind)
                {
                    case BlockKind.Rule:
                        body.AppendChild(new Paragraph(new ParagraphProperties(
                            new ParagraphBorders(new BottomBorder
                            {
                                Val = BorderValues.Single,
                                Size = 6,
                                Color = "999999"
                            }))));
                        break;

                    case BlockKind.Code:
                        foreach (var codeLine in (b.RawText ?? "").Split('\n'))
                        {
                            body.AppendChild(new Paragraph(
                                new ParagraphProperties(new ParagraphStyleId { Val = "CodeBlock" }),
                                new Run(new Text(codeLine.TrimEnd()) { Space = SpaceProcessingModeValues.Preserve })));
                        }
                        break;

                    case BlockKind.Image:
                        WriteImage(main, body, b.ImagePath, opt, temps);
                        break;

                    case BlockKind.Table:
                        WriteTable(body, b, opt);
                        // Word wants a paragraph after a table, or two tables run together.
                        body.AppendChild(new Paragraph());
                        break;

                    default:
                        body.AppendChild(BlockParagraph(main, b, opt));
                        break;
                }
            }
        }

        static Paragraph BlockParagraph(MainDocumentPart main, Block b, DocxOptions opt)
        {
            var pPr = new ParagraphProperties();

            switch (b.Kind)
            {
                case BlockKind.Heading1: pPr.AppendChild(new ParagraphStyleId { Val = "Heading1" }); break;
                case BlockKind.Heading2: pPr.AppendChild(new ParagraphStyleId { Val = "Heading2" }); break;
                case BlockKind.Heading3: pPr.AppendChild(new ParagraphStyleId { Val = "Heading3" }); break;
                case BlockKind.Quote: pPr.AppendChild(new ParagraphStyleId { Val = "Quote" }); break;

                case BlockKind.Bullet:
                case BlockKind.Number:
                    pPr.AppendChild(new NumberingProperties(
                        new NumberingLevelReference { Val = b.ListLevel },
                        new NumberingId { Val = b.Kind == BlockKind.Bullet ? BulletNumId : NumberNumId }));
                    pPr.AppendChild(new Indentation
                    {
                        Left = (720 * (b.ListLevel + 1)).ToString(),
                        Hanging = "360"
                    });
                    break;

                default:
                    if (opt.FirstLineIndent && opt.Style != AcademicStyle.Plain)
                        pPr.AppendChild(new Indentation { FirstLine = "720" });
                    break;
            }

            var p = new Paragraph(pPr);

            foreach (var span in b.Spans)
            {
                if (string.IsNullOrEmpty(span.Text)) continue;

                // rFonts, b, i, color, u - the schema's order, not a convenient one.
                var rPr = new RunProperties();
                if (span.Code)
                    rPr.AppendChild(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" });
                if (span.Bold) rPr.AppendChild(new Bold());
                if (span.Italic) rPr.AppendChild(new Italic());
                if (span.Link != null)
                {
                    rPr.AppendChild(new Color { Val = "0563C1" });
                    rPr.AppendChild(new Underline { Val = UnderlineValues.Single });
                }

                var run = new Run(rPr, new Text(span.Text) { Space = SpaceProcessingModeValues.Preserve });

                if (span.Link != null && Uri.IsWellFormedUriString(span.Link, UriKind.Absolute))
                {
                    var rel = main.AddHyperlinkRelationship(new Uri(span.Link, UriKind.Absolute), true);
                    p.AppendChild(new Hyperlink(run) { Id = rel.Id });
                }
                else p.AppendChild(run);
            }

            return p;
        }

        // ------------------------------------------------------------------
        //  tables
        // ------------------------------------------------------------------
        static void WriteTable(Body body, Block b, DocxOptions opt)
        {
            var table = new Table();

            // TableProperties children have a fixed order: width before borders.
            var props = new TableProperties();
            props.AppendChild(new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct });
            props.AppendChild(new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4, Color = "999999" },
                new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "999999" },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "999999" },
                new RightBorder { Val = BorderValues.Single, Size = 4, Color = "999999" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" }));
            table.AppendChild(props);

            int columns = 0;
            foreach (var r in b.Rows) columns = Math.Max(columns, r.Cells.Count);
            if (columns == 0) return;

            // The schema requires a grid between the properties and the rows.
            PaperTwips(opt.Paper, out var pageW, out _);
            int contentW = Math.Max(1000, pageW - (int)(opt.MarginIn * TwipsPerInch * 2));
            var grid = new TableGrid();
            for (int c = 0; c < columns; c++)
                grid.AppendChild(new GridColumn { Width = (contentW / columns).ToString() });
            table.AppendChild(grid);

            for (int rowIndex = 0; rowIndex < b.Rows.Count; rowIndex++)
            {
                var source = b.Rows[rowIndex];
                bool header = b.HeaderRow && rowIndex == 0;
                var row = new DocumentFormat.OpenXml.Wordprocessing.TableRow();

                if (header)
                    row.AppendChild(new TableRowProperties(new TableHeader()));

                for (int c = 0; c < columns; c++)
                {
                    var cell = new TableCell();
                    cell.AppendChild(new TableCellProperties(
                        new TableCellWidth { Type = TableWidthUnitValues.Auto }));

                    var align = c < b.Aligns.Count ? b.Aligns[c] : CellAlign.Left;
                    var spans = c < source.Cells.Count ? source.Cells[c] : new List<Span>();
                    cell.AppendChild(CellParagraph(spans, align, header, opt));
                    row.AppendChild(cell);
                }

                table.AppendChild(row);
            }

            body.AppendChild(table);
        }

        /// <summary>
        /// Table cells get single spacing and no first-line indent whatever the document
        /// body uses — a double-spaced indented cell is unreadable.
        /// </summary>
        static Paragraph CellParagraph(List<Span> spans, CellAlign align, bool header, DocxOptions opt)
        {
            var pPr = new ParagraphProperties();
            pPr.AppendChild(new SpacingBetweenLines
            {
                Before = "40",
                After = "40",
                Line = "240",
                LineRule = LineSpacingRuleValues.Auto
            });
            if (align != CellAlign.Left)
                pPr.AppendChild(new Justification
                {
                    Val = align == CellAlign.Center ? JustificationValues.Center : JustificationValues.Right
                });

            var p = new Paragraph(pPr);

            foreach (var span in spans)
            {
                if (string.IsNullOrEmpty(span.Text)) continue;

                var rPr = new RunProperties();
                rPr.AppendChild(new RunFonts
                {
                    Ascii = span.Code ? "Consolas" : opt.FontName,
                    HighAnsi = span.Code ? "Consolas" : opt.FontName
                });
                if (span.Bold || header) rPr.AppendChild(new Bold());
                if (span.Italic) rPr.AppendChild(new Italic());
                rPr.AppendChild(new FontSize { Val = ((int)(opt.FontSize * 2)).ToString() });

                p.AppendChild(new Run(rPr, new Text(span.Text) { Space = SpaceProcessingModeValues.Preserve }));
            }

            if (!p.Elements<Run>().Any())
                p.AppendChild(new Run(RunProps(opt), new Text("")));

            return p;
        }

        // ------------------------------------------------------------------
        //  images
        // ------------------------------------------------------------------
        static void WriteImage(MainDocumentPart main, Body body, string path, DocxOptions opt, List<string> temps)
        {
            var ext = Path.GetExtension(path);
            var source = path;

            if (!ImageTypes.TryGetValue(ext, out var partType))
            {
                // WebP, HEIC and friends: Word will not embed them, so re-wrap as PNG.
                source = ConvertToPng(path);
                temps.Add(source);
                partType = ImagePartType.Png;
            }

            int pxW, pxH;
            double dpiX, dpiY;
            try
            {
                var frame = BitmapFrame.Create(new Uri(source), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                pxW = frame.PixelWidth;
                pxH = frame.PixelHeight;
                dpiX = frame.DpiX > 1 ? frame.DpiX : 96;
                dpiY = frame.DpiY > 1 ? frame.DpiY : 96;
            }
            catch (Exception ex) { throw new InvalidOperationException("could not read the image: " + ex.Message); }

            var imagePart = main.AddImagePart(partType);
            using (var stream = File.OpenRead(source)) imagePart.FeedData(stream);
            var relId = main.GetIdOfPart(imagePart);

            PaperTwips(opt.Paper, out var pwTwips, out _);
            double contentIn = pwTwips / (double)TwipsPerInch - opt.MarginIn * 2;

            double wIn = pxW / dpiX, hIn = pxH / dpiY;
            if (wIn > contentIn) { var s = contentIn / wIn; wIn *= s; hIn *= s; }

            long cx = Math.Max(1, (long)(wIn * EmuPerInch));
            long cy = Math.Max(1, (long)(hIn * EmuPerInch));
            uint id = (uint)(main.Document.Body.Descendants<DW.DocProperties>().Count() + 1);

            var drawing = new Drawing(
                new DW.Inline(
                    new DW.Extent { Cx = cx, Cy = cy },
                    new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                    new DW.DocProperties { Id = id, Name = Path.GetFileName(path) },
                    new DW.NonVisualGraphicFrameDrawingProperties(
                        new A.GraphicFrameLocks { NoChangeAspect = true }),
                    new A.Graphic(
                        new A.GraphicData(
                            new PIC.Picture(
                                new PIC.NonVisualPictureProperties(
                                    new PIC.NonVisualDrawingProperties { Id = id, Name = Path.GetFileName(path) },
                                    new PIC.NonVisualPictureDrawingProperties()),
                                new PIC.BlipFill(
                                    new A.Blip { Embed = relId },
                                    new A.Stretch(new A.FillRectangle())),
                                new PIC.ShapeProperties(
                                    new A.Transform2D(
                                        new A.Offset { X = 0L, Y = 0L },
                                        new A.Extents { Cx = cx, Cy = cy }),
                                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                        { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
                {
                    DistanceFromTop = 0U,
                    DistanceFromBottom = 0U,
                    DistanceFromLeft = 0U,
                    DistanceFromRight = 0U
                });

            body.AppendChild(new Paragraph(
                new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
                new Run(drawing)));
        }

        static string ConvertToPng(string src)
        {
            if (Ff.FfmpegPath == null)
                throw new InvalidOperationException(
                    "Word cannot embed " + Path.GetExtension(src) + " and ffmpeg was not found to convert it.");

            var outPng = Path.Combine(DocConvert.WorkDir,
                                      "img_" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".png");
            var args = new List<string> { "-y", "-i", src, "-frames:v", "1", outPng };
            if (!Ff.QuietAsync(args, CancellationToken.None).GetAwaiter().GetResult() || !File.Exists(outPng))
                throw new InvalidOperationException("could not convert " + Path.GetFileName(src) + " to PNG");
            return outPng;
        }

        // ------------------------------------------------------------------
        //  bringing in an existing .docx
        // ------------------------------------------------------------------
        static void ImportDocx(MainDocumentPart main, Body body, string path)
        {
            using var src = WordprocessingDocument.Open(path, false);
            var srcMain = src.MainDocumentPart;
            if (srcMain?.Document?.Body == null)
                throw new InvalidOperationException("that document has no readable body");

            foreach (var element in srcMain.Document.Body.ChildElements)
            {
                if (element is SectionProperties) continue;      // the target keeps its own page setup

                var copy = element.CloneNode(true);

                // Pictures carry relationship ids that mean nothing in the new package,
                // so each referenced image is copied across and the id rewritten.
                foreach (var blip in copy.Descendants<A.Blip>().ToList())
                {
                    var oldId = blip.Embed?.Value;
                    if (string.IsNullOrEmpty(oldId)) continue;
                    try
                    {
                        if (!(srcMain.GetPartById(oldId) is ImagePart srcImage)) continue;
                        var newPart = main.AddImagePart(srcImage.ContentType);
                        using (var s = srcImage.GetStream()) newPart.FeedData(s);
                        blip.Embed = main.GetIdOfPart(newPart);
                    }
                    catch { blip.Embed = null; }
                }

                body.AppendChild(copy);
            }
        }
    }
}
