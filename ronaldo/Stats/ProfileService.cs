using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ronaldo.Stats;

public class SummonerProfile
{
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public int ProfileIconId { get; set; }
    public string Puuid { get; set; } = "";
}

public class RankEntry
{
    public string QueueName { get; set; } = "";
    public string Tier { get; set; } = "";
    public string Division { get; set; } = "";
    public int LeaguePoints { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }

    public bool IsRanked => Tier.Length > 0 && !Tier.Equals("NONE", StringComparison.OrdinalIgnoreCase);
    public int Games => Wins + Losses;
    public double WinRate => Games > 0 ? Wins * 100.0 / Games : 0;
}

/// <summary>One player's final line in a finished game.</summary>
public class ScoreboardPlayer
{
    public string Name { get; set; } = "";
    public int ChampionId { get; set; }
    public int TeamId { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int Cs { get; set; }
    public int Gold { get; set; }
    public int Damage { get; set; }
    public int Level { get; set; }
    public bool Won { get; set; }
    public bool IsMe { get; set; }
    public List<int> Items { get; set; } = new();
}

public class MatchSummary
{
    public long GameId { get; set; }
    public int ChampionId { get; set; }
    public string QueueName { get; set; } = "";
    public bool Won { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int Cs { get; set; }
    public TimeSpan Duration { get; set; }
    public DateTime PlayedAt { get; set; }
    public List<int> Items { get; set; } = new();

    /// <summary>All ten players, so the full scoreboard can be shown without another call.</summary>
    public List<ScoreboardPlayer> Scoreboard { get; set; } = new();

    public double Kda => Deaths == 0 ? Kills + Assists : (Kills + Assists) / (double)Deaths;
}

/// <summary>
/// Reads the signed-in player's profile, rank and match history from the League client.
///
/// This needs no Riot account linking or API key: the client is already authenticated, and
/// the app talks to it over the local lockfile session.
/// </summary>
public class ProfileService
{
    private readonly LcuService _lcu;

    public ProfileService(LcuService lcu) => _lcu = lcu;

    private static readonly Dictionary<int, string> QueueNames = new()
    {
        { 400, "Normal Draft" }, { 420, "Ranked Solo" }, { 430, "Normal Blind" },
        { 440, "Ranked Flex" }, { 450, "ARAM" }, { 490, "Quickplay" },
        { 700, "Clash" }, { 720, "ARAM Clash" }, { 830, "Co-op vs AI" },
        { 840, "Co-op vs AI" }, { 850, "Co-op vs AI" }, { 900, "URF" },
        { 1020, "One for All" }, { 1300, "Nexus Blitz" }, { 1700, "Arena" },
        { 1900, "URF" }, { 2000, "Tutorial" }
    };

