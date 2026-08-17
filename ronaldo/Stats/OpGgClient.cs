using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ronaldo.Stats;

/// <summary>A complete rune page as op.gg reports it, with its own sample size.</summary>
public class OpGgRunePage
{
    public int PrimaryStyleId { get; set; }
    public int SubStyleId { get; set; }
    public List<int> PrimaryRunes { get; set; } = new();   // keystone first
    public List<int> SecondaryRunes { get; set; } = new();
    public List<int> Shards { get; set; } = new();

    public int Play { get; set; }
    public int Win { get; set; }
    public double PickRate { get; set; }   // 0-1 as returned by op.gg

    public int KeystoneId => PrimaryRunes.Count > 0 ? PrimaryRunes[0] : 0;
    public double WinRatePercent => Play > 0 ? Win * 100.0 / Play : 0;
}

/// <summary>An option op.gg ranks by pick rate (items, boots, spells, skill orders).</summary>
public class OpGgOption
{
    public List<int> Ids { get; set; } = new();
    public List<string> Labels { get; set; } = new();
    public int Play { get; set; }
    public int Win { get; set; }
    public double PickRate { get; set; }

    public double WinRatePercent => Play > 0 ? Win * 100.0 / Play : 0;
}

public class OpGgChampion
{
    public List<OpGgRunePage> RunePages { get; set; } = new();
    public List<OpGgOption> CoreItems { get; set; } = new();
    public List<OpGgOption> StarterItems { get; set; } = new();
    public List<OpGgOption> Boots { get; set; } = new();
    public List<OpGgOption> LastItems { get; set; } = new();
    public List<OpGgOption> SummonerSpells { get; set; } = new();

    /// <summary>Share of this champion's games played in each lane, from summary.positions.</summary>
    public Dictionary<Lane, double> LaneRates { get; set; } = new();

    /// <summary>Same-lane matchups, as op.gg returns them (ordered by how often they occur).</summary>
    public List<ChampionMatchup> Counters { get; set; } = new();

    public Lane? BestLane { get; set; }
    public int TotalPlay { get; set; }
}

/// <summary>
/// Reads op.gg's champion statistics API. One request returns rune pages, item paths,
/// skill orders and summoner spells for a champion at a given lane, rank and region.
/// </summary>
public class OpGgClient
{
    private const string BaseUrl = "https://lol-api-champion.op.gg/api";

    public async Task<OpGgChampion?> GetChampionAsync(
        int championId, Lane lane, StatsRank rank, StatsRegion region, CancellationToken ct = default)
    {
        string url = $"{BaseUrl}/{StatsCatalog.OpGgRegion(region)}/champions/ranked/" +
                     $"{championId}/{StatsCatalog.OpGgPosition(lane)}" +
                     $"?tier={StatsCatalog.OpGgTier(rank)}";

        string? json = await StatsHttp.GetStringAsync(url, ct);
        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

            var champ = new OpGgChampion
            {
                RunePages = ReadRunePages(data),
                CoreItems = ReadOptions(data, "core_items"),
                StarterItems = ReadOptions(data, "starter_items"),
                Boots = ReadOptions(data, "boots"),
                LastItems = ReadOptions(data, "last_items"),
                SummonerSpells = ReadOptions(data, "summoner_spells"),
                Counters = ReadCounters(data)
            };

            ReadPositions(data, champ);

            return champ.RunePages.Count > 0 || champ.CoreItems.Count > 0 ? champ : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<OpGgRunePage> ReadRunePages(JsonElement data)
    {
        var pages = new List<OpGgRunePage>();
        if (!data.TryGetProperty("runes", out var runes) || runes.ValueKind != JsonValueKind.Array)
            return pages;

        foreach (var r in runes.EnumerateArray())
        {
            var page = new OpGgRunePage
            {
                PrimaryStyleId = GetInt(r, "primary_page_id"),
                SubStyleId = GetInt(r, "secondary_page_id"),
                PrimaryRunes = ReadIntArray(r, "primary_rune_ids"),
                SecondaryRunes = ReadIntArray(r, "secondary_rune_ids"),
                Shards = ReadIntArray(r, "stat_mod_ids"),
                Play = GetInt(r, "play"),
                Win = GetInt(r, "win"),
                PickRate = GetDouble(r, "pick_rate")
            };

            if (page.PrimaryStyleId > 0 && page.PrimaryRunes.Count >= 4)
                pages.Add(page);
        }

        return pages;
    }

    /// <summary>
    /// Reads the lane matchups. op.gg returns one entry per opponent for the position asked
    /// about, with this champion's games and wins against them.
    /// </summary>
    private static List<ChampionMatchup> ReadCounters(JsonElement data)
    {
        var counters = new List<ChampionMatchup>();
        if (!data.TryGetProperty("counters", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return counters;

        foreach (var c in arr.EnumerateArray())
        {
            var matchup = new ChampionMatchup
            {
                OpponentId = GetInt(c, "champion_id"),
                Play = GetInt(c, "play"),
                Win = GetInt(c, "win")
            };

            if (matchup.OpponentId > 0 && matchup.Play > 0) counters.Add(matchup);
        }

        return counters;
    }

    private static List<OpGgOption> ReadOptions(JsonElement data, string property)
    {
        var options = new List<OpGgOption>();
        if (!data.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return options;

        foreach (var e in arr.EnumerateArray())
        {
            var option = new OpGgOption
            {
                Play = GetInt(e, "play"),
                Win = GetInt(e, "win"),
                PickRate = GetDouble(e, "pick_rate")
            };

            // "ids" is numeric for items/spells but holds skill letters for skill_masteries.
            if (e.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in ids.EnumerateArray())
                {
                    if (x.ValueKind == JsonValueKind.Number) option.Ids.Add(x.GetInt32());
                    else if (x.ValueKind == JsonValueKind.String)
                    {
                        string s = x.GetString() ?? "";
                        if (int.TryParse(s, out var v)) option.Ids.Add(v);
                        else if (s.Length > 0) option.Labels.Add(s);
                    }
                }
            }

            if (option.Ids.Count > 0 || option.Labels.Count > 0) options.Add(option);
        }

        return options.OrderByDescending(o => o.Play).ToList();
    }

    private static void ReadPositions(JsonElement data, OpGgChampion champ)
    {
        if (!data.TryGetProperty("summary", out var summary)) return;

        if (summary.TryGetProperty("average_stats", out var avg))
            champ.TotalPlay = GetInt(avg, "play");

        if (!summary.TryGetProperty("positions", out var positions) ||
            positions.ValueKind != JsonValueKind.Array) return;

        double bestRate = 0;

        foreach (var p in positions.EnumerateArray())
        {
            var lane = StatsCatalog.LaneFromOpGg(p.TryGetProperty("name", out var n) ? n.GetString() : null);
            if (lane == null || !p.TryGetProperty("stats", out var stats)) continue;

            // role_rate is the share of this champion's games played in that position.
            double rate = GetDouble(stats, "role_rate");
            champ.LaneRates[lane.Value] = rate * 100.0;

            if (rate > bestRate)
            {
                bestRate = rate;
                champ.BestLane = lane;
            }
        }
    }

    private static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static double GetDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static List<int> ReadIntArray(JsonElement parent, string name)
    {
        var list = new List<int>();
        if (!parent.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;

        foreach (var x in arr.EnumerateArray())
        {
            if (x.ValueKind == JsonValueKind.Number) list.Add(x.GetInt32());
            else if (x.ValueKind == JsonValueKind.String && int.TryParse(x.GetString(), out var v)) list.Add(v);
        }
        return list;
    }
}
