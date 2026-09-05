using System;
using System.Windows;
using System.Windows.Threading;

namespace CompDash
{
    public partial class App : Application
    {
        /// <summary>Files passed on the command line, so CompDash.exe "clip.mp4" opens with it queued.</summary>
        public static string[] StartupFiles = new string[0];

        /// <summary>--selftest: drive every output format through the real UI code, then quit.</summary>
        public static bool SelfTest;

        protected override void OnStartup(StartupEventArgs e)
        {
            var args = new System.Collections.Generic.List<string>(e.Args ?? new string[0]);
            SelfTest = args.RemoveAll(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)) > 0;
            StartupFiles = args.ToArray();

            DispatcherUnhandledException += (s, a) =>
            {
                MessageBox.Show(a.Exception.ToString(), "CompDash error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                a.Handled = true;
            };
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { Core.Estimator.CleanTemp(); } catch { }
            base.OnExit(e);
        }
    }
}
