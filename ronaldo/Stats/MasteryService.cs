using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ronaldo.Stats;

/// <summary>Champion mastery for one player on one champion.</summary>
public class MasteryInfo
{
    public int Level { get; set; }
    public long Points { get; set; }

    /// <summary>e.g. "M7 · 245K". Empty when the client gave us nothing.</summary>
    public string Display
    {
        get
        {
            if (Level <= 0 && Points <= 0) return "";

            string points = Points >= 1_000_000
                ? (Points / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M"
                : Points >= 1000
                    ? (Points / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K"
                    : Points.ToString(CultureInfo.InvariantCulture);

            if (Level <= 0) return points + " pts";
            return Points > 0 ? $"M{Level} · {points}" : $"M{Level}";
        }
    }
}

/// <summary>
/// Reads champion mastery from the League client.
///
/// Riot has moved these endpoints more than once — older clients keyed them by summonerId
/// under lol-collections, newer ones by puuid under lol-champion-mastery — and there is no
/// way to know which build a user is on. So the first lookup probes the known shapes, then
/// the one that answered is reused for everybody else.
/// </summary>
public class MasteryService
{
    private readonly LcuService _lcu;

    /// <summary>{0} = puuid, {1} = summonerId, {2} = championId.</summary>
    private static readonly string[] Candidates =
    {
        "lol-champion-mastery/v1/{0}/champion-mastery/{2}",
        "lol-collections/v1/inventories/{1}/champion-mastery/{2}",
        "lol-champion-mastery/v1/{0}/champion-mastery",
        "lol-collections/v1/inventories/{1}/champion-mastery",
        "lol-champion-mastery/v1/local-player/champion-mastery"
    };

    private string? _workingTemplate;
    private bool _givenUp;

    private readonly List<string> _probeLog = new();

    public MasteryService(LcuService lcu) => _lcu = lcu;

    public static string DiagnosticPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ronaldo", "mastery-probe.txt");

    /// <summary>Fills in mastery for every player, on the champion they are actually playing.</summary>
    public async Task FillAsync(IEnumerable<LivePlayer> players)
    {
        var list = players.Where(p => p.ChampionId > 0).ToList();
        if (list.Count == 0) return;

        foreach (var p in list)
        {
            if (_givenUp) return;

            var info = await LookupAsync(p);
            if (info != null) p.MasteryText = info.Display;
        }

        // Nothing answered anywhere: leave a note so the endpoint can be corrected.
        if (_workingTemplate == null && !_givenUp)
        {
            _givenUp = true;
            SaveProbeLog();
        }
    }

    private async Task<MasteryInfo?> LookupAsync(LivePlayer player)
    {
        // Once one shape works, stop probing the others.
        if (_workingTemplate != null)
            return await TryTemplateAsync(_workingTemplate, player, log: false);

        foreach (var template in Candidates)
        {
            var info = await TryTemplateAsync(template, player, log: true);
            if (info == null) continue;

            _workingTemplate = template;
            return info;
        }

        return null;
    }

    private async Task<MasteryInfo?> TryTemplateAsync(string template, LivePlayer player, bool log)
    {
        // Skip shapes we lack an identifier for.
        if (template.Contains("{0}") && player.Puuid.Length == 0) return null;
        if (template.Contains("{1}") && player.SummonerId <= 0) return null;

        string url = string.Format(template, player.Puuid, player.SummonerId, player.ChampionId);

        var (status, body) = await _lcu.GetWithStatusAsync(url);
        if (log) _probeLog.Add($"{status,-4} {url}");

        if (status != 200 || string.IsNullOrEmpty(body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // A list endpoint: find the champion being played.
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in root.EnumerateArray())
                {
                    if (ReadInt(e, "championId") != player.ChampionId) continue;
                    return Read(e);
                }
                return null;
            }

            if (root.ValueKind != JsonValueKind.Object) return null;

            // A single-champion endpoint. Guard against it answering for a different champion.
            int id = ReadInt(root, "championId");
            if (id != 0 && id != player.ChampionId) return null;

            return Read(root);
        }
        catch
        {
            return null;
        }
    }

    private static MasteryInfo? Read(JsonElement e)
    {
        var info = new MasteryInfo
        {
            Level = ReadInt(e, "championLevel", "masteryLevel", "level"),
            Points = ReadLong(e, "championPoints", "masteryPoints", "points")
        };

        return info.Level > 0 || info.Points > 0 ? info : null;
    }

    private static int ReadInt(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number &&
                v.TryGetInt32(out int i)) return i;
        return 0;
    }

    private static long ReadLong(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number &&
                v.TryGetInt64(out long i)) return i;
        return 0;
    }

    private void SaveProbeLog()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("No champion-mastery endpoint answered. Endpoints tried:");
            foreach (var line in _probeLog.Distinct()) sb.AppendLine("  " + line);

            string path = DiagnosticPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, sb.ToString());
        }
        catch { }
    }
}
