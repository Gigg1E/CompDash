using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CompDash.Core
{
    /// <summary>Turns an <see cref="EncodeSettings"/> into an ffmpeg argument list.</summary>
    public static class Cmd
    {
        static string N(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);

        public static OutFamily Family(OutKind k)
        {
            switch (k)
            {
                case OutKind.Gif: return OutFamily.Gif;
                case OutKind.Png:
                case OutKind.Jpg:
                case OutKind.WebP: return OutFamily.Image;
                default: return OutFamily.Video;
            }
        }

        public static string Ext(OutKind k)
        {
            switch (k)
            {
                case OutKind.Mp4: return ".mp4";
                case OutKind.Mkv: return ".mkv";
                case OutKind.WebM: return ".webm";
                case OutKind.Gif: return ".gif";
                case OutKind.Png: return ".png";
                case OutKind.Jpg: return ".jpg";
                default: return ".webp";
            }
        }

        // ------------------------------------------------------------------
        //  geometry & timing
        // ------------------------------------------------------------------
        public static void OutputSize(MediaInfo m, EncodeSettings s, out int w, out int h)
        {
            int sw = Math.Max(2, m.Width), sh = Math.Max(2, m.Height);
            switch (s.Scale)
            {
                case ScaleMode.Percent:
                    w = (int)Math.Round(sw * s.Percent / 100.0);
                    h = (int)Math.Round(sh * s.Percent / 100.0);
                    break;
                case ScaleMode.Width:
                    w = Math.Max(2, s.TargetWidth);
                    h = (int)Math.Round(sh * (double)w / sw);
                    break;
                case ScaleMode.Exact:
                    w = Math.Max(2, s.ExactWidth);
                    h = Math.Max(2, s.ExactHeight);
                    break;
                default:
                    w = sw; h = sh;
                    break;
            }
            // libx264/x265/vp9 in yuv420p need even dimensions; gif/png do not care.
            if (Family(s.Out) == OutFamily.Video)
            {
                if (w % 2 != 0) w++;
                if (h % 2 != 0) h++;
            }
            w = Math.Max(2, Math.Min(16384, w));
            h = Math.Max(2, Math.Min(16384, h));
        }

        public static double OutDuration(MediaInfo m, EncodeSettings s)
        {
            if (m.IsStill || Family(s.Out) == OutFamily.Image) return 0;
            var end = s.TrimEnd > 0 ? Math.Min(s.TrimEnd, m.Duration) : m.Duration;
            return Math.Max(0.02, end - Math.Max(0, s.TrimStart));
        }

        public static double OutFps(MediaInfo m, EncodeSettings s)
        {
            if (!s.ChangeFps) return m.Fps > 0 ? m.Fps : 30;
            return Math.Max(0.5, s.Fps);
        }

        // ------------------------------------------------------------------
        //  filter graph
        // ------------------------------------------------------------------
        /// <summary>fps -> scale -> colour, without the GIF palette stage.</summary>
        public static string BaseFilter(MediaInfo m, EncodeSettings s, Stage stages)
        {
            var parts = new List<string>();

            if (stages.HasFlag(Stage.Fps) && s.ChangeFps && !m.IsStill)
                parts.Add("fps=" + N(Math.Max(0.5, s.Fps)));

            if (stages.HasFlag(Stage.Scale))
            {
                OutputSize(m, s, out var w, out var h);
                if (w != m.Width || h != m.Height)
                    parts.Add($"scale={w}:{h}:flags={s.ScaleFlags}");
            }

            if (stages.HasFlag(Stage.Color))
            {
                if (Math.Abs(s.Contrast - 1.0) > 0.001 || Math.Abs(s.Brightness) > 0.001)
                    parts.Add($"eq=contrast={N(s.Contrast)}:brightness={N(s.Brightness)}");

                if (s.Grayscale) parts.Add("hue=s=0");
                else if (Math.Abs(s.Saturation - 1.0) > 0.001) parts.Add("hue=s=" + N(s.Saturation));
            }

            return string.Join(",", parts);
        }

        /// <summary>Palette generate + apply, used for GIF and for quantised PNG.</summary>
        static string PaletteChain(EncodeSettings s, string baseFilter, bool useDiffStats)
        {
            var b = string.IsNullOrEmpty(baseFilter) ? "" : baseFilter + ",";
            var colors = Math.Max(2, Math.Min(256, s.PaletteColors));
            var stats = useDiffStats ? s.PaletteStats : "full";

            var dither = s.Dither;
            if (dither == "bayer") dither = "bayer:bayer_scale=" + s.BayerScale;

            return $"[0:v]{b}split[cd_a][cd_b];" +
                   $"[cd_a]palettegen=max_colors={colors}:stats_mode={stats}[cd_p];" +
                   $"[cd_b][cd_p]paletteuse=dither={dither}:diff_mode=rectangle";
        }

        // ------------------------------------------------------------------
        //  codec argument helpers
        // ------------------------------------------------------------------
        public static string CodecName(VCodec c)
        {
            switch (c)
            {
                case VCodec.H265: return "libx265";
                case VCodec.VP9: return "libvpx-vp9";
                case VCodec.AV1: return "libsvtav1";
                default: return "libx264";
            }
        }

        public static string AudioCodecName(ACodec c)
        {
            switch (c)
            {
                case ACodec.Opus: return "libopus";
                case ACodec.Mp3: return "libmp3lame";
                default: return "aac";
            }
        }

        /// <summary>Fixes combinations the chosen container cannot hold. Returns a human note.</summary>
        public static string Normalize(MediaInfo m, EncodeSettings s)
        {
            var notes = new List<string>();

            if (s.Out == OutKind.WebM && (s.Codec == VCodec.H264 || s.Codec == VCodec.H265))
            {
                s.Codec = VCodec.VP9;
                notes.Add("WebM cannot hold H.264/H.265 — switched video codec to VP9.");
            }
            if (s.Out == OutKind.WebM && s.Audio == AudioMode.Reencode && s.ACodec != ACodec.Opus)
            {
                s.ACodec = ACodec.Opus;
                notes.Add("WebM audio switched to Opus.");
            }
            if (s.Out == OutKind.WebM && s.Audio == AudioMode.Copy &&
                m.HasAudio && m.AudioCodec != "opus" && m.AudioCodec != "vorbis")
            {
                s.Audio = AudioMode.Reencode; s.ACodec = ACodec.Opus;
                notes.Add("Source audio is not WebM-compatible — re-encoding to Opus.");
            }
            if (s.Out == OutKind.Mp4 && s.Audio == AudioMode.Reencode && s.ACodec == ACodec.Opus)
            {
                s.ACodec = ACodec.Aac;
                notes.Add("Opus in MP4 is poorly supported — switched audio to AAC.");
            }
            if (s.Audio == AudioMode.Copy && !m.HasAudio) s.Audio = AudioMode.Remove;

            if (s.Codec == VCodec.VP9 && s.PixFmt != null && s.PixFmt.Contains("10le") && s.Out == OutKind.Mp4)
                notes.Add("10-bit VP9 in MP4 may not play everywhere.");

            if (m.IsStill && Family(s.Out) != OutFamily.Image)
                notes.Add("Source is a still image — video/GIF output will be a 1-frame file.");

            return string.Join("  ", notes);
        }

        static void AddRate(List<string> a, MediaInfo m, EncodeSettings s, int? kbpsOverride, int pass)
        {
            var crf = s.Crf;
            var codec = CodecName(s.Codec);
            a.Add("-c:v"); a.Add(codec);

            if (s.Codec == VCodec.AV1) { a.Add("-preset"); a.Add(s.SvtPreset.ToString()); }
            else if (s.Codec == VCodec.VP9) { a.Add("-row-mt"); a.Add("1"); a.Add("-deadline"); a.Add("good"); a.Add("-cpu-used"); a.Add("3"); }
            else { a.Add("-preset"); a.Add(s.Preset); }

            int kbps = kbpsOverride ?? s.VideoKbps;

            if (s.Rate == RateMode.Quality && kbpsOverride == null)
            {
                a.Add("-crf"); a.Add(crf.ToString());
                if (s.Codec == VCodec.VP9) { a.Add("-b:v"); a.Add("0"); }
            }
            else
            {
                a.Add("-b:v"); a.Add(kbps + "k");
                a.Add("-maxrate"); a.Add((int)(kbps * 1.45) + "k");
                a.Add("-bufsize"); a.Add(kbps * 2 + "k");
            }

            if (pass > 0)
            {
                a.Add("-pass"); a.Add(pass.ToString());
                a.Add("-passlogfile"); a.Add(Path.Combine(Ff.TempDir, "cdpass"));
            }

            a.Add("-pix_fmt"); a.Add(string.IsNullOrEmpty(s.PixFmt) ? "yuv420p" : s.PixFmt);
            if (s.Codec == VCodec.H265 && s.Out == OutKind.Mp4) { a.Add("-tag:v"); a.Add("hvc1"); }
        }

        static void AddAudio(List<string> a, MediaInfo m, EncodeSettings s, int pass)
        {
            if (!m.HasAudio || s.Audio == AudioMode.Remove || pass == 1) { a.Add("-an"); return; }
            if (s.Audio == AudioMode.Copy) { a.Add("-c:a"); a.Add("copy"); return; }

            a.Add("-c:a"); a.Add(AudioCodecName(s.ACodec));
            a.Add("-b:a"); a.Add(s.AudioKbps + "k");
            if (s.AudioChannels > 0) { a.Add("-ac"); a.Add(s.AudioChannels.ToString()); }
        }

        // ------------------------------------------------------------------
        //  the builder
        // ------------------------------------------------------------------
        /// <param name="pass">0 = single pass, 1 / 2 = two-pass.</param>
        /// <param name="sampleStart">Override trim start (sample encoding).</param>
        /// <param name="sampleDur">Override duration (sample encoding).</param>
        public static List<string> Build(MediaInfo m, EncodeSettings s, string outPath,
                                         Stage stages = Stage.All, int pass = 0,
                                         double? sampleStart = null, double? sampleDur = null,
                                         int? kbpsOverride = null)
        {
            var a = new List<string> { "-y" };
            var fam = Family(s.Out);

            double start = sampleStart ?? (stages.HasFlag(Stage.Trim) ? s.TrimStart : 0);
            double dur = sampleDur ?? (stages.HasFlag(Stage.Trim) ? OutDuration(m, s) : m.Duration);

            // Still output pulled out of a video: FrameTime is the seek point, not TrimStart.
            if (fam == OutFamily.Image && !m.IsStill && sampleStart == null)
                start = Math.Max(0, Math.Min(s.FrameTime, Math.Max(0, m.Duration - 0.05)));

            if (start > 0.001) { a.Add("-ss"); a.Add(N(start)); }

            a.Add("-i"); a.Add(m.Path);

            if (fam != OutFamily.Image && !m.IsStill && dur > 0 && dur < m.Duration - 0.01)
            { a.Add("-t"); a.Add(N(dur)); }

            var baseFilter = BaseFilter(m, s, stages);

            bool quantise = fam == OutFamily.Gif ||
                            (s.Out == OutKind.Png && s.UsePalette && stages.HasFlag(Stage.Color));

            if (quantise)
            {
                a.Add("-filter_complex");
                a.Add(PaletteChain(s, baseFilter, fam == OutFamily.Gif && !m.IsStill));
            }
            else if (!string.IsNullOrEmpty(baseFilter))
            {
                a.Add("-vf"); a.Add(baseFilter);
            }

            switch (fam)
            {
                case OutFamily.Gif:
                    a.Add("-loop"); a.Add(s.GifLoop.ToString());
                    if (m.IsStill) { a.Add("-frames:v"); a.Add("1"); }
                    a.Add("-an");
                    break;

                case OutFamily.Image:
                    a.Add("-frames:v"); a.Add("1");
                    a.Add("-an");
                    if (s.Out == OutKind.Jpg)
                    {
                        a.Add("-c:v"); a.Add("mjpeg");
                        var q = 2 + (100 - Math.Max(1, Math.Min(100, s.JpegQuality))) * 29.0 / 99.0;
                        a.Add("-q:v"); a.Add(((int)Math.Round(q)).ToString());
                        a.Add("-pix_fmt"); a.Add(s.Grayscale ? "gray" : "yuvj420p");
                    }
                    else if (s.Out == OutKind.Png)
                    {
                        a.Add("-c:v"); a.Add("png");
                        a.Add("-compression_level"); a.Add(Math.Max(0, Math.Min(9, s.PngLevel)).ToString());
                    }
                    else
                    {
                        a.Add("-c:v"); a.Add("libwebp");
                        a.Add("-lossless"); a.Add(s.WebpLossless ? "1" : "0");
                        a.Add("-quality"); a.Add(Math.Max(0, Math.Min(100, s.WebpQuality)).ToString());
                        a.Add("-compression_level"); a.Add("6");
                    }
                    break;

                default:
                    if (m.IsStill) { a.Add("-frames:v"); a.Add("1"); }
                    AddRate(a, m, s, kbpsOverride, pass);
                    AddAudio(a, m, s, pass);
                    if (s.Out == OutKind.Mp4 && s.FastStart) { a.Add("-movflags"); a.Add("+faststart"); }
                    break;
            }

            if (s.StripMetadata) { a.Add("-map_metadata"); a.Add("-1"); }

            if (pass == 1) { a.Add("-f"); a.Add("null"); a.Add("NUL"); }
            else a.Add(outPath);

            return a;
        }

        /// <summary>Solves the video bitrate needed to land on a target file size.</summary>
        public static int SolveKbps(MediaInfo m, EncodeSettings s, double targetBytes)
        {
            var dur = Math.Max(0.05, OutDuration(m, s));
            var budgetKbits = targetBytes * 8 / 1000.0 * 0.92;   // headroom for container overhead
            int audio = 0;
            if (m.HasAudio && s.Audio != AudioMode.Remove)
                audio = s.Audio == AudioMode.Copy ? (int)(m.AudioBitRate / 1000) : s.AudioKbps;

            var v = (int)Math.Floor(budgetKbits / dur) - audio;
            return Math.Max(4, v);
        }

        public static string OutputPath(MediaInfo m, EncodeSettings s)
        {
            var dir = string.IsNullOrWhiteSpace(s.OutDir) ? Path.GetDirectoryName(m.Path) : s.OutDir;
            var name = Path.GetFileNameWithoutExtension(m.Path) + s.Suffix + Ext(s.Out);
            var p = Path.Combine(dir ?? "", name);
            if (string.Equals(p, m.Path, StringComparison.OrdinalIgnoreCase))
                p = Path.Combine(dir ?? "", Path.GetFileNameWithoutExtension(m.Path) + s.Suffix + "1" + Ext(s.Out));
            return p;
        }
    }
}
