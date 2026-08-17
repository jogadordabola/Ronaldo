using System.Collections.Generic;
using System.Linq;

namespace ronaldo.Stats;

/// <summary>An item build path, optionally conditioned on a specific keystone.</summary>
public class ItemBuild
{
    public List<int> StarterIds { get; set; } = new();
    public List<int> CoreIds { get; set; } = new();
    public int BootsId { get; set; }
    public List<int> SituationalIds { get; set; } = new();
    public List<int> SpellIds { get; set; } = new();

    public double WinRate { get; set; }
    public int Games { get; set; }

    /// <summary>True when these items were filtered to this page's keystone.</summary>
    public bool KeystoneSpecific { get; set; }

    public string Source { get; set; } = "";
}

/// <summary>A complete rune page plus the item build that goes with it.</summary>
public class RunePage
{
    public string Label { get; set; } = "";      // "Most Popular", "Highest Winrate", ...
    public string Source { get; set; } = "";     // "Lolalytics" / "U.GG"

    public int PrimaryStyleId { get; set; }
    public int SubStyleId { get; set; }

    /// <summary>9 ids in LCU order: keystone, 3 primary minors, 2 secondary, 3 shards.</summary>
    public List<int> PerkIds { get; set; } = new();

    public double WinRate { get; set; }
    public double PickRate { get; set; }
    public int Games { get; set; }

    public ItemBuild? Items { get; set; }

    public int KeystoneId => PerkIds.Count > 0 ? PerkIds[0] : 0;

    /// <summary>Identity used to drop duplicate pages returned by different sources.</summary>
    public string Signature =>
        PrimaryStyleId + ":" + SubStyleId + ":" + string.Join(",", PerkIds.Take(6));

    public bool IsValid => PerkIds.Count == 9 && PrimaryStyleId > 0 && SubStyleId > 0;
}

/// <summary>Where the lane being shown came from, so the UI can say how sure it is.</summary>
public enum LaneSource
{
    /// <summary>The user picked it from the lane dropdown.</summary>
    Manual,

    /// <summary>Champ select told us the assigned position.</summary>
    Assigned,

    /// <summary>No role was available, so the champion's most-played lane was used.</summary>
    Detected
}

/// <summary>Everything the UI shows for one champion at one lane/rank/region.</summary>
/// <summary>One item option for a particular purchase slot.</summary>
public class SlotItem
{
    public int ItemId { get; set; }
    public int Play { get; set; }
    public int Win { get; set; }
    public double PickRate { get; set; }

    public double WinRatePercent => Play > 0 ? Win * 100.0 / Play : 0;
}

/// <summary>
/// The popular choices for one purchase slot — what tends to be bought fourth, fifth or sixth —
/// most picked first, as op.gg orders them.
/// </summary>
public class ItemSlot
{
    /// <summary>Which purchase this is: 1 for the first item through 6 for the last.</summary>
    public int Slot { get; set; }

    public List<SlotItem> Items { get; set; } = new();
}

/// <summary>
/// How the champion fares against one opponent in the same lane, over the rank and region
/// currently selected. Win and play are this champion's, not the opponent's.
/// </summary>
public class ChampionMatchup
{
    public int OpponentId { get; set; }
    public int Play { get; set; }
    public int Win { get; set; }

    public double WinRatePercent => Play > 0 ? Win * 100.0 / Play : 0;

    /// <summary>Above an even matchup, so this is a lane the champion tends to win.</summary>
    public bool IsFavourable => WinRatePercent >= 50;
}

public class ChampionBuildData
{
    public int ChampionId { get; set; }
    public string ChampionName { get; set; } = "";
    public Lane Lane { get; set; } = Lane.Mid;
    public LaneSource LaneSource { get; set; } = LaneSource.Detected;
    public StatsRank Rank { get; set; } = StatsRank.DiamondPlus;
    public StatsRegion Region { get; set; } = StatsRegion.World;
    public string Patch { get; set; } = "";

    public List<RunePage> Pages { get; set; } = new();

    /// <summary>Play rate per lane, used to show where the champion is actually played.</summary>
    public Dictionary<Lane, double> LanePlayRates { get; set; } = new();

    /// <summary>Lane matchups, best win rate first. Empty when the source had none.</summary>
    public List<ChampionMatchup> Matchups { get; set; } = new();


    /// <summary>Set when every online source failed and we fell back to the in-client data.</summary>
    public bool IsFallback { get; set; }

    public string StatusLine { get; set; } = "";
}
