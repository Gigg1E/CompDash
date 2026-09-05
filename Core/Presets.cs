using System;
using System.Collections.Generic;

namespace CompDash.Core
{
    public sealed class Preset
    {
        public string Name;
        public string Detail;
        public string Origin;                 // which C:\Tools script this came from
        public Action<MediaInfo, EncodeSettings> Apply;
    }

    /// <summary>
    /// The fixed-setting behaviours of the C:\Tools scripts, restated as one-click
    /// starting points that every knob on the dashboard can then override.
    /// </summary>
    public static class Presets
    {
        public static List<Preset> All = new List<Preset>
        {
            new Preset
            {
                Name = "Compress (CRF 28)",
                Detail = "x264 CRF 28, preset slower, AAC 128k, faststart. Same resolution.",
                Origin = "compress.bat",
                Apply = (m, s) =>
                {
                    s.Out = m.Ext == ".mkv" ? OutKind.Mkv : OutKind.Mp4;
                    s.Scale = ScaleMode.Original;
                    s.ChangeFps = false;
                    s.Codec = VCodec.H264; s.Rate = RateMode.Quality; s.Crf = 28; s.Preset = "slower";
                    s.Audio = AudioMode.Reencode; s.ACodec = ACodec.Aac; s.AudioKbps = 128;
                    s.FastStart = true; s.Suffix = "_compressed";
                }
            },
            new Preset
            {
                Name = "Compress GIF (10fps / 480px / 64c)",
                Detail = "The gif branch of compress.bat: 10 fps, 480px wide, 64 colours, no dither.",
                Origin = "compress.bat",
                Apply = (m, s) =>
                {
                    s.Out = OutKind.Gif;
                    s.Scale = ScaleMode.Width; s.TargetWidth = Math.Min(480, Math.Max(2, m.Width));
                    s.ChangeFps = true; s.Fps = 10;
                    s.PaletteColors = 64; s.PaletteStats = "diff"; s.Dither = "none";
                    s.Suffix = "_compressed";
                }
            },
            new Preset
            {
                Name = "Squeeze to 500 KB",
                Detail = "Solves the bitrate to land under 500 KB, two-pass, original resolution.",
                Origin = "squeeze.ps1",
                Apply = (m, s) =>
                {
                    s.Out = m.Ext == ".mkv" ? OutKind.Mkv : OutKind.Mp4;
                    s.Scale = ScaleMode.Original;
                    s.Codec = VCodec.H264; s.Rate = RateMode.TargetSize;
                    s.TargetMB = 500.0 / 1024.0; s.TwoPass = true; s.Preset = "medium";
                    s.Audio = AudioMode.Reencode; s.AudioKbps = 64;
                    s.Suffix = "_squeezed";
                }
            },
            new Preset
            {
                Name = "Convert to GIF",
                Detail = "10 fps, 480px wide, full palette — the old convert_to_gif defaults.",
                Origin = "convert_to_gif.bat",
                Apply = (m, s) =>
                {
                    s.Out = OutKind.Gif;
                    s.Scale = ScaleMode.Width; s.TargetWidth = Math.Min(480, Math.Max(2, m.Width));
                    s.ChangeFps = true; s.Fps = 10;
                    s.PaletteColors = 256; s.PaletteStats = "diff"; s.Dither = "sierra2_4a";
                    s.Suffix = "";
                }
            },
            new Preset
            {
                Name = "Convert to MP4",
                Detail = "H.264 + AAC, faststart, quality-preserving CRF 20.",
                Origin = "convert_to_mp4.bat",
                Apply = (m, s) =>
                {
                    s.Out = OutKind.Mp4;
                    s.Scale = ScaleMode.Original; s.ChangeFps = false;
                    s.Codec = VCodec.H264; s.Rate = RateMode.Quality; s.Crf = 20; s.Preset = "medium";
                    s.Audio = m.HasAudio ? AudioMode.Reencode : AudioMode.Remove;
                    s.FastStart = true; s.Suffix = "";
                }
            },
            new Preset
            {
                Name = "Convert to MKV",
                Detail = "H.264 + AAC in Matroska, CRF 20.",
                Origin = "convert_to_mkv.bat",
                Apply = (m, s) =>
                {
                    s.Out = OutKind.Mkv;
                    s.Scale = ScaleMode.Original; s.ChangeFps = false;
                    s.Codec = VCodec.H264; s.Rate = RateMode.Quality; s.Crf = 20; s.Preset = "medium";
                    s.Audio = m.HasAudio ? AudioMode.Reencode : AudioMode.Remove;
                    s.Suffix = "";
                }
            },
            new Preset
            {
                Name = "Web 720p",
                Detail = "720p H.264 CRF 24, AAC 128k, faststart — safe to post anywhere.",
                Origin = "new",
                Apply = (m, s) =>
                {
                    s.Out = OutKind.Mp4;
                    s.Scale = ScaleMode.Width; s.TargetWidth = 1280;
                    s.Codec = VCodec.H264; s.Rate = RateMode.Quality; s.Crf = 24; s.Preset = "medium";
                    s.Audio = AudioMode.Reencode; s.ACodec = ACodec.Aac; s.AudioKbps = 128;
                    s.FastStart = true; s.Suffix = "_720p";
                }
            },
            new Preset
            {
                Name = "Fit 10 MB",
                Detail = "Two-pass to exactly under 10 MB — for upload limits.",
                Origin = "new",
                Apply = (m, s) =>
                {
                    s.Out = OutKind.Mp4;
                    s.Codec = VCodec.H264; s.Rate = RateMode.TargetSize; s.TargetMB = 10;
                    s.TwoPass = true; s.Preset = "medium";
                    s.Audio = AudioMode.Reencode; s.AudioKbps = 96;
                    s.FastStart = true; s.Suffix = "_10mb";
                }
            },
            new Preset
            {
                Name = "PNG to JPEG 85",
                Detail = "Re-encode a still as JPEG at quality 85, metadata stripped.",
                Origin = "new",
                Apply = (m, s) =>
                {
                    s.Out = OutKind.Jpg; s.JpegQuality = 85;
                    s.Scale = ScaleMode.Original; s.StripMetadata = true; s.Suffix = "_q85";
                }
            },
            new Preset
            {
                Name = "Still to WebP 80",
                Detail = "WebP quality 80 — usually a third of the JPEG for the same look.",
                Origin = "new",
                Apply = (m, s) =>
                {
                    s.Out = OutKind.WebP; s.WebpQuality = 80; s.WebpLossless = false;
                    s.Scale = ScaleMode.Original; s.StripMetadata = true; s.Suffix = "_webp";
                }
            },
            new Preset
            {
                Name = "Shrink PNG (palette 128)",
                Detail = "Quantise a PNG to 128 colours with max deflate — lossless-looking, far smaller.",
                Origin = "new",
                Apply = (m, s) =>
                {
                    s.Out = OutKind.Png; s.UsePalette = true; s.PaletteColors = 128;
                    s.Dither = "sierra2_4a"; s.PngLevel = 9; s.Suffix = "_small";
                }
            },
        };
    }

    /// <summary>The C:\Tools scripts the Toolbox tab can launch, and what they do.</summary>
    public sealed class ExternalTool
    {
        public string Name, Description, Path, Kind;
    }

    public static class Toolbox
    {
        public const string Root = @"C:\Tools";

        public static List<ExternalTool> All = new List<ExternalTool>
        {
            new ExternalTool { Name = "Disk Analyzer", Kind = "ps1",
                Path = @"C:\Tools\disk-analyzer.ps1",
                Description = "Scans a drive or folder and writes an interactive treemap HTML report of what is eating space." },
            new ExternalTool { Name = "TREAI", Kind = "ps1",
                Path = @"C:\Tools\TREAI.ps1",
                Description = "Walks a source tree, samples the files and asks a local Ollama model to summarise the project into an HTML report." },
            new ExternalTool { Name = "Install Explorer menu", Kind = "reg",
                Path = @"C:\Tools\install_context_menu.reg",
                Description = "Adds Convert / Compress / Squeeze entries to the right-click menu for .mp4, .mkv and .gif." },
            new ExternalTool { Name = "Remove Explorer menu", Kind = "reg",
                Path = @"C:\Tools\uninstall_context_menu.reg",
                Description = "Removes those right-click entries again." },
        };
    }
}
