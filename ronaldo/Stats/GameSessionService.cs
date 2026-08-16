using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ronaldo.Stats;

/// <summary>One player in the current game.</summary>
public class LivePlayer
{
    public string Name { get; set; } = "";
    public int ChampionId { get; set; }
    public int Spell1Id { get; set; }
    public int Spell2Id { get; set; }
    public string Position { get; set; } = "";
    public string Puuid { get; set; } = "";
    public long SummonerId { get; set; }
    public bool IsLocalPlayer { get; set; }

    /// <summary>Ranked tier, when the client will tell us. Blank when unavailable.</summary>
    public string RankText { get; set; } = "";
    public string WinRateText { get; set; } = "";

    /// <summary>Mastery on the champion being played, e.g. "M7 · 245K". Blank if unavailable.</summary>
    public string MasteryText { get; set; } = "";
}

public class LiveGame
{
    public List<LivePlayer> TeamOne { get; set; } = new();
    public List<LivePlayer> TeamTwo { get; set; } = new();
    public string QueueName { get; set; } = "";
    public long GameId { get; set; }

    public bool HasPlayers => TeamOne.Count > 0 || TeamTwo.Count > 0;
}

/// <summary>
/// Reads the in-progress game from the League client.
///
/// The team lists live in lol-gameflow/v1/session under gameData. Field names have shifted
/// across patches (summonerName is often empty now that Riot IDs exist, and champion picks
/// sometimes only appear in playerChampionSelections), so every field is read defensively
/// with fallbacks rather than assuming one shape.
/// </summary>
public class GameSessionService
{
    private readonly LcuService _lcu;
    private readonly MasteryService _mastery;

    public GameSessionService(LcuService lcu)
    {
        _lcu = lcu;
        _mastery = new MasteryService(lcu);
    }

    /// <summary>Where the raw session is dumped when parsing finds no players.</summary>
    public static string DiagnosticPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ronaldo", "last-game-session.json");

    public async Task<LiveGame?> GetLiveGameAsync()
    {
        string? json = await _lcu.GetAsync("lol-gameflow/v1/session");
        if (string.IsNullOrEmpty(json)) return null;

        LiveGame game;
        try
        {
            game = Parse(json);
        }
        catch
        {
            SaveDiagnostic(json);
            return null;
        }

        // If the shape changed, keep the payload so the mapping can be corrected.
        if (!game.HasPlayers) SaveDiagnostic(json);

        await FillNamesAsync(game);
        await FillRanksAsync(game);
        await _mastery.FillAsync(game.TeamOne.Concat(game.TeamTwo));

        return game;
    }

    private static LiveGame Parse(string json)
    {
        var game = new LiveGame();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("gameData", out var data)) return game;

        if (data.TryGetProperty("gameId", out var gid) && gid.ValueKind == JsonValueKind.Number)
            game.GameId = gid.GetInt64();

        if (data.TryGetProperty("queue", out var queue))
        {
            game.QueueName =
                queue.TryGetProperty("name", out var qn) ? qn.GetString() ?? "" :
                queue.TryGetProperty("shortName", out var sn) ? sn.GetString() ?? "" : "";
        }

