using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace viktor.Stats;

public enum Lane { Top, Jungle, Mid, Bottom, Support }

/// <summary>Rank buckets offered in the UI. Both stats sources accept the same slugs.</summary>
public enum StatsRank { PlatinumPlus, EmeraldPlus, DiamondPlus, MasterPlus, Challenger, Overall }

public enum StatsRegion { World, NA, EUW, EUN, KR, BR, JP, LAN, LAS, OCE, RU, TR }

public static class StatsCatalog
{
    // --- op.gg (primary source) ---

    public static string OpGgTier(StatsRank r) => r switch
    {
        StatsRank.PlatinumPlus => "platinum_plus",
        StatsRank.EmeraldPlus => "emerald_plus",
        StatsRank.DiamondPlus => "diamond_plus",
        StatsRank.MasterPlus => "master_plus",
        StatsRank.Challenger => "challenger",
        StatsRank.Overall => "all",
        _ => "diamond_plus"
    };

    public static string OpGgRegion(StatsRegion r) => r switch
    {
        StatsRegion.World => "global",
        StatsRegion.NA => "na",
        StatsRegion.EUW => "euw",
        StatsRegion.EUN => "eune",
        StatsRegion.KR => "kr",
        StatsRegion.BR => "br",
        StatsRegion.JP => "jp",
        StatsRegion.LAN => "lan",
        StatsRegion.LAS => "las",
        StatsRegion.OCE => "oce",
        StatsRegion.RU => "ru",
        StatsRegion.TR => "tr",
        _ => "global"
    };

    public static string OpGgPosition(Lane l) => l switch
    {
        Lane.Top => "top",
        Lane.Jungle => "jungle",
        Lane.Mid => "mid",
        Lane.Bottom => "adc",
        Lane.Support => "support",
        _ => "mid"
    };

    /// <summary>Parses the position names op.gg reports in summary.positions.</summary>
    public static Lane? LaneFromOpGg(string? s) => (s ?? "").ToUpperInvariant() switch
    {
        "TOP" => Lane.Top,
        "JUNGLE" => Lane.Jungle,
        "MID" or "MIDDLE" => Lane.Mid,
        "ADC" or "BOTTOM" or "BOT" => Lane.Bottom,
        "SUPPORT" or "UTILITY" => Lane.Support,
        _ => null
    };

    // --- Lolalytics (keystone-filtered item builds) ---

    public static string LolalyticsTier(StatsRank r) => r switch
    {
        StatsRank.PlatinumPlus => "platinum_plus",
        StatsRank.EmeraldPlus => "emerald_plus",
        StatsRank.DiamondPlus => "diamond_plus",
        StatsRank.MasterPlus => "master_plus",
        StatsRank.Challenger => "challenger",
        StatsRank.Overall => "all",
        _ => "diamond_plus"
    };

    public static string LolalyticsRegion(StatsRegion r) => r switch
    {
        StatsRegion.World => "all",
        StatsRegion.NA => "na",
        StatsRegion.EUW => "euw",
        StatsRegion.EUN => "eune",
        StatsRegion.KR => "kr",
        StatsRegion.BR => "br",
        StatsRegion.JP => "jp",
        StatsRegion.LAN => "lan",
        StatsRegion.LAS => "las",
        StatsRegion.OCE => "oce",
        StatsRegion.RU => "ru",
        StatsRegion.TR => "tr",
        _ => "all"
    };

    public static string LolalyticsLane(Lane l) => l switch
    {
        Lane.Top => "top",
        Lane.Jungle => "jungle",
        Lane.Mid => "middle",
        Lane.Bottom => "bottom",
        Lane.Support => "support",
        _ => "middle"
    };

    // --- League client ---

    /// <summary>Maps the LCU's champ-select assigned position onto a lane.</summary>
    public static Lane? LaneFromLcuPosition(string? s) => (s ?? "").ToUpperInvariant() switch
    {
        "TOP" => Lane.Top,
        "JUNGLE" => Lane.Jungle,
        "MIDDLE" or "MID" => Lane.Mid,
        "BOTTOM" or "ADC" => Lane.Bottom,
        "UTILITY" or "SUPPORT" => Lane.Support,
        _ => null
    };

    public static string LcuPosition(Lane l) => l switch
    {
        Lane.Top => "TOP",
        Lane.Jungle => "JUNGLE",
        Lane.Mid => "MIDDLE",
        Lane.Bottom => "BOTTOM",
        Lane.Support => "UTILITY",
        _ => "MIDDLE"
    };

    // --- Display ---

    public static string LaneLabel(Lane l) => l switch
    {
        Lane.Top => "TOP",
        Lane.Jungle => "JUNGLE",
        Lane.Mid => "MID",
        Lane.Bottom => "BOT",
        Lane.Support => "SUPPORT",
        _ => "MID"
    };

    public static string RankLabel(StatsRank r) => r switch
    {
        StatsRank.PlatinumPlus => "Platinum+",
        StatsRank.EmeraldPlus => "Emerald+",
        StatsRank.DiamondPlus => "Diamond+",
        StatsRank.MasterPlus => "Master+",
        StatsRank.Challenger => "Challenger",
        StatsRank.Overall => "All Ranks",
        _ => "Diamond+"
    };

    public static string RegionLabel(StatsRegion r) => r == StatsRegion.World ? "World" : r.ToString();

    /// <summary>
    /// Lolalytics champion slugs are the lowercased LCU alias with non-alphanumerics stripped.
    /// Wukong is the sole mismatch (its LCU alias is "MonkeyKing").
    /// </summary>
    public static string ChampionSlug(string alias)
    {
        if (string.Equals(alias, "MonkeyKing", StringComparison.OrdinalIgnoreCase)) return "wukong";

        var sb = new StringBuilder(alias.Length);
        foreach (char c in alias)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Boot item ids, used to split a build path into "core" versus "boots".</summary>
    public static readonly HashSet<int> BootIds = new()
    {
        1001, 3006, 3009, 3020, 3047, 3111, 3117, 3158,
        3010, 3013, 3170, 3171, 3172, 3173, 3174, 3175, 3176
    };

    public static readonly Dictionary<int, string> SummonerSpellNames = new()
    {
        { 1, "Cleanse" }, { 3, "Exhaust" }, { 4, "Flash" }, { 6, "Ghost" }, { 7, "Heal" },
        { 11, "Smite" }, { 12, "Teleport" }, { 13, "Clarity" }, { 14, "Ignite" },
        { 21, "Barrier" }, { 30, "To the King!" }, { 31, "Poro Toss" }, { 32, "Mark" }, { 39, "Mark" }
    };

    public static string SpellName(int id) =>
        SummonerSpellNames.TryGetValue(id, out var n) ? n : $"Spell #{id}";
}
