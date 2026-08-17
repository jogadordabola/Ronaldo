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

    /// <summary>
    /// True when <see cref="WinRateText"/> is a real win rate rather than a bare win count.
    /// For other players that means it was counted from their recent games.
    /// </summary>
    public bool HasRealWinRate { get; set; }

    /// <summary>Mastery on the champion being played, e.g. "M7 · 245K". Blank if unavailable.</summary>
    public string MasteryText { get; set; } = "";

    /// <summary>
    /// True when the client left this player out of its team list and the card was rebuilt from
    /// the champion selections. Their champion is known; their name is not.
    /// </summary>
    public bool IsHidden { get; set; }
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
    private readonly ProfileService _profiles;

    public GameSessionService(LcuService lcu)
    {
        _lcu = lcu;
        _mastery = new MasteryService(lcu);
        _profiles = new ProfileService(lcu);
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

        // Needed before ranks are read: the client serves fuller stats for the signed-in player.
        string localPuuid = await _lcu.GetLocalPuuidAsync();
        foreach (var p in game.TeamOne.Concat(game.TeamTwo))
            p.IsLocalPlayer = localPuuid.Length > 0 && p.Puuid == localPuuid;

        await FillNamesAsync(game);
        await FillRanksAsync(game);
        await FillRecentFormAsync(game);
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

        // Champion picks sometimes only appear here, keyed by the internal summoner name. The
        // order is kept as well: it is what lets a hidden player be put back on the right team.
        var picks = new Dictionary<string, (int Champ, int S1, int S2)>(StringComparer.OrdinalIgnoreCase);
        var selections = new List<Selection>();

        if (data.TryGetProperty("playerChampionSelections", out var sel) &&
            sel.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in sel.EnumerateArray())
            {
                string key = Str(p, "summonerInternalName", "summonerName", "puuid");
                var pick = (Champ: Int(p, "championId"), S1: Int(p, "spell1Id"), S2: Int(p, "spell2Id"));

                if (key.Length > 0) picks[key] = pick;

                selections.Add(new Selection(Str(p, "puuid"), pick.Champ, pick.S1, pick.S2));
            }
        }

        game.TeamOne = ReadTeam(data, "teamOne", picks);
        game.TeamTwo = ReadTeam(data, "teamTwo", picks);

        RestoreHiddenPlayers(game, selections);

        return game;
    }

    /// <summary>One entry of playerChampionSelections, in the order the client listed it.</summary>
    private readonly record struct Selection(string Puuid, int Champ, int S1, int S2);

    /// <summary>
    /// Puts back players the client left out of its team lists.
    ///
    /// Streamer mode and privacy settings drop a player from gameData.teamOne/teamTwo entirely,
    /// so the team renders with four cards instead of five. playerChampionSelections still
    /// carries all ten, in team order — first half one side, second half the other — so the card
    /// can be rebuilt with its champion, spells and puuid. There is no name to show, but a
    /// champion with no stats beats a missing player.
    /// </summary>
    private static void RestoreHiddenPlayers(LiveGame game, List<Selection> selections)
    {
        if (selections.Count == 0 || selections.Count % 2 != 0) return;

        var known = game.TeamOne.Concat(game.TeamTwo)
            .Select(p => p.Puuid)
            .Where(p => p.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int perTeam = selections.Count / 2;

        for (int i = 0; i < selections.Count; i++)
        {
            var s = selections[i];
            if (s.Puuid.Length == 0 || known.Contains(s.Puuid)) continue;

            // The half a selection falls in says which side it belongs to. If that side is
            // somehow already full, use the other rather than pushing a team to six.
            var expected = i < perTeam ? game.TeamOne : game.TeamTwo;
            var other = i < perTeam ? game.TeamTwo : game.TeamOne;
            var team = expected.Count < perTeam ? expected
                     : other.Count < perTeam ? other
                     : null;

            if (team == null) continue;

            team.Add(new LivePlayer
            {
                Puuid = s.Puuid,
                ChampionId = s.Champ,
                Spell1Id = s.S1,
                Spell2Id = s.S2,
                IsHidden = true
            });

            known.Add(s.Puuid);
        }

        foreach (var team in new[] { game.TeamOne, game.TeamTwo })
        {
            InferMissingPosition(team, perTeam);
            SortByLane(team);
        }
    }

    /// <summary>
    /// Gives a restored player the one lane its team has not accounted for. Without this the
    /// rebuilt card has no position and would sort to the end instead of into its slot.
    /// </summary>
    private static void InferMissingPosition(List<LivePlayer> team, int perTeam)
    {
        // Only safe on a full five-lane team where exactly one position is unaccounted for.
        if (team.Count != perTeam || perTeam != 5) return;

        var blank = team.Where(p => p.Position.Length == 0).ToList();
        if (blank.Count != 1) return;

        var taken = team
            .Select(p => StatsCatalog.LaneFromLcuPosition(p.Position))
            .Where(l => l.HasValue)
            .Select(l => l!.Value)
            .ToHashSet();

        if (taken.Count != perTeam - 1) return;

        var missing = Enum.GetValues<Lane>().Where(l => !taken.Contains(l)).ToList();
        if (missing.Count != 1) return;

        blank[0].Position = StatsCatalog.LcuPosition(missing[0]);
    }

    /// <summary>
    /// The client lists a team in no particular order, while the loading screen shows it by lane.
    /// Lane is declared top/jungle/mid/bottom/support, so its values sort directly. Anything with
    /// no position — ARAM, customs — sorts last and keeps the order it arrived in, OrderBy being
    /// stable.
    /// </summary>
    private static void SortByLane(List<LivePlayer> team)
    {
        var ordered = team
            .OrderBy(p => (int?)StatsCatalog.LaneFromLcuPosition(p.Position) ?? 5)
            .ToList();

        team.Clear();
        team.AddRange(ordered);
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

        SortByLane(team);
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

                // Riot only serves the loss count for the signed-in player. Everyone else comes
                // back as losses = 0, so treating it as real would show every opponent on a
                // flawless 100% record. Their rate is counted from their games instead, below.
                if (p.IsLocalPlayer)
                {
                    if (wins + losses > 0)
                    {
                        p.WinRateText = $"{wins * 100.0 / (wins + losses):0}% WR ({wins}W {losses}L)";
                        p.HasRealWinRate = true;
                    }
                }
                else if (wins > 0)
                {
                    // Stands in until FillRecentFormAsync can work out a real rate from their games.
                    p.WinRateText = wins == 1 ? "1 win" : $"{wins} wins";
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Works out a real win rate for the other players by counting their recent games.
    ///
    /// Riot withholds their split loss count, but their match history is readable, so the wins
    /// and losses in it can be counted directly. The client serves at most twenty games and
    /// ignores paging, so that window is as wide as this can get — which is why the count is
    /// shown alongside the rate rather than presented as a career figure.
    /// </summary>
    private async Task FillRecentFormAsync(LiveGame game)
    {
        var others = game.TeamOne.Concat(game.TeamTwo)
            .Where(p => !p.IsLocalPlayer && p.Puuid.Length > 0)
            .ToList();

        // Run together rather than in turn. The client answers in about 25ms each, so this is
        // not a bottleneck either way, but the scoreboard waits on all of them before it renders.
        await Task.WhenAll(others.Select(FillOneRecentFormAsync));
    }

    private async Task FillOneRecentFormAsync(LivePlayer p)
    {
        var matches = await _profiles.GetMatchHistoryForPuuidAsync(p.Puuid, accountId: 0);

        // Riot restricts this for some accounts. Leave the bare win count in place when it fails.
        var games = matches.Where(m => m.CountsTowardsForm).ToList();
        if (games.Count == 0) return;

        int wins = games.Count(m => m.Won);
        int losses = games.Count - wins;

        p.WinRateText = $"{wins * 100.0 / games.Count:0}% WR · {wins}W {losses}L";
        p.HasRealWinRate = true;
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
