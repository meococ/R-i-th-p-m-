using System.Text.Json;
using System.IO;
using Autodesk.Revit.DB;
using BIM.DatViet.Models;

namespace BIM.DatViet.Infrastructure;

public sealed class ManholeUserSettings
{
    public ManholeRebarPreset Preset { get; set; } = new();
    public double W1X { get; set; }
    public double W1Y { get; set; } = 1;
    public double W1Z { get; set; }
}

public static class ManholeSettingsStore
{
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DVB_ADDIN", "ManholeRebar", "settings.json");

    public static ManholeUserSettings Load(Document document, Element primary)
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new ManholeUserSettings();
            var all = JsonSerializer.Deserialize<Dictionary<string, ManholeUserSettings>>(File.ReadAllText(SettingsPath));
            return all is not null && all.TryGetValue(Key(document, primary), out var value)
                ? value : new ManholeUserSettings();
        }
        catch
        {
            return new ManholeUserSettings();
        }
    }

    public static void Save(Document document, Element primary, ManholeRebarPreset preset, XYZ w1Outward)
    {
        try
        {
            var folder = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(folder);
            Dictionary<string, ManholeUserSettings> all;
            try
            {
                all = File.Exists(SettingsPath)
                    ? JsonSerializer.Deserialize<Dictionary<string, ManholeUserSettings>>(File.ReadAllText(SettingsPath)) ?? []
                    : [];
            }
            catch { all = []; }
            all[Key(document, primary)] = new ManholeUserSettings
            {
                Preset = preset.Clone(), W1X = w1Outward.X, W1Y = w1Outward.Y, W1Z = w1Outward.Z
            };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // User settings are convenient state only; failure must not mutate or block the model.
        }
    }

    private static string Key(Document document, Element primary) =>
        $"{document.PathName}|{document.Title}|{primary.UniqueId}";
}
