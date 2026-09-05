using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CompDash.Core
{
    public sealed class RunResult
    {
        public int ExitCode;
        public string Log = "";
        public bool Canceled;
        public bool Ok => ExitCode == 0 && !Canceled;
    }

    /// <summary>Thin wrapper around the ffmpeg / ffprobe executables.</summary>
    public static class Ff
    {
        public static string FfmpegPath { get; private set; }
        public static string FfprobePath { get; private set; }

        public static string TempDir
        {
            get
            {
                var d = Path.Combine(Path.GetTempPath(), "CompDash");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static bool Locate()
        {
            FfmpegPath = Which("ffmpeg.exe");
            FfprobePath = Which("ffprobe.exe");
            return FfmpegPath != null && FfprobePath != null;
        }

        static string Which(string exe)
        {
            var extra = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "Microsoft", "WinGet", "Links"),
                @"C:\ffmpeg\bin",
                @"C:\Program Files\ffmpeg\bin",
                @"C:\Tools",
            };
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try { var p = Path.Combine(dir.Trim(), exe); if (File.Exists(p)) return p; } catch { }
            }
            foreach (var dir in extra)
            {
                try { var p = Path.Combine(dir, exe); if (File.Exists(p)) return p; } catch { }
            }
            return null;
        }

        // ------------------------------------------------------------------
        //  probing
        // ------------------------------------------------------------------
        public static async Task<MediaInfo> ProbeAsync(string file)
        {
            var mi = new MediaInfo
            {
                Path = file,
                Ext = Path.GetExtension(file).ToLowerInvariant()
            };
            try { mi.SizeBytes = new FileInfo(file).Length; } catch { }

            if (FfprobePath == null) { mi.Error = "ffprobe not found"; return mi; }

            var args = new List<string>
            {
                "-v", "quiet", "-print_format", "json",
                "-show_format", "-show_streams", file
            };

            string json;
            try { json = await CaptureAsync(FfprobePath, args, CancellationToken.None); }
            catch (Exception ex) { mi.Error = ex.Message; return mi; }

            if (string.IsNullOrWhiteSpace(json)) { mi.Error = "no probe output"; return mi; }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("format", out var f))
                {
                    mi.FormatName = Str(f, "format_name");
                    mi.Duration = Dbl(f, "duration");
                    mi.BitRate = Lng(f, "bit_rate");
                }

                if (root.TryGetProperty("streams", out var streams))
                {
                    foreach (var s in streams.EnumerateArray())
                    {
                        var type = Str(s, "codec_type");
                        if (type == "video" && !mi.HasVideo)
                        {
                            mi.HasVideo = true;
                            mi.VideoCodec = Str(s, "codec_name");
                            mi.PixFmt = Str(s, "pix_fmt");
                            mi.Width = (int)Lng(s, "width");
                            mi.Height = (int)Lng(s, "height");
                            mi.VideoBitRate = Lng(s, "bit_rate");
                            mi.NbFrames = Lng(s, "nb_frames");
                            mi.Fps = Rational(Str(s, "avg_frame_rate"));
                            if (mi.Fps <= 0) mi.Fps = Rational(Str(s, "r_frame_rate"));
                            var sd = Dbl(s, "duration");
                            if (mi.Duration <= 0 && sd > 0) mi.Duration = sd;
                        }
                        else if (type == "audio" && !mi.HasAudio)
                        {
                            mi.HasAudio = true;
                            mi.AudioCodec = Str(s, "codec_name");
                            mi.AudioBitRate = Lng(s, "bit_rate");
                            mi.AudioChannels = (int)Lng(s, "channels");
                            mi.AudioSampleRate = (int)Lng(s, "sample_rate");
                        }
                    }
                }
            }
            catch (Exception ex) { mi.Error = ex.Message; return mi; }

            if (mi.AudioBitRate <= 0 && mi.HasAudio) mi.AudioBitRate = 128000;
            if (!mi.HasAudio) mi.AudioBitRate = 0;

            var stillExt = mi.Ext == ".png" || mi.Ext == ".jpg" || mi.Ext == ".jpeg" ||
                           mi.Ext == ".bmp" || mi.Ext == ".tif" || mi.Ext == ".tiff" ||
                           mi.Ext == ".webp" || mi.Ext == ".avif" || mi.Ext == ".heic";

            mi.IsStill = mi.HasVideo && !mi.HasAudio && (stillExt || mi.NbFrames == 1) &&
                         !(mi.Ext == ".gif" && mi.Duration > 0.05) &&
                         !(mi.Ext == ".webp" && mi.NbFrames > 1);

            if (mi.IsStill) { mi.Duration = 0; mi.Fps = 0; }
            if (!mi.IsStill && mi.Duration <= 0 && mi.NbFrames > 1 && mi.Fps > 0)
                mi.Duration = mi.NbFrames / mi.Fps;

            return mi;
        }

        static string Str(JsonElement e, string k) =>
            e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        static double Dbl(JsonElement e, string k)
        {
            if (!e.TryGetProperty(k, out var v)) return 0;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
            return 0;
        }

        static long Lng(JsonElement e, string k) => (long)Dbl(e, k);

        static double Rational(string r)
        {
            if (string.IsNullOrWhiteSpace(r)) return 0;
            var p = r.Split('/');
            if (p.Length == 2 &&
                double.TryParse(p[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var a) &&
                double.TryParse(p[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var b) && b != 0)
                return a / b;
            double.TryParse(r, NumberStyles.Any, CultureInfo.InvariantCulture, out var v);
            return v;
        }

        // ------------------------------------------------------------------
        //  running
        // ------------------------------------------------------------------
        public static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            return s.IndexOfAny(new[] { ' ', '\t', '"', '&', '^', '%', '(', ')', ',', ';' }) < 0
                ? s
                : "\"" + s.Replace("\"", "\\\"") + "\"";
        }

        public static string CommandLine(string exe, IEnumerable<string> args)
        {
            var sb = new StringBuilder(Quote(exe));
            foreach (var a in args) { sb.Append(' '); sb.Append(Quote(a)); }
            return sb.ToString();
        }

        static ProcessStartInfo Psi(string exe, IEnumerable<string> args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            return psi;
        }

        static async Task<string> CaptureAsync(string exe, IEnumerable<string> args, CancellationToken ct)
        {
            using var p = new Process { StartInfo = Psi(exe, args) };
            p.Start();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return stdout.Result;
        }

        /// <summary>Runs ffmpeg, reporting 0..1 progress parsed from -progress pipe:1.</summary>
        public static async Task<RunResult> RunAsync(IEnumerable<string> args, double expectedSeconds,
                                                     Action<double> onProgress, Action<string> onLog,
                                                     CancellationToken ct)
        {
            var res = new RunResult();
            var full = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-progress", "pipe:1" };
            full.AddRange(args);

            var log = new StringBuilder();
            using var p = new Process { StartInfo = Psi(FfmpegPath, full) };
            p.Start();

            var errTask = Task.Run(async () =>
            {
                string line;
                while ((line = await p.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    lock (log) log.AppendLine(line);
                    onLog?.Invoke(line);
                }
            });

            var outTask = Task.Run(async () =>
            {
                string line;
                while ((line = await p.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (onProgress == null || expectedSeconds <= 0) continue;
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
                    {
                        if (long.TryParse(line.Substring(12), out var us) && us >= 0)
                            onProgress(Math.Min(1.0, us / 1000000.0 / expectedSeconds));
                    }
                    else if (line.StartsWith("progress=end", StringComparison.Ordinal))
                        onProgress(1.0);
                }
            });

            try
            {
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(true); } catch { }
                res.Canceled = true;
            }

            try { await Task.WhenAll(errTask, outTask).ConfigureAwait(false); } catch { }
            res.ExitCode = res.Canceled ? -1 : p.ExitCode;
            lock (log) res.Log = log.ToString();
            return res;
        }

        /// <summary>Fire-and-wait helper for short internal jobs (samples, previews).</summary>
        public static async Task<bool> QuietAsync(IEnumerable<string> args, CancellationToken ct)
        {
            var r = await RunAsync(args, 0, null, null, ct).ConfigureAwait(false);
            return r.Ok;
        }
    }
}
