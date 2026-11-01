using System;
using System.Windows;

namespace EjectScope.App
{
    /// <summary>
    /// Entry point. STAThread is required by WPF. There is deliberately no
    /// command-line surface here beyond one developer flag
    /// (<c>--screenshot &lt;path&gt;</c>), and it only ever writes a PNG of the
    /// window: a GUI executable that can be driven from a script is a different
    /// security story than one a person clicks, so batch behaviour stays in the
    /// separate console executable.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        internal static void Main(string[] args)
        {
            string screenshot = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--screenshot" && i + 1 < args.Length)
                {
                    screenshot = args[i + 1];
                    i++;
                }
                else if (args[i].StartsWith("--screenshot=", StringComparison.Ordinal))
                {
                    screenshot = args[i].Substring("--screenshot=".Length);
                }
            }

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.Run(new MainWindow(screenshot));
        }
    }
}
