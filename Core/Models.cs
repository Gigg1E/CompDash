using System;
using System.IO;

namespace CompDash.Core
{
    public enum OutKind { Mp4, Mkv, WebM, Gif, Png, Jpg, WebP }
    public enum OutFamily { Video, Gif, Image }
    public enum VCodec { H264, H265, VP9, AV1 }
    public enum RateMode { Quality, Bitrate, TargetSize }
    public enum ScaleMode { Original, Percent, Width, Exact }
    public enum AudioMode { Copy, Reencode, Remove }
    public enum ACodec { Aac, Opus, Mp3 }

    /// <summary>Everything ffprobe could tell us about one input file.</summary>
    public sealed class MediaInfo
    {
        public string Path = "";
        public string Ext = "";
        public string FormatName = "";
        public long SizeBytes;

        public bool HasVideo, HasAudio, IsStill;
        public string VideoCodec = "", AudioCodec = "", PixFmt = "";
        public int Width, Height;
        public double Fps;
        public double Duration;
        public long BitRate, VideoBitRate, AudioBitRate;
        public int AudioChannels, AudioSampleRate;
        public long NbFrames;
        public string Error;

        public string Name => System.IO.Path.GetFileName(Path);

        /// <summary>Best-effort video bitrate in bits/sec, falling back to file size.</summary>
        public long EffectiveVideoBps
        {
            get
            {
                if (VideoBitRate > 0) return VideoBitRate;
                if (BitRate > 0) return Math.Max(1000, BitRate - AudioBitRate);
                if (Duration > 0.01) return (long)(SizeBytes * 8.0 / Duration * 0.94);
                return 1_000_000;
            }
        }

        public string Summary()
        {
            if (Error != null) return "unreadable: " + Error;
            var s = Fmt.Size(SizeBytes);
            if (IsStill) return $"{Width}x{Height}  ·  {VideoCodec}  ·  {s}";
            var parts = $"{Width}x{Height}  ·  {Fps:0.##} fps  ·  {Fmt.Time(Duration)}  ·  {VideoCodec}";
            if (HasAudio) parts += $"/{AudioCodec} {AudioChannels}ch";
            parts += $"  ·  {s}";
            if (BitRate > 0) parts += $"  ·  {BitRate / 1000:N0} kbps";
            return parts;
        }
    }

    /// <summary>Every knob the dashboard exposes. One instance drives the whole queue.</summary>
    public sealed class EncodeSettings
    {
        public OutKind Out = OutKind.Mp4;

        // --- 1. trim -------------------------------------------------------
        public double TrimStart = 0;
        public double TrimEnd = 0;          // 0 == run to end of source

        // --- 2. frame size -------------------------------------------------
        public ScaleMode Scale = ScaleMode.Original;
        public double Percent = 100;
        public int TargetWidth = 1280;
        public int ExactWidth = 1280, ExactHeight = 720;
        public string ScaleFlags = "lanczos";

        // --- 3. frame rate -------------------------------------------------
        public bool ChangeFps = false;
        public double Fps = 30;

        // --- 4. colour / palette -------------------------------------------
        public bool Grayscale = false;
        public double Saturation = 1.0;
        public double Contrast = 1.0;
        public double Brightness = 0.0;
        public string PixFmt = "yuv420p";
        public int PaletteColors = 128;
        public string PaletteStats = "diff";       // diff | full | single
        public string Dither = "sierra2_4a";       // none | bayer | floyd_steinberg | sierra2 | sierra2_4a
        public int BayerScale = 3;
        public bool UsePalette = false;            // PNG only: quantise to PaletteColors

        // --- 5. quality / codec --------------------------------------------
        public VCodec Codec = VCodec.H264;
        public RateMode Rate = RateMode.Quality;
        public int Crf = 26;
        public int VideoKbps = 2000;
        public double TargetMB = 8;
        public string Preset = "medium";
        public int SvtPreset = 6;
        public bool TwoPass = true;

        // --- 6. audio -------------------------------------------------------
        public AudioMode Audio = AudioMode.Reencode;
        public ACodec ACodec = ACodec.Aac;
        public int AudioKbps = 128;
        public int AudioChannels = 0;              // 0 == keep source

        // --- image-only ------------------------------------------------------
        public int JpegQuality = 85;
        public int WebpQuality = 80;
        public bool WebpLossless = false;
        public int PngLevel = 9;
        public double FrameTime = 0;               // grab-a-frame time when video -> still

        // --- 7. output -------------------------------------------------------
        public bool StripMetadata = true;
        public bool FastStart = true;
        public int GifLoop = 0;                    // 0 == infinite
        public string OutDir = "";                 // empty == alongside source
        public string Suffix = "_cd";

        public EncodeSettings Clone() => (EncodeSettings)MemberwiseClone();
    }

    /// <summary>Which stages of the pipeline are "switched on" for an estimate.</summary>
    [Flags]
    public enum Stage
    {
        None = 0,
        Trim = 1,
        Scale = 2,
        Fps = 4,
        Color = 8,
        Quality = 16,
        Audio = 32,
        All = Trim | Scale | Fps | Color | Quality | Audio
    }

    public static class Fmt
    {
        public static string Size(long b)
        {
            if (b < 0) return "-";
            if (b < 1024) return b + " B";
            if (b < 1024L * 1024) return (b / 1024.0).ToString("0.#") + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0.##") + " MB";
            return (b / 1073741824.0).ToString("0.##") + " GB";
        }

        public static string Time(double sec)
        {
            if (sec < 0) sec = 0;
            var t = TimeSpan.FromSeconds(sec);
            return t.TotalHours >= 1
                ? string.Format("{0:0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                : string.Format("{0:0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
        }

        public static string Delta(long from, long to)
        {
            if (from <= 0) return "";
            var pct = (to - from) * 100.0 / from;
            return (pct <= 0 ? "−" : "+") + Math.Abs(pct).ToString("0") + "%";
        }
    }
}
