using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CompDash.Core;
using Microsoft.Win32;

namespace CompDash
{
    public sealed class QueueItem : INotifyPropertyChanged
    {
        public string Path { get; set; }
        public string Name => System.IO.Path.GetFileName(Path);

        string _sub = "reading…";
        public string Sub
        {
            get => _sub;
            set { _sub = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sub))); }
        }

        public MediaInfo Info;
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class MainWindow : Window
    {
        static readonly string[] Accepted =
        {
            ".mp4", ".mkv", ".mov", ".webm", ".avi", ".m4v", ".wmv", ".flv", ".ts", ".mpg", ".mpeg", ".gif",
            ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp"
        };

        readonly ObservableCollection<QueueItem> _items = new ObservableCollection<QueueItem>();
        readonly EncodeSettings _s = new EncodeSettings();

        MediaInfo _cur;
        bool _suppress = true;
        bool _busy;

        DispatcherTimer _estTimer, _prevTimer;
        CancellationTokenSource _estCts, _prevCts, _jobCts;
        long _lastCalibrated;

        public MainWindow()
        {
            InitializeComponent();
            FileList.ItemsSource = _items;
            Loaded += OnLoaded;
        }

        // ==================================================================
        //  startup
        // ==================================================================
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            var ok = Ff.Locate();
            FfText.Text = ok ? "ffmpeg ready" : "ffmpeg not found — see Toolbox";
            FfBadge.ToolTip = ok ? Ff.FfmpegPath : "install ffmpeg and restart";
            FfDot.Fill = ok ? (Brush)FindResource("Good") : (Brush)FindResource("Bad");
            BtnConvert.IsEnabled = ok;
            BtnConvertAll.IsEnabled = ok;

            foreach (var p in Presets.All)
                PresetCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p });
            PresetCombo.SelectedIndex = 0;

            MappingText.Text = MappingBlurb();

            _estTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            _estTimer.Tick += (a, b) => { _estTimer.Stop(); StartCalibrate(); };

            _prevTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _prevTimer.Tick += (a, b) => { _prevTimer.Stop(); StartPreview(); };

            ChipMp4.IsChecked = true;
            _suppress = false;
            Refresh();

            InitPdfTab();

            if (App.StartupFiles.Length > 0) AddFiles(App.StartupFiles);
            if (App.SelfTest) RunSelfTest();
        }

        /// <summary>
        /// Walks every output format through the same code the buttons use, so a layout or
        /// visibility mistake shows up as a failure line instead of a crash in front of the user.
        /// </summary>
        async void RunSelfTest()
        {
            var problems = new List<string>();
            var report = new List<string>();

            for (int i = 0; i < 40 && _cur == null; i++) await Task.Delay(150);
            if (_cur == null) { problems.Add("no file was probed"); }

            foreach (OutKind k in Enum.GetValues(typeof(OutKind)))
            {
                try
                {
                    _suppress = true; SetChip(k); _suppress = false;
                    _lastCalibrated = 0;
                    Refresh();
                    await Task.Delay(60);

                    if (_cur != null)
                    {
                        if (string.IsNullOrWhiteSpace(CmdPreview.Text))
                            problems.Add(k + ": no command was built");
                        report.Add($"{k,-5} est {EstTrim.Text,10} -> {EstQuality.Text,10}   {CmdPreview.Text}");
                    }
                }
                catch (Exception ex) { problems.Add(k + ": " + ex.GetType().Name + " " + ex.Message); }
            }

            report.Add("");
            await SelfTestPdfTab(problems, report);

            var path = Path.Combine(Ff.TempDir, "selftest.txt");
            File.WriteAllLines(path, new[] { problems.Count == 0 ? "SELFTEST OK" : "SELFTEST FAILURES:" }
                .Concat(problems).Concat(new[] { "" }).Concat(report));
            Console.WriteLine(path);
            Close();
        }

        Preset SelectedPreset() => (PresetCombo.SelectedItem as ComboBoxItem)?.Tag as Preset;

        static string MappingBlurb() =>
            "compress.bat\n" +
            "   CRF 28 / preset slower / AAC 128k  →  section 5, with any CRF, codec and effort you like.\n" +
            "   Its GIF branch (10 fps, 480px, 64 colours, no dither)  →  sections 2, 3 and 4, each independent.\n\n" +
            "squeeze.ps1\n" +
            "   Hard-coded 500 KB target and its bitrate solver  →  section 5, \"Target file size\", any number.\n" +
            "   It never touched resolution; here you can, and the estimate tells you what that buys.\n" +
            "   Its retry-until-it-fits loop is kept: if the first pass overshoots, the bitrate drops and it goes again.\n\n" +
            "convert_to_gif.bat / convert_to_mp4.bat / convert_to_mkv.bat\n" +
            "   Fixed settings  →  the format chips plus every section. Same defaults available as presets.\n\n" +
            "install_context_menu.reg\n" +
            "   Still the fastest route for a one-off. Install or remove it from the left of this tab.\n\n" +
            "What is new\n" +
            "   A size estimate after every section, so you can see which knob is actually paying for itself.\n" +
            "   The big number is measured, not guessed: short slices are really encoded and extrapolated.\n" +
            "   A side-by-side preview that shows the compressed frame, not a clean one.\n" +
            "   H.265, VP9 and AV1, WebM and WebP, palette control for PNG, and per-file batching.";

        // ==================================================================
        //  file queue
        // ==================================================================
        void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);

            // Drops land in whichever tab is open, so the same gesture means the obvious thing.
            if (Tabs != null && Tabs.SelectedIndex == 1) AddDocs(files, sortNew: true);
            else AddFiles(files);
        }

        void OnAddFiles(object sender, RoutedEventArgs e)
        {
            var d = new OpenFileDialog
            {
                Multiselect = true,
                Title = "Add media",
                Filter = "Media|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.gif;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.webp|All files|*.*"
            };
            if (d.ShowDialog(this) == true) AddFiles(d.FileNames);
        }

        void OnClearFiles(object sender, RoutedEventArgs e)
        {
            _items.Clear();
            _cur = null;
            Refresh();
        }

        async void AddFiles(IEnumerable<string> paths)
        {
            var added = new List<QueueItem>();
            foreach (var p in paths)
            {
                try
                {
                    if (Directory.Exists(p))
                    {
                        foreach (var f in Directory.GetFiles(p))
                            if (Accepted.Contains(Path.GetExtension(f).ToLowerInvariant()))
                                added.Add(Enqueue(f));
                        continue;
                    }
                    if (!File.Exists(p)) continue;
                    if (!Accepted.Contains(Path.GetExtension(p).ToLowerInvariant())) continue;
                    added.Add(Enqueue(p));
                }
                catch { }
            }

            if (added.Count > 0 && FileList.SelectedItem == null)
                FileList.SelectedIndex = 0;

            foreach (var it in added.Where(x => x != null))
            {
                it.Info = await Ff.ProbeAsync(it.Path);
                it.Sub = it.Info.Error != null ? "unreadable" : it.Info.Summary();
                if (ReferenceEquals(FileList.SelectedItem, it)) LoadCurrent(it);
            }
        }

        QueueItem Enqueue(string path)
        {
            if (_items.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) return null;
            var it = new QueueItem { Path = path };
            _items.Add(it);
            return it;
        }

        void OnFileSelected(object sender, SelectionChangedEventArgs e)
        {
            var it = FileList.SelectedItem as QueueItem;
            if (it?.Info != null) LoadCurrent(it);
        }

        void LoadCurrent(QueueItem it)
        {
            _cur = it.Info;
            _suppress = true;

            SrcName.Text = _cur.Name;
            SrcMeta.Text = _cur.Summary();

            var dur = Math.Max(0.01, _cur.Duration);
            TrimStartSlider.Maximum = dur;
            TrimEndSlider.Maximum = dur;
            TrimStartSlider.Value = 0;
            TrimEndSlider.Value = dur;
            PreviewTime.Maximum = dur;
            PreviewTime.Value = Math.Min(dur * 0.3, dur);
            FrameTimeSlider.Maximum = dur;
            FrameTimeSlider.Value = Math.Min(dur * 0.3, dur);

            if (_cur.Width > 0) WidthSlider.Maximum = Math.Max(3840, _cur.Width);
            WidthSlider.Value = Math.Min(WidthSlider.Maximum, Math.Max(64, _cur.Width));
            ExactW.Text = _cur.Width.ToString();
            ExactH.Text = _cur.Height.ToString();
            FpsSlider.Value = Math.Max(1, Math.Min(60, Math.Round(_cur.Fps > 0 ? _cur.Fps : 30)));

            // A sensible starting format for what was dropped in.
            if (_cur.IsStill) SetChip(_cur.Ext == ".png" ? OutKind.Jpg : OutKind.Png);
            else if (_cur.Ext == ".gif") SetChip(OutKind.Mp4);
            else SetChip(OutKind.Mp4);

            _suppress = false;
            _lastCalibrated = 0;
            Refresh();
        }

        // ==================================================================
        //  format chips
        // ==================================================================
        IEnumerable<System.Windows.Controls.Primitives.ToggleButton> Chips()
        {
            yield return ChipMp4; yield return ChipMkv; yield return ChipWebm; yield return ChipGif;
            yield return ChipPng; yield return ChipJpg; yield return ChipWebp;
        }

        void SetChip(OutKind k)
        {
            foreach (var c in Chips())
                c.IsChecked = (string)c.Tag == k.ToString();
            _s.Out = k;
        }

        void OnFormatChip(object sender, RoutedEventArgs e)
        {
            var c = (System.Windows.Controls.Primitives.ToggleButton)sender;
            var kind = (OutKind)Enum.Parse(typeof(OutKind), (string)c.Tag);
            _suppress = true;
            SetChip(kind);
            _suppress = false;
            _lastCalibrated = 0;
            Refresh();
        }

        // ==================================================================
        //  generic control handlers
        // ==================================================================
        void OnSettingSlider(object s, RoutedPropertyChangedEventArgs<double> e) { if (!_suppress) Refresh(); }
        void OnSettingCombo(object s, SelectionChangedEventArgs e) { if (!_suppress) Refresh(); }
        void OnSettingToggle(object s, RoutedEventArgs e) { if (!_suppress) Refresh(); }
        void OnSettingText(object s, TextChangedEventArgs e) { if (!_suppress) Refresh(); }
        void OnScaleModeChanged(object s, RoutedEventArgs e) { if (!_suppress) Refresh(); }
        void OnRateModeChanged(object s, RoutedEventArgs e) { if (!_suppress) Refresh(); }
        void OnAudioModeChanged(object s, RoutedEventArgs e) { if (!_suppress) Refresh(); }

        void OnTrimChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppress) return;
            if (TrimEndSlider.Value < TrimStartSlider.Value + 0.05)
            {
                _suppress = true;
                if (ReferenceEquals(s, TrimStartSlider))
                    TrimStartSlider.Value = Math.Max(0, TrimEndSlider.Value - 0.05);
                else
                    TrimEndSlider.Value = Math.Min(TrimEndSlider.Maximum, TrimStartSlider.Value + 0.05);
                _suppress = false;
            }
            Refresh();
        }

        void OnResetTrim(object s, RoutedEventArgs e)
        {
            _suppress = true;
            TrimStartSlider.Value = 0;
            TrimEndSlider.Value = TrimEndSlider.Maximum;
            _suppress = false;
            Refresh();
        }

        void OnQuickWidth(object s, RoutedEventArgs e)
        {
            ScaleWidth.IsChecked = true;
            WidthSlider.Value = double.Parse((string)((Button)s).Tag, CultureInfo.InvariantCulture);
        }

        void OnQuickFps(object s, RoutedEventArgs e)
        {
            ChangeFpsCheck.IsChecked = true;
            FpsSlider.Value = double.Parse((string)((Button)s).Tag, CultureInfo.InvariantCulture);
        }

        void OnQuickColors(object s, RoutedEventArgs e) =>
            ColorsSlider.Value = double.Parse((string)((Button)s).Tag, CultureInfo.InvariantCulture);

        void OnQuickTarget(object s, RoutedEventArgs e)
        {
            RateTarget.IsChecked = true;
            TargetMbBox.Text = ((string)((Button)s).Tag);
        }

        void OnPreviewTimeChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppress) return;
            _prevTimer.Stop();
            _prevTimer.Start();
        }

        void OnRefreshPreview(object s, RoutedEventArgs e) => StartPreview();
        void OnRefine(object s, RoutedEventArgs e) => StartCalibrate();
        void OnClearLog(object s, RoutedEventArgs e) => LogBox.Clear();

        void OnBrowseOutDir(object s, RoutedEventArgs e)
        {
            var d = new OpenFolderDialog { Title = "Output folder" };
            if (d.ShowDialog(this) == true) OutDirBox.Text = d.FolderName;
        }

        void OnSameAsSource(object s, RoutedEventArgs e) => OutDirBox.Text = "";

        void OnCopyCommand(object s, RoutedEventArgs e)
        {
            try { Clipboard.SetText(CmdPreview.Text); Log("command copied to clipboard"); } catch { }
        }

        // ==================================================================
        //  UI  ->  settings
        // ==================================================================
        static string TagOf(ComboBox c) => (c.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        static double ParseD(string s, double dflt)
        {
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : dflt;
        }

        void Collect()
        {
            _s.TrimStart = TrimStartSlider.Value;
            _s.TrimEnd = TrimEndSlider.Value >= TrimEndSlider.Maximum - 0.001 ? 0 : TrimEndSlider.Value;

            _s.Scale = ScalePercent.IsChecked == true ? ScaleMode.Percent
                     : ScaleWidth.IsChecked == true ? ScaleMode.Width
                     : ScaleExact.IsChecked == true ? ScaleMode.Exact
                     : ScaleMode.Original;
            _s.Percent = PercentSlider.Value;
            _s.TargetWidth = (int)Math.Round(WidthSlider.Value);
            _s.ExactWidth = (int)ParseD(ExactW.Text, 1280);
            _s.ExactHeight = (int)ParseD(ExactH.Text, 720);
            _s.ScaleFlags = TagOf(ScalerCombo);
            if (string.IsNullOrEmpty(_s.ScaleFlags)) _s.ScaleFlags = "lanczos";

            _s.ChangeFps = ChangeFpsCheck.IsChecked == true;
            _s.Fps = FpsSlider.Value;

            _s.Grayscale = GrayCheck.IsChecked == true;
            _s.Saturation = SatSlider.Value;
            _s.Contrast = ContrastSlider.Value;
            _s.Brightness = BrightSlider.Value;
            _s.PixFmt = TagOf(PixFmtCombo);
            if (string.IsNullOrEmpty(_s.PixFmt)) _s.PixFmt = "yuv420p";
            _s.PaletteColors = (int)Math.Round(ColorsSlider.Value);
            _s.PaletteStats = TagOf(StatsCombo);
            if (string.IsNullOrEmpty(_s.PaletteStats)) _s.PaletteStats = "diff";
            _s.Dither = TagOf(DitherCombo);
            if (string.IsNullOrEmpty(_s.Dither)) _s.Dither = "sierra2_4a";
            _s.BayerScale = (int)Math.Round(BayerSlider.Value);
            _s.UsePalette = UsePaletteCheck.IsChecked == true;

            _s.Codec = (VCodec)Enum.Parse(typeof(VCodec), string.IsNullOrEmpty(TagOf(CodecCombo)) ? "H264" : TagOf(CodecCombo));
            _s.Rate = RateBitrate.IsChecked == true ? RateMode.Bitrate
                    : RateTarget.IsChecked == true ? RateMode.TargetSize
                    : RateMode.Quality;
            _s.Crf = (int)Math.Round(CrfSlider.Value);
            _s.VideoKbps = (int)Math.Round(BitrateSlider.Value);
            _s.TargetMB = Math.Max(0.01, ParseD(TargetMbBox.Text, 8));
            _s.Preset = string.IsNullOrEmpty(TagOf(PresetCodecCombo)) ? "medium" : TagOf(PresetCodecCombo);
            _s.SvtPreset = (int)Math.Round(SvtSlider.Value);
            _s.TwoPass = TwoPassCheck.IsChecked == true;

            _s.Audio = AudioCopy.IsChecked == true ? AudioMode.Copy
                     : AudioRemove.IsChecked == true ? AudioMode.Remove
                     : AudioMode.Reencode;
            _s.ACodec = (ACodec)Enum.Parse(typeof(ACodec), string.IsNullOrEmpty(TagOf(ACodecCombo)) ? "Aac" : TagOf(ACodecCombo));
            _s.AudioKbps = (int)Math.Round(AudioKbpsSlider.Value);
            _s.AudioChannels = (int)ParseD(TagOf(AChannelsCombo), 0);

            _s.JpegQuality = (int)Math.Round(JpegSlider.Value);
            _s.WebpQuality = (int)Math.Round(WebpSlider.Value);
            _s.WebpLossless = WebpLosslessCheck.IsChecked == true;
            _s.PngLevel = (int)Math.Round(PngSlider.Value);
            _s.FrameTime = FrameTimeSlider.Value;

            _s.StripMetadata = StripMetaCheck.IsChecked == true;
            _s.FastStart = FastStartCheck.IsChecked == true;
            _s.GifLoop = GifLoopCheck.IsChecked == true ? 0 : -1;
            _s.OutDir = OutDirBox.Text.Trim();
            _s.Suffix = SuffixBox.Text;
        }

        // ==================================================================
        //  settings  ->  UI  (presets)
        // ==================================================================
        static void SelectTag(ComboBox c, string tag)
        {
            foreach (ComboBoxItem i in c.Items)
                if ((i.Tag as string) == tag) { c.SelectedItem = i; return; }
        }

        void Apply()
        {
            _suppress = true;

            SetChip(_s.Out);

            ScaleOriginal.IsChecked = _s.Scale == ScaleMode.Original;
            ScalePercent.IsChecked = _s.Scale == ScaleMode.Percent;
            ScaleWidth.IsChecked = _s.Scale == ScaleMode.Width;
            ScaleExact.IsChecked = _s.Scale == ScaleMode.Exact;
            PercentSlider.Value = _s.Percent;
            WidthSlider.Value = Math.Min(WidthSlider.Maximum, Math.Max(WidthSlider.Minimum, _s.TargetWidth));
            ExactW.Text = _s.ExactWidth.ToString();
            ExactH.Text = _s.ExactHeight.ToString();
            SelectTag(ScalerCombo, _s.ScaleFlags);

            ChangeFpsCheck.IsChecked = _s.ChangeFps;
            FpsSlider.Value = Math.Max(1, Math.Min(60, _s.Fps));

            GrayCheck.IsChecked = _s.Grayscale;
            SatSlider.Value = _s.Saturation;
            ContrastSlider.Value = _s.Contrast;
            BrightSlider.Value = _s.Brightness;
            SelectTag(PixFmtCombo, _s.PixFmt);
            ColorsSlider.Value = _s.PaletteColors;
            SelectTag(StatsCombo, _s.PaletteStats);
            SelectTag(DitherCombo, _s.Dither);
            BayerSlider.Value = _s.BayerScale;
            UsePaletteCheck.IsChecked = _s.UsePalette;

            SelectTag(CodecCombo, _s.Codec.ToString());
            RateQuality.IsChecked = _s.Rate == RateMode.Quality;
            RateBitrate.IsChecked = _s.Rate == RateMode.Bitrate;
            RateTarget.IsChecked = _s.Rate == RateMode.TargetSize;
            CrfSlider.Value = _s.Crf;
            BitrateSlider.Value = Math.Min(BitrateSlider.Maximum, _s.VideoKbps);
            TargetMbBox.Text = _s.TargetMB.ToString("0.####", CultureInfo.InvariantCulture);
            SelectTag(PresetCodecCombo, _s.Preset);
            SvtSlider.Value = _s.SvtPreset;
            TwoPassCheck.IsChecked = _s.TwoPass;

            AudioCopy.IsChecked = _s.Audio == AudioMode.Copy;
            AudioRemove.IsChecked = _s.Audio == AudioMode.Remove;
            AudioReencode.IsChecked = _s.Audio == AudioMode.Reencode;
            SelectTag(ACodecCombo, _s.ACodec.ToString());
            AudioKbpsSlider.Value = Math.Max(16, Math.Min(320, _s.AudioKbps));
            SelectTag(AChannelsCombo, _s.AudioChannels.ToString());

            JpegSlider.Value = _s.JpegQuality;
            WebpSlider.Value = _s.WebpQuality;
            WebpLosslessCheck.IsChecked = _s.WebpLossless;
            PngSlider.Value = _s.PngLevel;

            StripMetaCheck.IsChecked = _s.StripMetadata;
            FastStartCheck.IsChecked = _s.FastStart;
            GifLoopCheck.IsChecked = _s.GifLoop == 0;
            OutDirBox.Text = _s.OutDir ?? "";
            SuffixBox.Text = _s.Suffix ?? "";

            _suppress = false;
        }

        void OnPresetPreview(object s, SelectionChangedEventArgs e)
        {
            var p = SelectedPreset();
            if (p != null)
                PresetDetail.Text = p.Detail + (p.Origin == "new" ? "" : "\nfrom " + p.Origin);
        }

        void OnApplyPreset(object s, RoutedEventArgs e)
        {
            var p = SelectedPreset();
            if (p == null) return;
            if (_cur == null) { Log("pick a file first"); return; }
            p.Apply(_cur, _s);
            Apply();
            _lastCalibrated = 0;
            Refresh();
            Log("preset applied: " + p.Name);
        }

        // ==================================================================
        //  visibility rules per output family
        // ==================================================================
        void ApplyVisibility()
        {
            var fam = Cmd.Family(_s.Out);
            var still = _cur != null && _cur.IsStill;

            Vis(SecTrim, fam != OutFamily.Image && !still);
            Vis(SecFps, fam != OutFamily.Image && !still);
            Vis(SecAudio, fam == OutFamily.Video && !still);

            Vis(PercentRow, _s.Scale == ScaleMode.Percent);
            Vis(WidthRow, _s.Scale == ScaleMode.Width);
            Vis(ExactRow, _s.Scale == ScaleMode.Exact);

            var palette = fam == OutFamily.Gif || _s.Out == OutKind.Png;
            Vis(PaletteBlock, palette);
            Vis(UsePaletteCheck, _s.Out == OutKind.Png);
            var paletteActive = fam == OutFamily.Gif || (_s.Out == OutKind.Png && _s.UsePalette);
            ColorsSlider.IsEnabled = paletteActive;
            DitherCombo.IsEnabled = paletteActive;
            StatsCombo.IsEnabled = paletteActive && fam == OutFamily.Gif;
            Vis(BayerRow, paletteActive && _s.Dither == "bayer");
            Vis(StatsCombo, fam == OutFamily.Gif);
            Vis(PixFmtRow, fam == OutFamily.Video);

            Vis(VideoQualityBlock, fam == OutFamily.Video);
            Vis(ImageQualityBlock, fam == OutFamily.Image);
            Vis(GifQualityNote, fam == OutFamily.Gif);

            Vis(CrfRow, _s.Rate == RateMode.Quality);
            Vis(CrfHint, _s.Rate == RateMode.Quality);
            Vis(BitrateRow, _s.Rate == RateMode.Bitrate);
            Vis(TargetRow, _s.Rate == RateMode.TargetSize);
            Vis(SvtRow, _s.Codec == VCodec.AV1);
            PresetCodecCombo.IsEnabled = _s.Codec == VCodec.H264 || _s.Codec == VCodec.H265;
            TwoPassCheck.IsEnabled = _s.Rate != RateMode.Quality;

            Vis(JpegRow, _s.Out == OutKind.Jpg);
            Vis(WebpRow, _s.Out == OutKind.WebP);
            Vis(WebpLosslessCheck, _s.Out == OutKind.WebP);
            Vis(PngRow, _s.Out == OutKind.Png);
            Vis(FrameTimeRow, fam == OutFamily.Image && !still && _cur != null && _cur.Duration > 0.1);

            AudioOpts.IsEnabled = _s.Audio == AudioMode.Reencode;

            QualityTitle.Text = fam == OutFamily.Image ? "5 · Format quality" : "5 · Compression & codec";
        }

        static void Vis(UIElement el, bool show) =>
            el.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        // ==================================================================
        //  the refresh cycle
        // ==================================================================
        void Refresh()
        {
            if (_suppress) return;
            Collect();

            var note = _cur != null ? Cmd.Normalize(_cur, _s) : "";
            if (!string.IsNullOrEmpty(note))
            {
                NormalizeNote.Text = note;
                NormalizeNote.Visibility = Visibility.Visible;
                _suppress = true; Apply(); _suppress = false;
                Collect();
            }
            else NormalizeNote.Visibility = Visibility.Collapsed;

            ApplyVisibility();
            UpdateReadouts();
            UpdateCommandPreview();
            UpdateInstantEstimates();

            if (_cur != null && !_busy)
            {
                _estTimer.Stop(); _estTimer.Start();
                if (AutoPreviewCheck.IsChecked == true) { _prevTimer.Stop(); _prevTimer.Start(); }
            }
        }

        void UpdateReadouts()
        {
            if (_cur == null)
            {
                OutDims.Text = ""; TrimReadout.Text = ""; FpsReadout.Text = ""; OutPathPreview.Text = "";
                return;
            }

            Cmd.OutputSize(_cur, _s, out var w, out var h);
            var pct = _cur.Width > 0 ? (w * 100.0 / _cur.Width) : 100;
            OutDims.Text = $"{_cur.Width}×{_cur.Height} → {w}×{h}  ({pct:0}%)";
            ScaleSub.Text = $"{_cur.Width}×{_cur.Height} → {w}×{h}";

            var dur = Cmd.OutDuration(_cur, _s);
            TrimReadout.Text = $"keeping {Fmt.Time(dur)} of {Fmt.Time(_cur.Duration)}";
            TrimSub.Text = _cur.Duration > 0 ? $"{Fmt.Time(dur)} of {Fmt.Time(_cur.Duration)}" : "still image";

            var fps = Cmd.OutFps(_cur, _s);
            var frames = Math.Max(1, (int)Math.Round(dur * fps));
            FpsReadout.Text = $"source {_cur.Fps:0.##} fps → {fps:0.##} fps · {frames:N0} frames out";
            FpsSub.Text = $"{_cur.Fps:0.##} → {fps:0.##} fps";

            ColorSub.Text = Cmd.Family(_s.Out) == OutFamily.Gif || (_s.Out == OutKind.Png && _s.UsePalette)
                ? $"{_s.PaletteColors} colours · {_s.Dither}"
                : (_s.Grayscale ? "greyscale" : "full colour");

            QualitySub.Text = Cmd.Family(_s.Out) == OutFamily.Video
                ? (_s.Rate == RateMode.Quality ? $"{Cmd.CodecName(_s.Codec)} · CRF {_s.Crf}"
                 : _s.Rate == RateMode.Bitrate ? $"{Cmd.CodecName(_s.Codec)} · {_s.VideoKbps} kbps"
                 : $"{Cmd.CodecName(_s.Codec)} · fit {_s.TargetMB:0.##} MB")
                : (_s.Out == OutKind.Jpg ? $"JPEG q{_s.JpegQuality}"
                 : _s.Out == OutKind.WebP ? (_s.WebpLossless ? "WebP lossless" : $"WebP q{_s.WebpQuality}")
                 : _s.Out == OutKind.Png ? $"PNG deflate {_s.PngLevel}" : "");

            AudioSub.Text = !_cur.HasAudio ? "source has no audio"
                : _s.Audio == AudioMode.Remove ? "removed"
                : _s.Audio == AudioMode.Copy ? $"copied ({_cur.AudioCodec})"
                : $"{Cmd.AudioCodecName(_s.ACodec)} {_s.AudioKbps}k";

            CrfHint.Text = CrfHintFor(_s.Codec, _s.Crf);
            OutPathPreview.Text = "→ " + Path.GetFileName(Cmd.OutputPath(_cur, _s));
            PrevResultLabel.Text = "result · " + Cmd.Ext(_s.Out).TrimStart('.');
        }

        static string CrfHintFor(VCodec c, int crf)
        {
            var q = Estimator.EquivCrf(c, crf);
            if (q <= 17) return "visually lossless — very large";
            if (q <= 21) return "high quality";
            if (q <= 25) return "good — the usual sweet spot";
            if (q <= 30) return "noticeably compressed";
            if (q <= 36) return "soft and blocky in motion";
            return "heavily degraded";
        }

        void UpdateCommandPreview()
        {
            if (_cur == null || Ff.FfmpegPath == null) { CmdPreview.Text = ""; return; }
            try
            {
                var outPath = Cmd.OutputPath(_cur, _s);
                int? kbps = null;
                if (_s.Rate == RateMode.TargetSize && Cmd.Family(_s.Out) == OutFamily.Video)
                    kbps = Cmd.SolveKbps(_cur, _s, _s.TargetMB * 1024 * 1024);
                var args = Cmd.Build(_cur, _s, outPath, Stage.All, 0, null, null, kbps);
                CmdPreview.Text = Ff.CommandLine("ffmpeg", args);
            }
            catch (Exception ex) { CmdPreview.Text = ex.Message; }
        }

        // ==================================================================
        //  estimates
        // ==================================================================
        void UpdateInstantEstimates()
        {
            if (_cur == null)
            {
                EstTrim.Text = EstScale.Text = EstFps.Text = EstColor.Text = EstQuality.Text = EstAudio.Text = "—";
                EstBig.Text = "—"; EstDelta.Text = ""; EstFrom.Text = ""; EstNote.Text = "";
                return;
            }

            var v = Estimator.Breakdown(_cur, _s, _lastCalibrated);
            var chips = new[] { EstTrim, EstScale, EstFps, EstColor, EstQuality, EstAudio };
            var prev = _cur.SizeBytes;

            for (int i = 0; i < chips.Length; i++)
            {
                chips[i].Text = Fmt.Size(v[i]);
                chips[i].Foreground = v[i] < prev ? (Brush)FindResource("Good")
                                    : v[i] > prev * 1.02 ? (Brush)FindResource("Warn")
                                    : (Brush)FindResource("Fg");
                prev = v[i];
            }

            var total = v[v.Length - 1];
            EstBig.Text = Fmt.Size(total);
            EstDelta.Text = Fmt.Delta(_cur.SizeBytes, total);
            EstDelta.Foreground = total <= _cur.SizeBytes ? (Brush)FindResource("Good") : (Brush)FindResource("Warn");
            EstFrom.Text = "from " + Fmt.Size(_cur.SizeBytes) + " source";
            if (_lastCalibrated <= 0) EstNote.Text = "modelled — measuring…";
        }

        async void StartCalibrate()
        {
            if (_cur == null || Ff.FfmpegPath == null || _busy) return;

            _estCts?.Cancel();
            var cts = new CancellationTokenSource();
            _estCts = cts;

            EstSpinner.Visibility = Visibility.Visible;
            var snapshot = _s.Clone();
            var src = _cur;

            Estimate est = null;
            try { est = await Estimator.CalibrateAsync(src, snapshot, cts.Token); }
            catch { }

            if (cts.IsCancellationRequested || !ReferenceEquals(_estCts, cts)) return;
            EstSpinner.Visibility = Visibility.Collapsed;

            if (est == null || est.Bytes <= 0)
            {
                EstNote.Text = "modelled estimate — could not measure a sample";
                return;
            }

            _lastCalibrated = est.Bytes;
            UpdateInstantEstimates();
            EstNote.Text = est.Note;
            EstNote.Foreground = (Brush)FindResource(est.Exact ? "Good" : "Dim");
        }

        // ==================================================================
        //  preview
        // ==================================================================
        async void StartPreview()
        {
            if (_cur == null || Ff.FfmpegPath == null || _busy) return;

            _prevCts?.Cancel();
            var cts = new CancellationTokenSource();
            _prevCts = cts;

            var src = _cur;
            var snap = _s.Clone();
            var t = PreviewTime.Value;
            var stamp = Guid.NewGuid().ToString("N").Substring(0, 8);

            var srcPng = Path.Combine(Ff.TempDir, "prev_s_" + stamp + ".png");
            var outTmp = Path.Combine(Ff.TempDir, "prev_o_" + stamp + Cmd.Ext(snap.Out));
            var outPng = Path.Combine(Ff.TempDir, "prev_r_" + stamp + ".png");

            try
            {
                // left: an untouched frame
                var a1 = new List<string> { "-y" };
                if (t > 0.01 && src.Duration > 0.05) { a1.Add("-ss"); a1.Add(t.ToString("0.###", CultureInfo.InvariantCulture)); }
                a1.Add("-i"); a1.Add(src.Path);
                a1.Add("-frames:v"); a1.Add("1");
                a1.Add("-vf"); a1.Add("scale='min(420,iw)':-2:flags=lanczos");
                a1.Add(srcPng);
                if (await Ff.QuietAsync(a1, cts.Token) && !cts.IsCancellationRequested)
                    PrevSource.Source = LoadBitmap(srcPng);

                // right: a frame that has actually been through the encoder
                double sampleDur = Cmd.Family(snap.Out) == OutFamily.Image || src.IsStill ? 0 : 0.7;
                var a2 = sampleDur > 0
                    ? Cmd.Build(src, snap, outTmp, Stage.All, 0, t, sampleDur)
                    : Cmd.Build(src, snap, outTmp);

                if (await Ff.QuietAsync(a2, cts.Token) && !cts.IsCancellationRequested && File.Exists(outTmp))
                {
                    var a3 = new List<string>
                    {
                        "-y", "-i", outTmp, "-frames:v", "1",
                        "-vf", "scale='min(420,iw)':-2:flags=neighbor", outPng
                    };
                    if (await Ff.QuietAsync(a3, cts.Token) && !cts.IsCancellationRequested)
                        PrevResult.Source = LoadBitmap(outPng);
                }
            }
            catch { }
            finally
            {
                Del(srcPng); Del(outTmp); Del(outPng);
            }
        }

        static void Del(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        static BitmapImage LoadBitmap(string path)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bi.UriSource = new Uri(path);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        // ==================================================================
        //  converting
        // ==================================================================
        void Log(string s)
        {
            if (s == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LogBox.AppendText(s + Environment.NewLine);
                LogBox.ScrollToEnd();
            }));
        }

        void OnCancel(object s, RoutedEventArgs e) => _jobCts?.Cancel();

        async void OnConvert(object s, RoutedEventArgs e)
        {
            if (_cur == null) { Log("nothing selected"); return; }
            await RunJobs(new[] { _cur });
        }

        async void OnConvertAll(object s, RoutedEventArgs e)
        {
            var list = _items.Where(x => x.Info != null && x.Info.Error == null).Select(x => x.Info).ToArray();
            if (list.Length == 0) { Log("queue is empty"); return; }
            await RunJobs(list);
        }

        async Task RunJobs(IReadOnlyList<MediaInfo> jobs)
        {
            if (_busy) return;
            _busy = true;
            _estCts?.Cancel();
            _prevCts?.Cancel();
            _jobCts = new CancellationTokenSource();

            BtnConvert.IsEnabled = false;
            BtnConvertAll.IsEnabled = false;
            BtnCancel.Visibility = Visibility.Visible;

            var snap = _s.Clone();
            var sw = Stopwatch.StartNew();

            try
            {
                for (int i = 0; i < jobs.Count; i++)
                {
                    if (_jobCts.IsCancellationRequested) break;
                    var m = jobs[i];
                    var local = snap.Clone();
                    Cmd.Normalize(m, local);
                    ProgText.Text = $"{i + 1}/{jobs.Count}  {m.Name}";
                    await RunOne(m, local, _jobCts.Token);
                }
            }
            finally
            {
                sw.Stop();
                _busy = false;
                BtnConvert.IsEnabled = Ff.FfmpegPath != null;
                BtnConvertAll.IsEnabled = Ff.FfmpegPath != null;
                BtnCancel.Visibility = Visibility.Collapsed;
                Prog.Value = 0;
                ProgText.Text = _jobCts.IsCancellationRequested
                    ? "cancelled"
                    : $"done in {sw.Elapsed.TotalSeconds:0.#}s";
                Estimator.CleanTemp();
            }
        }

        async Task RunOne(MediaInfo m, EncodeSettings s, CancellationToken ct)
        {
            var outPath = Cmd.OutputPath(m, s);
            var dir = Path.GetDirectoryName(outPath);
            try { if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); } catch { }

            var dur = Math.Max(0.05, Cmd.OutDuration(m, s));
            var fam = Cmd.Family(s.Out);

            Log("");
            Log("=== " + m.Name + "  →  " + Path.GetFileName(outPath));

            Action<double> prog = p => Dispatcher.BeginInvoke(new Action(() => Prog.Value = p));

            bool targetMode = s.Rate == RateMode.TargetSize && fam == OutFamily.Video;
            long targetBytes = (long)(s.TargetMB * 1024 * 1024);
            int kbps = targetMode ? Cmd.SolveKbps(m, s, targetBytes) : 0;

            for (int attempt = 0; attempt < (targetMode ? 4 : 1); attempt++)
            {
                if (ct.IsCancellationRequested) return;

                if (targetMode) Log($"attempt {attempt + 1}: {kbps} kbps video");

                bool twoPass = fam == OutFamily.Video && s.TwoPass && s.Rate != RateMode.Quality;

                if (twoPass)
                {
                    var p1 = Cmd.Build(m, s, outPath, Stage.All, 1, null, null, targetMode ? kbps : (int?)null);
                    Log("pass 1/2");
                    var r1 = await Ff.RunAsync(p1, dur, p => prog(p * 0.5), null, ct);
                    if (!r1.Ok) { LogTail(r1); return; }

                    var p2 = Cmd.Build(m, s, outPath, Stage.All, 2, null, null, targetMode ? kbps : (int?)null);
                    Log("pass 2/2");
                    var r2 = await Ff.RunAsync(p2, dur, p => prog(0.5 + p * 0.5), null, ct);
                    if (!r2.Ok) { LogTail(r2); return; }
                }
                else
                {
                    var a = Cmd.Build(m, s, outPath, Stage.All, 0, null, null, targetMode ? kbps : (int?)null);
                    var r = await Ff.RunAsync(a, dur, prog, null, ct);
                    if (!r.Ok) { LogTail(r); return; }
                }

                if (!File.Exists(outPath)) { Log("ffmpeg produced no file"); return; }

                var size = new FileInfo(outPath).Length;
                if (!targetMode || size <= targetBytes)
                {
                    Report(m, outPath, size, targetMode ? targetBytes : 0);
                    return;
                }

                Log($"  {Fmt.Size(size)} — still over {Fmt.Size(targetBytes)}, lowering bitrate");
                kbps = Math.Max(4, (int)(kbps * 0.75));
            }

            if (File.Exists(outPath))
                Report(m, outPath, new FileInfo(outPath).Length, targetBytes);
        }

        void Report(MediaInfo m, string outPath, long size, long target)
        {
            var pct = m.SizeBytes > 0 ? (m.SizeBytes - size) * 100.0 / m.SizeBytes : 0;
            Log($"  {Fmt.Size(m.SizeBytes)} → {Fmt.Size(size)}  ({(pct >= 0 ? "saved " : "grew ")}{Math.Abs(pct):0.#}%)");
            if (target > 0 && size > target)
                Log("  WARNING: could not get under the target even at the bitrate floor.");
            if (size >= m.SizeBytes)
                Log("  NOTE: output is not smaller — the source is already well compressed for these settings.");
            Log("  " + outPath);
        }

        void LogTail(RunResult r)
        {
            if (r.Canceled) { Log("  cancelled"); return; }
            var lines = (r.Log ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Log("  FAILED (exit " + r.ExitCode + ")");
            foreach (var l in lines.Skip(Math.Max(0, lines.Length - 12))) Log("  " + l);
        }

        // ==================================================================
        //  toolbox tab
        // ==================================================================
        void Shell(string exe, string args, bool elevate = false)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true };
                if (elevate) psi.Verb = "runas";
                Process.Start(psi);
            }
            catch (Exception ex) { Log("could not launch: " + ex.Message); }
        }

        void OnRunDiskAnalyzer(object s, RoutedEventArgs e)
        {
            var script = Path.Combine(Toolbox.Root, "disk-analyzer.ps1");
            if (!File.Exists(script)) { Warn(script + " is missing"); return; }
            Shell("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Path \"{DiskPath.Text}\" -Depth {DiskDepth.Text} -Open");
        }

        void OnBrowseTreai(object s, RoutedEventArgs e)
        {
            var d = new OpenFolderDialog { Title = "Folder to summarise" };
            if (d.ShowDialog(this) == true) TreaiPath.Text = d.FolderName;
        }

        void OnRunTreai(object s, RoutedEventArgs e)
        {
            var script = Path.Combine(Toolbox.Root, "TREAI.ps1");
            if (!File.Exists(script)) { Warn(script + " is missing"); return; }
            var target = string.IsNullOrWhiteSpace(TreaiPath.Text) ? "." : TreaiPath.Text;
            Shell("powershell.exe",
                $"-NoExit -NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Path \"{target}\"");
        }

        void OnInstallReg(object s, RoutedEventArgs e) => RunReg("install_context_menu.reg");
        void OnUninstallReg(object s, RoutedEventArgs e) => RunReg("uninstall_context_menu.reg");

        void RunReg(string file)
        {
            var p = Path.Combine(Toolbox.Root, file);
            if (!File.Exists(p)) { Warn(p + " is missing"); return; }
            if (MessageBox.Show(this, "Merge " + file + " into the registry?", "CompDash",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            Shell("regedit.exe", "\"" + p + "\"", true);
        }

        void OnOpenTools(object s, RoutedEventArgs e)
        {
            if (Directory.Exists(Toolbox.Root)) Shell("explorer.exe", "\"" + Toolbox.Root + "\"");
            else Warn(Toolbox.Root + " does not exist");
        }

        void Warn(string msg) =>
            MessageBox.Show(this, msg, "CompDash", MessageBoxButton.OK, MessageBoxImage.Warning);

        protected override void OnClosed(EventArgs e)
        {
            _estCts?.Cancel();
            _prevCts?.Cancel();
            _jobCts?.Cancel();
            base.OnClosed(e);
        }
    }
}
