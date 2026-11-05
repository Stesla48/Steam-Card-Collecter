using System.IO;
using System.Text.Json;

namespace SteamCardCollector.App;

internal sealed record Preferences(string? Language = null, bool LoginGuideSeen = false, bool DashboardGuideSeen = false)
{
    internal static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SteamCardCollector", "preferences.json");

    internal static Preferences Load(string path)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path));
            return saved?.Language is "en" or "tr" ? saved : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }
}
