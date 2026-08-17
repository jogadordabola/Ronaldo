using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ronaldo.Stats;

/// <summary>
/// Assembles the champion build shown in the UI: several rune pages ranked by pick rate,
/// each paired with the item build from games that actually ran that keystone.
///
/// Sources:
///   op.gg       - rune pages with pick/win rates, item paths, skill order, summoner spells.
///   Lolalytics  - item builds filtered to a specific keystone, overlaid onto each page.
///   League client - offline fallback using Riot's own in-client recommendations.
/// </summary>
public class BuildProvider
{
    private readonly OpGgClient _opgg = new();
    private readonly LolalyticsClient _lola = new();
    private readonly PatchProvider _patches = new();

    private const int MaxPages = 3;

    /// <summary>Absolute floor on a matchup's sample, however rarely the champion is played.</summary>
    private const int MinMatchupGames = 50;

    /// <summary>
    /// A matchup also has to be worth at least this fraction of the most common one. Set from
    /// measurement: at a twentieth, a top-of-table champion was fine but Mordekaiser's best and
    /// worst matchups were both its smallest samples — a 72% read off 36 games.
    /// </summary>
    private const int MatchupShareOfBusiest = 10;

    /// <summary>Floor on the games behind a single slot option, for the same reason.</summary>
    private const int MinSlotItemGames = 50;

    /// <param name="assignedLane">
    /// The lane to build for, or null to let op.gg's play rates decide. A non-null lane is
    /// always honoured, so a manual override is never second-guessed by the auto-detection.
    /// </param>
    public async Task<ChampionBuildData> GetBuildAsync(
        LcuService lcu, int championId, string championName, string alias,
        Lane? assignedLane, LaneSource laneSource, StatsRank rank, StatsRegion region,
        CancellationToken ct = default)
    {
        var data = new ChampionBuildData
        {
            ChampionId = championId,
            ChampionName = championName,
            Rank = rank,
            Region = region,
            Lane = assignedLane ?? Lane.Mid,
            LaneSource = laneSource
        };

        // op.gg reports every position's play rate whichever one we ask for, so a single
        // request tells us where this champion is actually played.
        var opgg = await _opgg.GetChampionAsync(championId, data.Lane, rank, region, ct);

        if (opgg != null && assignedLane == null && opgg.BestLane.HasValue && opgg.BestLane != data.Lane)
        {
            data.Lane = opgg.BestLane.Value;
            data.LaneSource = LaneSource.Detected;
            opgg = await _opgg.GetChampionAsync(championId, data.Lane, rank, region, ct) ?? opgg;
        }

        if (opgg == null)
            return await FallbackAsync(lcu, data, ct);

        data.LanePlayRates = opgg.LaneRates;
        data.Matchups = RankMatchups(opgg.Counters);

        // One extra request, and only once the lane is settled. Shown for reference only: the
        // item set import stays on the core build.
        data.ItemSlots = TrimItemSlots(
            await _opgg.GetItemSlotsAsync(championId, data.Lane, rank, region, ct));

        var pages = BuildPages(lcu, opgg);
        if (pages.Count == 0)
            return await FallbackAsync(lcu, data, ct);

        string patch = await _patches.GetAsync(ct);
        data.Patch = patch;

        await AttachItemBuildsAsync(pages, opgg, alias, patch, data.Lane, rank, region, ct);

        data.Pages = pages;
        data.StatusLine = pages.Any(p => p.Items?.KeystoneSpecific == true)
            ? "op.gg + Lolalytics"
            : "op.gg";

        return data;
    }

    /// <summary>
    /// Drops slot options with too thin a sample, and any slot left with nothing.
    ///
    /// The late slots thin out fast, and a champion that rarely gets that far produces nonsense:
    /// a support's sixth item can come back as one game at a 100% win rate, which would read as
    /// the most convincing entry on the panel. Better to show the slot short, or not at all.
    /// </summary>
    private static List<ItemSlot> TrimItemSlots(List<ItemSlot> slots) =>
        slots
            .Select(s => new ItemSlot
            {
                Slot = s.Slot,
                Items = s.Items.Where(i => i.Play >= MinSlotItemGames).ToList()
            })
            .Where(s => s.Items.Count > 0)
            .ToList();