        // Champion picks sometimes only appear here, keyed by the internal summoner name.
        var picks = new Dictionary<string, (int Champ, int S1, int S2)>(StringComparer.OrdinalIgnoreCase);
        if (data.TryGetProperty("playerChampionSelections", out var sel) &&
            sel.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in sel.EnumerateArray())
            {
                string key = Str(p, "summonerInternalName", "summonerName", "puuid");
                if (key.Length == 0) continue;
                picks[key] = (Int(p, "championId"), Int(p, "spell1Id"), Int(p, "spell2Id"));
            }
        }

        game.TeamOne = ReadTeam(data, "teamOne", picks);
        game.TeamTwo = ReadTeam(data, "teamTwo", picks);

        return game;
    }

    private static List<LivePlayer> ReadTeam(
        JsonElement data, string property, Dictionary<string, (int Champ, int S1, int S2)> picks)
    {
        var team = new List<LivePlayer>();
        if (!data.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return team;

        foreach (var e in arr.EnumerateArray())
        {
            var player = new LivePlayer
            {
                Name = Str(e, "summonerName", "gameName", "displayName", "summonerInternalName"),
                Puuid = Str(e, "puuid"),
                SummonerId = Long(e, "summonerId"),
                ChampionId = Int(e, "championId", "championPickIntent"),
                Spell1Id = Int(e, "spell1Id"),
                Spell2Id = Int(e, "spell2Id"),
                Position = Str(e, "selectedPosition", "assignedPosition", "selectedRole")
            };

            // Riot IDs split the name across two fields.
            string tag = Str(e, "tagLine");
            if (tag.Length > 0 && !player.Name.Contains('#')) player.Name += "#" + tag;

            string key = Str(e, "summonerInternalName", "summonerName", "puuid");
            if (picks.TryGetValue(key, out var pick))
            {
                if (player.ChampionId <= 0) player.ChampionId = pick.Champ;
                if (player.Spell1Id <= 0) player.Spell1Id = pick.S1;
                if (player.Spell2Id <= 0) player.Spell2Id = pick.S2;
            }

            team.Add(player);
        }

        return team;
    }

    /// <summary>Fills in any blank names by looking the player up by puuid.</summary>
    private async Task FillNamesAsync(LiveGame game)
    {
        foreach (var p in game.TeamOne.Concat(game.TeamTwo))
        {
            if (p.Name.Length > 0 || p.Puuid.Length == 0) continue;

            string? json = await _lcu.GetAsync($"lol-summoner/v2/summoners/puuid/{p.Puuid}");
            if (string.IsNullOrEmpty(json)) continue;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string name = Str(root, "gameName", "displayName", "internalName");
                string tag = Str(root, "tagLine");
                p.Name = tag.Length > 0 ? $"{name}#{tag}" : name;
            }
            catch { }
        }
    }

    /// <summary>
    /// Attempts ranked stats per player. The client only reliably exposes this for the local
    /// player, so anything it withholds is simply left blank rather than faked.
    /// </summary>
    private async Task FillRanksAsync(LiveGame game)
    {
        foreach (var p in game.TeamOne.Concat(game.TeamTwo))
        {
            if (p.Puuid.Length == 0) continue;

            string? json = await _lcu.GetAsync($"lol-ranked/v1/ranked-stats/{p.Puuid}");
            if (string.IsNullOrEmpty(json)) continue;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("queueMap", out var queues)) continue;
                if (!queues.TryGetProperty("RANKED_SOLO_5x5", out var solo)) continue;

                string tier = Str(solo, "tier");
                string div = Str(solo, "division");
                int lp = Int(solo, "leaguePoints");

                if (tier.Length > 0 && !tier.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                {
                    string pretty = Capitalize(tier);
                    if (div.Length > 0 && div != "NA") pretty += " " + div;
                    p.RankText = lp > 0 ? $"{pretty} · {lp} LP" : pretty;
                }

                int wins = Int(solo, "wins");
                int losses = Int(solo, "losses");
                if (wins + losses > 0)
                    p.WinRateText = $"{wins * 100.0 / (wins + losses):0}% WR ({wins}W {losses}L)";
            }
            catch { }
        }
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    private static string Str(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
            {
                string s = v.GetString() ?? "";
                if (s.Length > 0) return s;
            }
        }
        return "";
    }

    private static long Long(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            if (!e.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long i) && i > 0) return i;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out long p) && p > 0) return p;
        }
        return 0;
    }

    private static int Int(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            if (!e.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) && i > 0) return i;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int p) && p > 0) return p;
        }
        return 0;
    }

    private static void SaveDiagnostic(string json)
    {
        try
        {
            string path = DiagnosticPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }
        catch { }
    }
}