    public async Task<SummonerProfile?> GetSummonerAsync()
    {
        string? json = await _lcu.GetAsync("lol-summoner/v1/current-summoner");
        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;

            string name = Str(r, "gameName", "displayName", "internalName");
            string tag = Str(r, "tagLine");

            return new SummonerProfile
            {
                Name = tag.Length > 0 ? $"{name}#{tag}" : name,
                Level = Int(r, "summonerLevel"),
                ProfileIconId = Int(r, "profileIconId"),
                Puuid = Str(r, "puuid")
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<RankEntry>> GetRanksAsync()
    {
        var result = new List<RankEntry>();

        string? json = await _lcu.GetAsync("lol-ranked/v1/current-ranked-stats");
        if (string.IsNullOrEmpty(json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("queueMap", out var queues) ||
                queues.ValueKind != JsonValueKind.Object) return result;

            foreach (var wanted in new[] { "RANKED_SOLO_5x5", "RANKED_FLEX_SR" })
            {
                if (!queues.TryGetProperty(wanted, out var q)) continue;

                var entry = new RankEntry
                {
                    QueueName = wanted == "RANKED_SOLO_5x5" ? "Ranked Solo/Duo" : "Ranked Flex",
                    Tier = Str(q, "tier"),
                    Division = Str(q, "division"),
                    LeaguePoints = Int(q, "leaguePoints"),
                    Wins = Int(q, "wins"),
                    Losses = Int(q, "losses")
                };

                result.Add(entry);
            }
        }
        catch { }

        return result;
    }

    /// <summary>
    /// Recent games. The history payload already carries every participant, so each match
    /// arrives with its full scoreboard and no follow-up request is needed.
    /// </summary>
    public async Task<List<MatchSummary>> GetMatchHistoryAsync(string puuid, int count = 15)
    {
        var matches = new List<MatchSummary>();

        string? json = await _lcu.GetAsync(
            $"lol-match-history/v1/products/lol/current-summoner/matches?begIndex=0&endIndex={count - 1}");

        if (string.IsNullOrEmpty(json)) return matches;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Shape is { games: { games: [...] } }, but some builds return the array directly.
            JsonElement games;
            if (root.TryGetProperty("games", out var outer) &&
                outer.TryGetProperty("games", out var inner)) games = inner;
            else if (root.ValueKind == JsonValueKind.Array) games = root;
            else if (outer.ValueKind == JsonValueKind.Array) games = outer;
            else return matches;

            foreach (var g in games.EnumerateArray())
            {
                var match = ParseGame(g, puuid);
                if (match != null) matches.Add(match);
            }
        }
        catch { }

        return matches.OrderByDescending(m => m.PlayedAt).ToList();
    }

    private static MatchSummary? ParseGame(JsonElement g, string puuid)
    {
        try
        {
            var match = new MatchSummary
            {
                GameId = Long(g, "gameId"),
                Duration = TimeSpan.FromSeconds(Int(g, "gameDuration")),
                QueueName = QueueLabel(Int(g, "queueId"), Str(g, "gameMode"))
            };

            long created = Long(g, "gameCreation");
            if (created > 0)
                match.PlayedAt = DateTimeOffset.FromUnixTimeMilliseconds(created).LocalDateTime;

            // participantIdentities maps participantId -> the actual player.
            var identities = new Dictionary<int, (string Name, string Puuid)>();
            if (g.TryGetProperty("participantIdentities", out var ids) &&
                ids.ValueKind == JsonValueKind.Array)
            {
                foreach (var i in ids.EnumerateArray())
                {
                    int pid = Int(i, "participantId");
                    if (!i.TryGetProperty("player", out var pl)) continue;

                    string name = Str(pl, "gameName", "summonerName");
                    string tag = Str(pl, "tagLine");
                    identities[pid] = (tag.Length > 0 ? $"{name}#{tag}" : name, Str(pl, "puuid"));
                }
            }

            if (!g.TryGetProperty("participants", out var parts) ||
                parts.ValueKind != JsonValueKind.Array) return null;

            foreach (var p in parts.EnumerateArray())
            {
                int pid = Int(p, "participantId");
                if (!p.TryGetProperty("stats", out var st)) continue;

                identities.TryGetValue(pid, out var who);

                var player = new ScoreboardPlayer
                {
                    Name = who.Name ?? "",
                    ChampionId = Int(p, "championId"),
                    TeamId = Int(p, "teamId"),
                    Kills = Int(st, "kills"),
                    Deaths = Int(st, "deaths"),
                    Assists = Int(st, "assists"),
                    Cs = Int(st, "totalMinionsKilled") + Int(st, "neutralMinionsKilled"),
                    Gold = Int(st, "goldEarned"),
                    Damage = Int(st, "totalDamageDealtToChampions"),
                    Level = Int(st, "champLevel"),
                    Won = Bool(st, "win"),
                    Items = Enumerable.Range(0, 7)
                        .Select(n => Int(st, "item" + n))
                        .Where(id => id > 0).ToList()
                };

                player.IsMe = puuid.Length > 0 && who.Puuid == puuid;
                match.Scoreboard.Add(player);

                if (player.IsMe)
                {
                    match.ChampionId = player.ChampionId;
                    match.Kills = player.Kills;
                    match.Deaths = player.Deaths;
                    match.Assists = player.Assists;
                    match.Cs = player.Cs;
                    match.Won = player.Won;
                    match.Items = player.Items;
                }
            }

            // Older payloads omit puuid; fall back to the single participant the client marks.
            if (match.ChampionId == 0 && match.Scoreboard.Count > 0)
            {
                var mine = match.Scoreboard[0];
                match.ChampionId = mine.ChampionId;
                match.Kills = mine.Kills;
                match.Deaths = mine.Deaths;
                match.Assists = mine.Assists;
                match.Cs = mine.Cs;
                match.Won = mine.Won;
                match.Items = mine.Items;
                mine.IsMe = true;
            }

            return match;
        }
        catch
        {
            return null;
        }
    }

    private static string QueueLabel(int queueId, string gameMode)
    {
        if (QueueNames.TryGetValue(queueId, out var n)) return n;
        if (gameMode.Length > 0) return Capitalize(gameMode);
        return queueId > 0 ? $"Queue {queueId}" : "Custom";
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    private static string Str(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
            {
                string s = v.GetString() ?? "";
                if (s.Length > 0) return s;
            }
        return "";
    }

    private static int Int(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number &&
                v.TryGetInt32(out int i)) return i;
        return 0;
    }

    private static long Long(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number &&
                v.TryGetInt64(out long i)) return i;
        return 0;
    }

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) &&
        (v.ValueKind == JsonValueKind.True ||
         (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out bool b) && b));
}
