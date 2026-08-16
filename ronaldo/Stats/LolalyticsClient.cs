using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ronaldo.Stats;

public class LolaItemBuild
{
    public List<int> Core { get; set; } = new();
    public int BootsId { get; set; }
    public double WinRate { get; set; }
    public int Games { get; set; }
}

/// <summary>
/// Reads Lolalytics' JSON API for item builds restricted to a single keystone — the part
/// op.gg doesn't expose. Query shape and the per-mode subdomains come from the site's own
/// client code: unfiltered builds are served by a1, keystone-filtered builds by a3.
/// </summary>
public class LolalyticsClient
{
    private const string BuildHost = "a1";
    private const string KeystoneHost = "a3";

    private static string Query(string ep, string host, string slug, string patch,
                                Lane lane, StatsRank rank, StatsRegion region, int? keystone)
    {
        string url = $"https://{host}.lolalytics.com/mega/?ep={ep}&v=1&patch={patch}" +
                     $"&c={slug}&lane={StatsCatalog.LolalyticsLane(lane)}" +
                     $"&tier={StatsCatalog.LolalyticsTier(rank)}&queue=420" +
                     $"&region={StatsCatalog.LolalyticsRegion(region)}";
        if (keystone.HasValue) url += $"&keystone={keystone.Value}";
        return url;
    }

    /// <summary>
    /// Fetches the most-played three-item core for games that ran the given keystone.
    /// Returns null when the keystone has too small a sample to report a build.
    /// </summary>
    public async Task<LolaItemBuild?> GetItemBuildAsync(
        string slug, string patch, Lane lane, StatsRank rank, StatsRegion region,
        int? keystone, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(patch) || string.IsNullOrEmpty(slug)) return null;

        string host = keystone.HasValue ? KeystoneHost : BuildHost;
        string? json = await StatsHttp.GetStringAsync(
            Query("build-itemset", host, slug, patch, lane, rank, region, keystone), ct);

        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("itemSets", out var sets) ||
                sets.ValueKind != JsonValueKind.Object) return null;

            var core = TopEntry(sets, "itemSet3") ?? TopEntry(sets, "itemSet2") ?? TopEntry(sets, "itemSet1");
            if (core == null) return null;

            var build = new LolaItemBuild
            {
                Core = core.Value.Ids,
                Games = core.Value.Games,
                WinRate = core.Value.Games > 0 ? core.Value.Wins * 100.0 / core.Value.Games : 0
            };

            var withBoots = TopEntry(sets, "itemBootSet3") ?? TopEntry(sets, "itemBootSet2");
            if (withBoots != null)
                build.BootsId = withBoots.Value.Ids.FirstOrDefault(StatsCatalog.BootIds.Contains);

            return build.Core.Count > 0 ? build : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Entries are ["id_id_id", games, wins]; the most-played one wins.</summary>
    private static (List<int> Ids, int Games, int Wins)? TopEntry(JsonElement sets, string key)
    {
        if (!sets.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;

        List<int>? bestIds = null;
        int bestGames = 0, bestWins = 0;

        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 3) continue;
            if (e[1].ValueKind != JsonValueKind.Number) continue;

            int games = e[1].GetInt32();
            if (games <= bestGames) continue;

            var ids = SplitIds(e[0].GetString());
            if (ids.Count == 0) continue;

            bestIds = ids;
            bestGames = games;
            bestWins = e[2].GetInt32();
        }

        return bestIds == null ? null : (bestIds, bestGames, bestWins);
    }

    private static List<int> SplitIds(string? s)
    {
        var list = new List<int>();
        if (string.IsNullOrEmpty(s)) return list;

        foreach (var part in s.Split('_', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part, out var v) && v > 0) list.Add(v);

        return list;
    }
}
