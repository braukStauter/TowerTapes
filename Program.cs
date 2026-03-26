using System.Windows.Forms;
using Microsoft.Win32;

namespace TowerTapes;

static class Program
{
    private static Mutex? _mutex;
    private const string StartupKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "TowerTapes";

    [STAThread]
    static void Main()
    {
        _mutex = new Mutex(true, @"Global\TowerTapes_SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("TowerTapes is already running.", "TowerTapes",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Apply startup setting on launch
        var config = Config.Load();
        SetStartupEnabled(config.LaunchAtStartup);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());

        GC.KeepAlive(_mutex);
    }

    public static void SetStartupEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKey, writable: true);
            if (key == null) return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(AppName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKey);
            return key?.GetValue(AppName) != null;
        }
        catch { return false; }
    }
}
