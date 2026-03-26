using System.Text.Json;

namespace TowerTapes;

public class Config
{
    private static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TowerTapes");
    private static readonly string ConfigPath = Path.Combine(AppDir, "config.json");

    public bool LaunchAtStartup { get; set; } = true;
    public bool PttEnabled { get; set; }
    public string PttKey { get; set; } = "Oemtilde";
    public string? MicDeviceId { get; set; }
    public int OpusBitrateKbps { get; set; } = 24;
    public int MaxStorageMB { get; set; } = 1024;
    public int RetentionDays { get; set; } = 90;

    public string RecordingsPath => Path.Combine(AppDir, "recordings");

    public static Config Load()
    {
        Directory.CreateDirectory(AppDir);
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<Config>(json) ?? new Config();
            }
            catch { }
        }
        var config = new Config();
        config.Save();
        return config;
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDir);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, opts));
    }
}
