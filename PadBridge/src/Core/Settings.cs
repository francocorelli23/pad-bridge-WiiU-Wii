using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace PadBridge.Core
{
    internal sealed class Settings
    {
        public bool PositionalLayout = true;
        public bool Rumble = true;
        public int DeadzonePercent = 10;
        public int IdleOffMinutes = 15;      // 0 = never
        public bool MinimizeToTray = true;
        public bool StartMinimized = false;

        private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PadBridge");
        private static string FilePath => Path.Combine(Dir, "settings.ini");
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "PadBridge";

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                bool B(string k, bool def) => d.TryGetValue(k, out var v) && bool.TryParse(v, out var b) ? b : def;
                int I(string k, int def) => d.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : def;
                s.PositionalLayout = B(nameof(PositionalLayout), s.PositionalLayout);
                s.Rumble = B(nameof(Rumble), s.Rumble);
                s.DeadzonePercent = Math.Max(0, Math.Min(50, I(nameof(DeadzonePercent), s.DeadzonePercent)));
                s.IdleOffMinutes = Math.Max(0, Math.Min(240, I(nameof(IdleOffMinutes), s.IdleOffMinutes)));
                s.MinimizeToTray = B(nameof(MinimizeToTray), s.MinimizeToTray);
                s.StartMinimized = B(nameof(StartMinimized), s.StartMinimized);
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, new[]
                {
                    $"{nameof(PositionalLayout)}={PositionalLayout}",
                    $"{nameof(Rumble)}={Rumble}",
                    $"{nameof(DeadzonePercent)}={DeadzonePercent}",
                    $"{nameof(IdleOffMinutes)}={IdleOffMinutes}",
                    $"{nameof(MinimizeToTray)}={MinimizeToTray}",
                    $"{nameof(StartMinimized)}={StartMinimized}",
                });
            }
            catch { }
        }

        public static bool StartWithWindows
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k?.GetValue(RunName) != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(RunName, $"\"{System.Windows.Forms.Application.ExecutablePath}\" --minimized");
                    else k.DeleteValue(RunName, false);
                }
            }
        }
    }
}
