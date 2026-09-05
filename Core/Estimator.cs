using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CompDash.Core
{
    public sealed class Estimate
    {
        public long Bytes;
        public bool Exact;        // produced by really encoding the whole thing
        public bool Calibrated;   // produced by encoding real samples and extrapolating
        public string Note = "";
    }

    /// <summary>
    /// Two-tier size prediction.
    ///  * <see cref="Analytic"/> is instant and drives the per-section running totals.
    ///  * <see cref="CalibrateAsync"/> really encodes a few short slices with the exact
    ///    settings and extrapolates, which is what the big headline number shows.
    /// </summary>
    public static class Estimator
    {
        // ------------------------------------------------------------------
        //  instant model
        // ------------------------------------------------------------------

        /// <summary>x264-equivalent CRF, so codecs can be compared on one scale.</summary>
        public static double EquivCrf(VCodec c, int crf)
        {
            switch (c)
            {
                case VCodec.H265: return crf - 4.5;
                case VCodec.VP9: return crf - 8.0;
                case VCodec.AV1: return crf - 10.5;
                default: return crf;
            }
        }

        static double DitherPenalty(string d)
        {
            switch (d)
            {
                case "none": return 1.00;
                case "bayer": return 1.30;
                case "sierra2": return 1.58;
                case "sierra2_4a": return 1.62;
                case "floyd_steinberg": return 1.72;
                default: return 1.5;
            }
        }

        public static long Analytic(MediaInfo m, EncodeSettings s, Stage stages)
        {
            if (m == null || !m.HasVideo) return 0;
            switch (Cmd.Family(s.Out))
            {
                case OutFamily.Gif: return AnalyticGif(m, s, stages);
                case OutFamily.Image: return AnalyticImage(m, s, stages);
                default: return AnalyticVideo(m, s, stages);
            }
        }

        static void Dims(MediaInfo m, EncodeSettings s, Stage stages, out int w, out int h)
        {
            if (stages.HasFlag(Stage.Scale)) Cmd.OutputSize(m, s, out w, out h);
            else { w = Math.Max(2, m.Width); h = Math.Max(2, m.Height); }
        }

        static double Fps(MediaInfo m, EncodeSettings s, Stage stages)
        {
            var src = m.Fps > 0 ? m.Fps : 30;
            if (!stages.HasFlag(Stage.Fps) || !s.ChangeFps) return src;
            return Math.Max(0.5, Math.Min(src, s.Fps));
        }

        static long AnalyticVideo(MediaInfo m, EncodeSettings s, Stage stages)
        {
            var dur = stages.HasFlag(Stage.Trim) ? Cmd.OutDuration(m, s) : Math.Max(0.02, m.Duration);
            if (m.IsStill) dur = 1.0 / 30.0;

            // A target-size request answers itself.
            if (stages.HasFlag(Stage.Quality) && s.Rate == RateMode.TargetSize)
                return (long)(s.TargetMB * 1024 * 1024);

            double srcV = m.EffectiveVideoBps;
            Dims(m, s, stages, out var w, out var h);
            var fps = Fps(m, s, stages);

            double srcPix = Math.Max(1, (double)m.Width * m.Height * Math.Max(1, m.Fps > 0 ? m.Fps : 30));
            double dstPix = Math.Max(1, (double)w * h * Math.Max(1, fps));

            // Bitrate scales sub-linearly with pixel rate: halving fps does not halve size,
            // because the remaining frames carry more change each.
            double geom = Math.Pow(dstPix / srcPix, 0.85);

            double qf = 1.0;
            double videoBps;

            if (stages.HasFlag(Stage.Quality) && s.Rate == RateMode.Bitrate)
            {
                videoBps = s.VideoKbps * 1000.0;
            }
            else
            {
                if (stages.HasFlag(Stage.Quality))
                {
                    // Infer roughly how hard the source was already squeezed, then move from there.
                    double bppSrc = srcV / srcPix;
                    double crfSrc = 23 - 6 * Math.Log(Math.Max(1e-6, bppSrc) / 0.085, 2);
                    crfSrc = Math.Max(12, Math.Min(42, crfSrc));
                    qf = Math.Pow(2, (crfSrc - EquivCrf(s.Codec, s.Crf)) / 6.0);
                    qf = Math.Max(0.02, Math.Min(4.0, qf));
                }
                videoBps = srcV * geom * qf;
            }

            if (stages.HasFlag(Stage.Color))
            {
                if (s.Grayscale) videoBps *= 0.82;
                else if (s.Saturation < 1.0) videoBps *= 0.92 + 0.08 * s.Saturation;
                if (!string.IsNullOrEmpty(s.PixFmt) && s.PixFmt.Contains("10le")) videoBps *= 1.15;
                if (!string.IsNullOrEmpty(s.PixFmt) && s.PixFmt.Contains("444")) videoBps *= 1.35;
            }

            double audioBps = m.AudioBitRate;
            if (stages.HasFlag(Stage.Audio) && m.HasAudio)
            {
                if (s.Audio == AudioMode.Remove) audioBps = 0;
                else if (s.Audio == AudioMode.Reencode) audioBps = s.AudioKbps * 1000.0;
            }
            if (!m.HasAudio) audioBps = 0;

            var bytes = (videoBps + audioBps) / 8.0 * dur * 1.005 + 6000;
            return (long)Math.Max(1024, bytes);
        }

        static long AnalyticGif(MediaInfo m, EncodeSettings s, Stage stages)
        {
            var dur = stages.HasFlag(Stage.Trim) ? Cmd.OutDuration(m, s) : Math.Max(0.02, m.Duration);
            Dims(m, s, stages, out var w, out var h);
            var fps = Fps(m, s, stages);

            double frames = m.IsStill ? 1 : Math.Max(1, dur * fps);
            int colors = stages.HasFlag(Stage.Color) ? Math.Max(2, Math.Min(256, s.PaletteColors)) : 256;
            double dither = stages.HasFlag(Stage.Color) ? DitherPenalty(s.Dither) : 1.5;

            double bitsPerPx = Math.Log(colors, 2);
            const double lzw = 0.42;                 // typical LZW ratio on dithered photo content
            double interFrame = frames > 1 &&
                (!stages.HasFlag(Stage.Color) || s.PaletteStats == "diff") ? 0.55 : 0.85;

            var bytes = frames * w * h * bitsPerPx / 8.0 * lzw * interFrame * dither + 800;
            return (long)Math.Max(512, bytes);
        }

        static long AnalyticImage(MediaInfo m, EncodeSettings s, Stage stages)
        {
            Dims(m, s, stages, out var w, out var h);
            double px = (double)w * h;
            double bpp;

            switch (s.Out)
            {
                case OutKind.Jpg:
                    {
                        var q = stages.HasFlag(Stage.Quality) ? s.JpegQuality : 92;
                        bpp = 0.09 + 2.0 * Math.Pow(q / 100.0, 3.1);
                        break;
                    }
                case OutKind.WebP:
                    {
                        if (stages.HasFlag(Stage.Quality) && s.WebpLossless) bpp = 5.5;
                        else
                        {
                            var q = stages.HasFlag(Stage.Quality) ? s.WebpQuality : 90;
                            bpp = 0.06 + 1.5 * Math.Pow(q / 100.0, 3.0);
                        }
                        break;
                    }
                default: // PNG
                    {
                        if (stages.HasFlag(Stage.Color) && s.UsePalette)
                            bpp = Math.Log(Math.Max(2, s.PaletteColors), 2) * 0.30 * DitherPenalty(s.Dither);
                        else
                            bpp = 8.0;
                        var lvl = stages.HasFlag(Stage.Quality) ? s.PngLevel : 6;
                        bpp *= 1.0 + (9 - Math.Max(0, Math.Min(9, lvl))) * 0.02;
                        break;
                    }
            }

            if (stages.HasFlag(Stage.Color) && s.Grayscale) bpp *= 0.62;
            return (long)Math.Max(256, px * bpp / 8.0 + 400);
        }

        // ------------------------------------------------------------------
        //  the running per-section totals
        // ------------------------------------------------------------------
        public static readonly Stage[] Chain =
        {
            Stage.Trim,
            Stage.Trim | Stage.Scale,
            Stage.Trim | Stage.Scale | Stage.Fps,
            Stage.Trim | Stage.Scale | Stage.Fps | Stage.Color,
            Stage.Trim | Stage.Scale | Stage.Fps | Stage.Color | Stage.Quality,
            Stage.All
        };

        /// <summary>
        /// Cumulative estimates after each section.
        ///
        /// The first entry is the model's honest baseline — for video that is essentially the
        /// source itself — and each later entry adds one section's effect. When a calibrated
        /// total arrives, the difference between it and the model is spread across the sections
        /// in proportion to how much each one claims to change the size, on a log scale. A
        /// section that changes nothing keeps a multiplier of exactly 1, so the baseline is
        /// never inflated past the source just because the codec model was pessimistic.
        /// </summary>
        public static long[] Breakdown(MediaInfo m, EncodeSettings s, long calibratedTotal)
        {
            int n = Chain.Length;
            var raw = new double[n];
            for (int i = 0; i < n; i++) raw[i] = Math.Max(1, Analytic(m, s, Chain[i]));

            var vals = new long[n];
            if (calibratedTotal <= 0 || raw[n - 1] <= 0)
            {
                for (int i = 0; i < n; i++) vals[i] = (long)raw[i];
                return vals;
            }

            // How much the model thinks the sections change things, versus what we measured.
            double modelLog = Math.Log(raw[n - 1] / raw[0]);
            double trueLog = Math.Log(calibratedTotal / raw[0]);

            if (Math.Abs(modelLog) < 0.05)
            {
                // The model claims nothing changed, so put the whole correction on the last step.
                for (int i = 0; i < n - 1; i++) vals[i] = (long)raw[i];
                vals[n - 1] = calibratedTotal;
                return vals;
            }

            double alpha = trueLog / modelLog;
            alpha = Math.Max(0.05, Math.Min(20.0, alpha));

            double acc = raw[0];
            vals[0] = (long)acc;
            for (int i = 1; i < n; i++)
            {
                acc *= Math.Pow(raw[i] / raw[i - 1], alpha);   // a no-op section stays a no-op
                vals[i] = (long)Math.Max(256, acc);
            }
            vals[n - 1] = calibratedTotal;
            return vals;
        }

        // ------------------------------------------------------------------
        //  calibration by real sample encodes
        // ------------------------------------------------------------------
        public static async Task<Estimate> CalibrateAsync(MediaInfo m, EncodeSettings s, CancellationToken ct)
        {
            var est = new Estimate();
            if (m == null || !m.HasVideo) return null;

            var fam = Cmd.Family(s.Out);
            var outPath = Path.Combine(Ff.TempDir, "probe_" + Guid.NewGuid().ToString("N") + Cmd.Ext(s.Out));

            try
            {
                // Stills and image output are cheap enough to just encode for real.
                if (fam == OutFamily.Image || m.IsStill || Cmd.OutDuration(m, s) <= 0.05)
                {
                    var args = Cmd.Build(m, s, outPath);
                    if (!await Ff.QuietAsync(args, ct).ConfigureAwait(false)) return null;
                    est.Bytes = new FileInfo(outPath).Length;
                    est.Exact = true;
                    est.Note = "exact — encoded in full";
                    return est;
                }

                // Fixed-size requests answer themselves.
                if (s.Rate == RateMode.TargetSize && fam == OutFamily.Video)
                {
                    est.Bytes = (long)(s.TargetMB * 1024 * 1024);
                    est.Note = "target size — solved to " + Cmd.SolveKbps(m, s, est.Bytes) + " kbps";
                    return est;
                }

                var total = Cmd.OutDuration(m, s);

                // Short clips: encoding the whole thing IS the estimate.
                if (total <= 6.0)
                {
                    var args = Cmd.Build(m, s, outPath);
                    if (!await Ff.QuietAsync(args, ct).ConfigureAwait(false)) return null;
                    est.Bytes = new FileInfo(outPath).Length;
                    est.Exact = true;
                    est.Note = "exact — short clip encoded in full";
                    return est;
                }

                // Otherwise sample three slices and extrapolate.
                double sample = total >= 60 ? 2.0 : 1.5;
                var offsets = new[] { 0.15, 0.45, 0.75 };
                long sumBytes = 0;
                double sumDur = 0;

                foreach (var o in offsets)
                {
                    ct.ThrowIfCancellationRequested();
                    var start = s.TrimStart + total * o;
                    var dur = Math.Min(sample, Math.Max(0.3, s.TrimStart + total - start));
                    if (dur < 0.25) continue;

                    var p = Path.Combine(Ff.TempDir, "probe_" + Guid.NewGuid().ToString("N") + Cmd.Ext(s.Out));
                    var args = Cmd.Build(m, s, p, Stage.All, 0, start, dur);
                    var ok = await Ff.QuietAsync(args, ct).ConfigureAwait(false);
                    if (ok && File.Exists(p))
                    {
                        sumBytes += new FileInfo(p).Length;
                        sumDur += dur;
                    }
                    TryDelete(p);
                }

                if (sumDur < 0.2 || sumBytes <= 0) return null;

                // Per-slice container headers are counted three times; charge only one.
                long overhead = fam == OutFamily.Gif ? 800 : 3000;
                var payload = Math.Max(1, sumBytes - overhead * (offsets.Length - 1));

                est.Bytes = (long)(payload / sumDur * total) + overhead;
                est.Calibrated = true;
                est.Note = $"calibrated from {sumDur:0.#}s of real encoding";
                return est;
            }
            catch (OperationCanceledException) { return null; }
            catch { return null; }
            finally { TryDelete(outPath); }
        }

        static void TryDelete(string p)
        {
            try { if (p != null && File.Exists(p)) File.Delete(p); } catch { }
        }

        public static void CleanTemp()
        {
            try
            {
                foreach (var f in Directory.GetFiles(Ff.TempDir))
                {
                    var n = Path.GetFileName(f);
                    if (n.StartsWith("probe_") || n.StartsWith("prev_") || n.StartsWith("cdpass"))
                        TryDelete(f);
                }
            }
            catch { }
        }
    }
}
