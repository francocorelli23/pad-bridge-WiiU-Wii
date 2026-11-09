using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using PadBridge.UI;

namespace PadBridge
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Contains("--selftest"))
                return SelfTest.Run();

            using (var mutex = new Mutex(true, "PadBridge.SingleInstance", out bool first))
            {
                if (!first)
                {
                    MessageBox.Show("PadBridge is already running (check the system tray).", "PadBridge",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => MessageBox.Show(e.Exception.ToString(), "PadBridge error");
                bool minimized = args.Contains("--minimized");
                Application.Run(new MainForm(minimized));
            }
            return 0;
        }

        public static bool OtherDriverAppRunning(out string name)
        {
            foreach (var n in new[] { "WiinUSoft", "ScpMonitor", "SCPUser" })
            {
                if (Process.GetProcessesByName(n).Length > 0) { name = n; return true; }
            }
            name = null;
            return false;
        }
    }
}