    /// <summary>
    /// Orders the lane matchups by win rate, best first, after dropping the ones too rare to
    /// mean anything.
    ///
    /// The tail of the list is where the extremes live: a matchup seen forty times can read
    /// 60% on nothing but noise and would top the table ahead of a real counter. The floor is
    /// relative to the most common matchup, so it adapts to champions and ranks where every
    /// matchup is rare, rather than emptying the table.
    /// </summary>
    private static List<ChampionMatchup> RankMatchups(List<ChampionMatchup> counters)
    {
        if (counters.Count == 0) return new List<ChampionMatchup>();

        int busiest = counters.Max(c => c.Play);
        int floor = Math.Max(MinMatchupGames, busiest / MatchupShareOfBusiest);

        return counters
            .Where(c => c.Play >= floor)
            .OrderByDescending(c => c.WinRatePercent)
            .ToList();
    }

    /// <summary>
    /// Picks the distinct pages worth showing. op.gg returns several near-identical variants
    /// of the same keystone, so pages are grouped by keystone and only the most-played
    /// variant of each is kept before falling back to variants for the remaining slots.
    /// </summary>
    private static List<RunePage> BuildPages(LcuService lcu, OpGgChampion opgg)
    {
        var candidates = new List<RunePage>();
        var seen = new HashSet<string>();

        var byKeystone = opgg.RunePages
            .Where(p => p.KeystoneId > 0)
            .GroupBy(p => p.KeystoneId)
            .Select(g => new
            {
                // op.gg reports pick rates against all of the champion's games in this lane,
                // so a keystone's share is the sum of its variants' rates.
                PickRate = g.Sum(x => x.PickRate) * 100.0,
                Play = g.Sum(x => x.Play),
                Variants = g.OrderByDescending(x => x.Play).ToList()
            })
            .OrderByDescending(g => g.Play)
            .ToList();

        // One page per keystone, so the list shows genuinely different setups...
        foreach (var group in byKeystone)
        {
            var page = Convert(lcu, group.Variants[0], group.PickRate, group.Play);
            if (page != null && seen.Add(page.Signature)) candidates.Add(page);
        }

        // ...then the remaining variants, in case there aren't enough distinct keystones.
        foreach (var group in byKeystone)
        {
            foreach (var variant in group.Variants.Skip(1))
            {
                var page = Convert(lcu, variant, variant.PickRate * 100.0, variant.Play);
                if (page != null && seen.Add(page.Signature)) candidates.Add(page);
            }
        }

        var result = candidates
            .OrderByDescending(p => p.PickRate)
            .Take(MaxPages)
            .ToList();

        // Label only once the final order is known, so "Most Popular" really is the top one.
        for (int i = 0; i < result.Count; i++)
        {
            result[i].Label = i switch
            {
                0 => "Most Popular",
                1 => "Alternative",
                _ => "Off-Meta Pick"
            };
        }

        return result;
    }

    private static RunePage? Convert(LcuService lcu, OpGgRunePage src, double pickRate, int games) =>
        RuneAssembler.Build(
            lcu,
            src.PrimaryRunes, src.SecondaryRunes, src.Shards,
            "", "op.gg",
            src.WinRatePercent, pickRate, games,
            src.PrimaryStyleId, src.SubStyleId);

    /// <summary>
    /// Gives each page its own item build. Lolalytics can filter items by keystone, which is
    /// what makes the build specific to the page; op.gg's champion-wide build is used for
    /// everything it can't cover, and as the fallback when Lolalytics is unavailable.
    /// </summary>
    private async Task AttachItemBuildsAsync(
        List<RunePage> pages, OpGgChampion opgg, string alias, string patch,
        Lane lane, StatsRank rank, StatsRegion region, CancellationToken ct)
    {
        // Champion-wide defaults from op.gg.
        var topCore = opgg.CoreItems.FirstOrDefault();
        var topStarter = opgg.StarterItems.FirstOrDefault();
        var topBoots = opgg.Boots.FirstOrDefault();
        var topSpells = opgg.SummonerSpells.FirstOrDefault();

        var situational = opgg.LastItems
            .SelectMany(o => o.Ids)
            .Where(id => !StatsCatalog.BootIds.Contains(id))
            .Distinct()
            .ToList();

        List<LolaItemBuild?> lolaBuilds;

        if (string.IsNullOrEmpty(patch))
        {
            lolaBuilds = pages.Select(_ => (LolaItemBuild?)null).ToList();
        }
        else
        {
            string slug = StatsCatalog.ChampionSlug(alias);
            var fetches = pages
                .Select(p => _lola.GetItemBuildAsync(slug, patch, lane, rank, region, p.KeystoneId, ct))
                .ToList();
            lolaBuilds = (await Task.WhenAll(fetches)).ToList();
        }

        for (int i = 0; i < pages.Count; i++)
        {
            var build = new ItemBuild
            {
                SpellIds = topSpells?.Ids ?? new List<int>(),
                StarterIds = topStarter?.Ids ?? new List<int>(),
                BootsId = topBoots?.Ids.FirstOrDefault() ?? 0
            };

            var lola = lolaBuilds[i];

            if (lola != null && lola.Core.Count > 0)
            {
                build.CoreIds = lola.Core.Where(id => !StatsCatalog.BootIds.Contains(id)).ToList();
                if (lola.BootsId > 0) build.BootsId = lola.BootsId;
                build.WinRate = lola.WinRate;
                build.Games = lola.Games;
                build.KeystoneSpecific = true;
                build.Source = "Lolalytics";
            }
            else
            {
                build.CoreIds = topCore?.Ids.Where(id => !StatsCatalog.BootIds.Contains(id)).ToList()
                                ?? new List<int>();
                build.WinRate = topCore?.WinRatePercent ?? 0;
                build.Games = topCore?.Play ?? 0;
                build.KeystoneSpecific = false;
                build.Source = "op.gg";
            }

            build.SituationalIds = situational
                .Where(id => !build.CoreIds.Contains(id))
                .Take(5).ToList();

            pages[i].Items = build;
        }
    }

