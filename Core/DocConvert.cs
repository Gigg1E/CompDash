using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CompDash.Core
{
    /// <summary>
    /// Turns the file types PDFsharp cannot read into a temporary PDF, using whatever
    /// is installed: Edge headless for HTML, LibreOffice for Office documents.
    /// </summary>
    public static class DocConvert
    {
        public static string EdgePath { get; private set; }
        public static string LibreOfficePath { get; private set; }

        public static void Locate()
        {
            EdgePath = FirstExisting(
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe");

            LibreOfficePath = FirstExisting(
                @"C:\Program Files\LibreOffice\program\soffice.exe",
                @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             @"Programs\LibreOffice\program\soffice.exe"));
        }

        static string FirstExisting(params string[] paths)
        {
            foreach (var p in paths)
            {
                try { if (p != null && File.Exists(p)) return p; } catch { }
            }
            return null;
        }

        public static string WorkDir
        {
            get
            {
                var d = Path.Combine(Path.GetTempPath(), "CompDash", "pdf");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        static async Task<int> RunAsync(string exe, IEnumerable<string> args, int timeoutMs, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = new Process { StartInfo = psi };
            p.Start();
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            try { await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(true); } catch { }
                return -1;
            }
            return p.ExitCode;
        }

        /// <summary>HTML (or SVG) to PDF via headless Edge.</summary>
        public static async Task<string> HtmlToPdfAsync(string src, CancellationToken ct)
        {
            if (EdgePath == null) throw new InvalidOperationException("Microsoft Edge was not found.");

            var stamp = Guid.NewGuid().ToString("N").Substring(0, 10);
            var outPdf = Path.Combine(WorkDir, "html_" + stamp + ".pdf");
            // A throwaway profile, so this never collides with the Edge the user has open.
            var profile = Path.Combine(WorkDir, "edge_" + stamp);
            Directory.CreateDirectory(profile);

            var uri = new Uri(Path.GetFullPath(src)).AbsoluteUri;

            var args = new List<string>
            {
                "--headless=new",
                "--disable-gpu",
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-extensions",
                "--user-data-dir=" + profile,
                "--run-all-compositor-stages-before-draw",
                "--virtual-time-budget=10000",
                "--no-pdf-header-footer",
                "--print-to-pdf=" + outPdf,
                uri
            };

            await RunAsync(EdgePath, args, 90000, ct).ConfigureAwait(false);
            try { Directory.Delete(profile, true); } catch { }

            if (!File.Exists(outPdf) || new FileInfo(outPdf).Length == 0)
                throw new InvalidOperationException("Edge did not produce a PDF for " + Path.GetFileName(src));
            return outPdf;
        }

        /// <summary>Word / Excel / PowerPoint / OpenDocument to PDF via LibreOffice.</summary>
        public static async Task<string> OfficeToPdfAsync(string src, CancellationToken ct)
        {
            if (LibreOfficePath == null)
                throw new InvalidOperationException(
                    "LibreOffice was not found. Install it with: winget install TheDocumentFoundation.LibreOffice");

            var stamp = Guid.NewGuid().ToString("N").Substring(0, 10);
            var outDir = Path.Combine(WorkDir, "office_" + stamp);
            Directory.CreateDirectory(outDir);

            // Its own user profile, so converting works even with LibreOffice already open.
            var profile = new Uri(Path.Combine(outDir, "profile")).AbsoluteUri;

            var args = new List<string>
            {
                "-env:UserInstallation=" + profile,
                "--headless", "--norestore", "--invisible", "--nolockcheck",
                "--convert-to", "pdf",
                "--outdir", outDir,
                Path.GetFullPath(src)
            };

            await RunAsync(LibreOfficePath, args, 180000, ct).ConfigureAwait(false);

            var produced = Path.Combine(outDir, Path.GetFileNameWithoutExtension(src) + ".pdf");
            if (File.Exists(produced)) return produced;

            foreach (var f in Directory.GetFiles(outDir, "*.pdf")) return f;

            throw new InvalidOperationException(
                "LibreOffice did not produce a PDF for " + Path.GetFileName(src));
        }

        public static void CleanWorkDir()
        {
            try
            {
                foreach (var f in Directory.GetFiles(WorkDir)) { try { File.Delete(f); } catch { } }
                foreach (var d in Directory.GetDirectories(WorkDir)) { try { Directory.Delete(d, true); } catch { } }
            }
            catch { }
        }
    }
}
