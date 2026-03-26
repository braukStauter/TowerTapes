namespace TowerTapes;

public record RecordingInfo(
    string FilePath, DateTime StartTime, long SizeBytes, TimeSpan EstimatedDuration);

public class StorageManager
{
    private readonly Config _config;

    public StorageManager(Config config) => _config = config;

    public void EnsureDirectory() => Directory.CreateDirectory(_config.RecordingsPath);

    public void Cleanup()
    {
        EnsureDirectory();
        var cutoff = DateTime.Now.AddDays(-_config.RetentionDays);

        foreach (var f in GetOggFiles().Where(f => f.LastWriteTime < cutoff))
            TryDelete(f);

        var remaining = GetOggFiles().OrderBy(f => f.LastWriteTime).ToList();
        long total = remaining.Sum(f => f.Length);
        long max = (long)_config.MaxStorageMB * 1024 * 1024;

        while (total > max && remaining.Count > 0)
        {
            total -= remaining[0].Length;
            TryDelete(remaining[0]);
            remaining.RemoveAt(0);
        }

        foreach (var dir in Directory.GetDirectories(_config.RecordingsPath))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                try { Directory.Delete(dir); } catch { }
        }
    }

    public List<RecordingInfo> GetRecordings()
    {
        EnsureDirectory();
        var results = new List<RecordingInfo>();
        foreach (var file in GetOggFiles().OrderByDescending(f => f.LastWriteTime))
        {
            if (TryParseTimestamp(file, out var startTime))
            {
                double seconds = file.Length / ((_config.OpusBitrateKbps * 1000.0) / 8.0);
                results.Add(new RecordingInfo(
                    file.FullName, startTime, file.Length,
                    TimeSpan.FromSeconds(Math.Max(seconds, 1))));
            }
        }
        return results;
    }

    public long GetTotalSizeBytes()
    {
        EnsureDirectory();
        return GetOggFiles().Sum(f => f.Length);
    }

    public string GenerateFilePath(DateTime startTime)
    {
        var dateDir = Path.Combine(_config.RecordingsPath, startTime.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dateDir);
        return Path.Combine(dateDir, $"session_{startTime:yyyy-MM-dd_HH-mm-ss}.ogg");
    }

    private FileInfo[] GetOggFiles()
    {
        try
        {
            return new DirectoryInfo(_config.RecordingsPath)
                .GetFiles("*.ogg", SearchOption.AllDirectories);
        }
        catch { return []; }
    }

    private static bool TryParseTimestamp(FileInfo file, out DateTime result)
    {
        result = default;
        var name = Path.GetFileNameWithoutExtension(file.Name);
        if (!name.StartsWith("session_")) return false;
        return DateTime.TryParseExact(name[8..], "yyyy-MM-dd_HH-mm-ss",
            null, System.Globalization.DateTimeStyles.None, out result);
    }

    private static void TryDelete(FileInfo f)
    {
        try { f.Delete(); } catch { }
    }
}
