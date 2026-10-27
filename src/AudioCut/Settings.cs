using System.IO;
using System.Text.Json;

namespace AudioCut;

public sealed class Settings
{
    public string Format { get; set; } = "mp3-320";
    public bool Fade { get; set; }
    public string? LastOpenDir { get; set; }
    public string? LastSaveDir { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioCut", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { /* settings are best-effort */ }
    }
}
