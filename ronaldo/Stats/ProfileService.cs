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
    public long AccountId { get; set; }

    /// <summary>False when this is another player, whose data the client may withhold.</summary>
    public bool IsSelf { get; set; }
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

    /// <summary>
    /// Rank crest. These live outside the game-data plugin, so it's a full URL.
    /// The shared-components set is used rather than ranked-emblem: the latter draws the crest
    /// small on a 1280x720 canvas, which shrinks to nothing at UI size.
    /// </summary>
    public string EmblemUrl =>
        "https://raw.communitydragon.org/latest/plugins/rcp-fe-lol-shared-components/global/default/" +
        (IsRanked ? Tier.ToLowerInvariant() : "unranked") + ".png";
}

/// <summary>How a champion has performed across the games we could read.</summary>
public class ChampionStat
{
    public int ChampionId { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }

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

    /// <summary>LP change, when this app saw the game finish. Null otherwise.</summary>
    public int? LpDelta { get; set; }

    public bool IsRanked => QueueName.StartsWith("Ranked", StringComparison.Ordinal);

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

    public Task<SummonerProfile?> GetSummonerAsync() =>
        ReadSummonerAsync("lol-summoner/v1/current-summoner", isSelf: true);

    /// <summary>Looks up another player. Returns null if the client won't resolve them.</summary>
    public Task<SummonerProfile?> GetSummonerByPuuidAsync(string puuid) =>
        ReadSummonerAsync($"lol-summoner/v2/summoners/puuid/{puuid}", isSelf: false);

    private async Task<SummonerProfile?> ReadSummonerAsync(string endpoint, bool isSelf)
    {
        string? json = await _lcu.GetAsync(endpoint);
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
                Puuid = Str(r, "puuid"),
                AccountId = Long(r, "accountId"),
                IsSelf = isSelf
            };
        }
        catch
        {
            return null;
        }
    }

    public Task<List<RankEntry>> GetRanksAsync() =>
        ReadRanksAsync("lol-ranked/v1/current-ranked-stats");

    public Task<List<RankEntry>> GetRanksForPuuidAsync(string puuid) =>
        ReadRanksAsync($"lol-ranked/v1/ranked-stats/{puuid}");

    private async Task<List<RankEntry>> ReadRanksAsync(string endpoint)
    {
        var result = new List<RankEntry>();

        string? json = await _lcu.GetAsync(endpoint);
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
    /// <summary>20 games: one request either way, and a big enough sample for the champion box.</summary>
    public Task<List<MatchSummary>> GetMatchHistoryAsync(string puuid, int count = 20) =>
        ReadHistoryAsync(
            $"lol-match-history/v1/products/lol/current-summoner/matches?begIndex=0&endIndex={count - 1}",
            puuid);

    /// <summary>
    /// Another player's recent games. Riot has tightened access to this over the years and
    /// the surviving route differs by client build, so the known shapes are tried in turn.
    /// Returns an empty list — not an error — when the client declines to answer.
    /// </summary>
    public async Task<List<MatchSummary>> GetMatchHistoryForPuuidAsync(
        string puuid, long accountId, int count = 20)
    {
        var probe = new List<string>();

        var candidates = new List<string>
        {
            $"lol-match-history/v1/products/lol/{puuid}/matches?begIndex=0&endIndex={count - 1}"
        };

        if (accountId > 0)
            candidates.Add(
                $"lol-match-history/v1/products/lol/accounts/{accountId}/matches?begIndex=0&endIndex={count - 1}");

        foreach (var url in candidates)
        {
            var (status, body) = await _lcu.GetWithStatusAsync(url);
            probe.Add($"{status,-4} {url}");

            if (status != 200 || string.IsNullOrEmpty(body)) continue;

            var matches = ParseHistory(body, puuid);
            if (matches.Count > 0) return matches;
        }

        SaveProbe(probe);
        return new List<MatchSummary>();
    }

    /// <summary>
    /// Most-played champions across the fetched games. The client only serves recent matches,
    /// so this is a form guide over that window rather than a career total.
    /// </summary>
    public static List<ChampionStat> SummariseChampions(IEnumerable<MatchSummary> matches, int take = 5)
    {
        return matches
            .Where(m => m.ChampionId > 0)
            .GroupBy(m => m.ChampionId)
            .Select(g => new ChampionStat
            {
                ChampionId = g.Key,
                Wins = g.Count(m => m.Won),
                Losses = g.Count(m => !m.Won)
            })
            .OrderByDescending(c => c.Games)
            .ThenByDescending(c => c.WinRate)
            .Take(take)
            .ToList();
    }

    /// <summary>Where the tried endpoints are recorded when another player's history fails.</summary>
    public static string DiagnosticPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ronaldo", "match-history-probe.txt");

    private static void SaveProbe(List<string> lines)
    {
        try
        {
            string path = DiagnosticPath;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path,
                "No match-history endpoint answered for another player. Tried:\n  " +
                string.Join("\n  ", lines));
        }
        catch { }
    }

    private async Task<List<MatchSummary>> ReadHistoryAsync(string endpoint, string puuid)
    {
        string? json = await _lcu.GetAsync(endpoint);
        return string.IsNullOrEmpty(json) ? new List<MatchSummary>() : ParseHistory(json, puuid);
    }

    private static List<MatchSummary> ParseHistory(string json, string puuid)
    {
        var matches = new List<MatchSummary>();

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

    /// <summary>
    /// Fetches the full participant list for one game.
    ///
    /// The match-history list endpoint only returns the signed-in player in its participants
    /// array, so a scoreboard has to come from the per-game endpoint instead.
    /// </summary>
    public async Task<List<ScoreboardPlayer>> GetMatchDetailAsync(long gameId, string puuid)
    {
        string? json = await _lcu.GetAsync($"lol-match-history/v1/games/{gameId}");
        if (string.IsNullOrEmpty(json)) return new List<ScoreboardPlayer>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var game = doc.RootElement;

            // Some builds wrap it as { game: {...} }.
            if (game.TryGetProperty("game", out var inner) && inner.ValueKind == JsonValueKind.Object)
                game = inner;

            return ParseGame(game, puuid)?.Scoreboard ?? new List<ScoreboardPlayer>();
        }
        catch
        {
            return new List<ScoreboardPlayer>();
        }
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
