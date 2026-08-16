using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ronaldo.Stats;

namespace ronaldo;

/// <summary>
/// User preferences, kept in %LOCALAPPDATA%\ronaldo\settings.json so toggles and filters
/// survive a restart.
/// </summary>
public class AppSettings
{
    public bool AutoAccept { get; set; } = true;
    public bool AutoApplyRunes { get; set; } = true;
    public bool ImportItemSets { get; set; }

    public StatsRank Rank { get; set; } = StatsRank.DiamondPlus;
    public StatsRegion Region { get; set; } = StatsRegion.World;

    /// <summary>The forced role, or null for "Auto".</summary>
    public Lane? Lane { get; set; }

    // Window placement, so the app reopens where it was left — including on a second monitor.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ronaldo", "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Reads saved settings, falling back to defaults if the file is missing or bad.</summary>
    public static AppSettings Load()
    {
        try
        {
            string path = FilePath;
            if (!File.Exists(path)) return new AppSettings();

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options)
                   ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>Writes settings out. Failures are ignored: preferences are not worth a crash.</summary>
    public void Save()
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch { }
    }
}