    // ---- Offline fallback: Riot's own in-client recommendations ----

    /// <summary>
    /// Used when the stats sites are unreachable. Reads the rune page and item defaults the
    /// League client ships with, so the app still works offline (without pick/win rates).
    /// </summary>
    private static async Task<ChampionBuildData> FallbackAsync(
        LcuService lcu, ChampionBuildData data, CancellationToken ct)
    {
        data.IsFallback = true;
        data.StatusLine = "League client recommendations (stats sites unreachable)";

        var page = await ReadClientRunePageAsync(lcu, data.ChampionId, data.Lane);
        if (page != null)
        {
            page.Items = await ReadClientItemsAsync(lcu, data.ChampionId);
            data.Pages.Add(page);
        }

        return data;
    }

    private static async Task<RunePage?> ReadClientRunePageAsync(LcuService lcu, int championId, Lane lane)
    {
        var positions = new[]
        {
            StatsCatalog.LcuPosition(lane), "MIDDLE", "TOP", "BOTTOM", "JUNGLE", "UTILITY"
        };

        foreach (var pos in positions.Distinct())
        {
            string? json = await lcu.GetAsync(
                $"lol-perks/v1/recommended-pages/champion/{championId}/position/{pos}/map/11");
            if (string.IsNullOrEmpty(json)) continue;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                    continue;

                var p = doc.RootElement[0];

                int primaryStyle =
                    p.TryGetProperty("primaryPerkStyleId", out var a) ? a.GetInt32() :
                    p.TryGetProperty("primaryStyleId", out var b) ? b.GetInt32() : 0;
                int subStyle =
                    p.TryGetProperty("secondaryPerkStyleId", out var c) ? c.GetInt32() :
                    p.TryGetProperty("subStyleId", out var d) ? d.GetInt32() : 0;

                if (!p.TryGetProperty("selectedPerkIds", out var perksArr)) continue;
                var perkIds = perksArr.EnumerateArray().Select(x => x.GetInt32()).ToList();

                if (primaryStyle <= 0 || subStyle <= 0 || perkIds.Count < 9) continue;

                return new RunePage
                {
                    Label = "Riot Recommended",
                    Source = "League Client",
                    PrimaryStyleId = primaryStyle,
                    SubStyleId = subStyle,
                    PerkIds = perkIds.Take(9).ToList()
                };
            }
            catch { }
        }

        return null;
    }

    private static async Task<ItemBuild> ReadClientItemsAsync(LcuService lcu, int championId)
    {
        var build = new ItemBuild { Source = "League Client" };

        string? json = await lcu.GetAsync($"lol-game-data/assets/v1/champions/{championId}.json");
        if (string.IsNullOrEmpty(json)) return build;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("recommendedItemDefaults", out var items) ||
                items.ValueKind != JsonValueKind.Array) return build;

            foreach (var item in items.EnumerateArray())
            {
                int id = item.GetInt32();
                if (id <= 0) continue;

                if (StatsCatalog.BootIds.Contains(id))
                {
                    if (build.BootsId == 0) build.BootsId = id;
                }
                else if (build.CoreIds.Count < 3 && !build.CoreIds.Contains(id))
                {
                    build.CoreIds.Add(id);
                }
            }
        }
        catch { }

        return build;
    }
}
