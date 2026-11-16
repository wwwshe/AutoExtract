using System.Text.Json;

namespace AutoExtract.Core;

public sealed class WatchFolder
{
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool IncludeSubdirectories { get; set; }
}
public sealed class AppSettings
{
    public List<WatchFolder> Folders { get; set; } = [];
    public string BandizipPath { get; set; } = "";
    public bool RecycleOriginal { get; set; } = true;
    public bool AutoStart { get; set; }
    public bool Paused { get; set; }
}
public sealed record JobRecord(string File, string Signature, DateTimeOffset Time, string Status, string Detail, string? Output = null);
public sealed class AppState
{
    public List<JobRecord> History { get; set; } = [];
    public Dictionary<string, string> Completed { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> OutputFolders { get; set; } = [];
}
public sealed class SettingsService
{
    public string Root { get; }
    public SettingsService(string? root = null) { Root = root ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoExtract"); Directory.CreateDirectory(Root); }
    public T Load<T>(string name) where T : new()
    {
        var path = System.IO.Path.Combine(Root, name);
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("빈 설정 파일"); }
        catch (Exception e) when (e is JsonException or InvalidDataException) { throw new InvalidDataException($"설정 손상: {path}. 원본 파일을 백업한 뒤 복구하세요.", e); }
    }
    public void Save<T>(string name, T value)
    {
        var path = System.IO.Path.Combine(Root, name);
        var temp = path + ".new";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
        File.Move(temp, path, true);
    }
}
public interface IArchiveEngine
{
    Task TestAsync(string archive, string workspace, CancellationToken token);
    Task ExtractAsync(string archive, string destination, string workspace, CancellationToken token);
}
public interface IFileRecycleService { void Recycle(string path); }
