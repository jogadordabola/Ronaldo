using System;
using System.Collections.Generic;
using System.Linq;

namespace ronaldo.Stats;

/// <summary>
/// Turns a loose set of rune ids from an external source into a page the LCU will accept:
/// the tree ids resolved from the runes themselves, and the ids ordered
/// keystone -> three primary rows -> two secondary -> three shards.
/// </summary>
public static class RuneAssembler
{
    /// <summary>
    /// Fallback tree lookup for runes the client's data didn't cover. Rune ids are grouped by
    /// tree in the 80xx-84xx range; these are the ids that break that pattern.
    /// </summary>
    private static readonly Dictionary<int, int> KnownStyleOverrides = new()
    {
        { 9923, 8100 }, // Hail of Blades
        { 9101, 8000 }, // Overheal
        { 9111, 8000 }, // Triumph
        { 9104, 8000 }, // Legend: Alacrity
        { 9105, 8000 }, // Legend: Haste
        { 9103, 8000 }, // Legend: Bloodline
        { 9922, 8300 }  // Nimbus Cloak
    };

    public static int StyleOf(LcuService lcu, int perkId)
    {
        if (lcu.PerkStyleOf.TryGetValue(perkId, out var style)) return style;
        if (KnownStyleOverrides.TryGetValue(perkId, out var known)) return known;

        // Runes are otherwise numbered 8000-8499, one hundred-block per tree.
        if (perkId >= 8000 && perkId < 8500) return perkId / 100 * 100;
        return 0;
    }

    private static int SlotOf(LcuService lcu, int perkId) =>
        lcu.PerkSlotOf.TryGetValue(perkId, out var slot) ? slot : int.MaxValue;

    /// <summary>Picks the tree that most of the given runes belong to.</summary>
    private static int DominantStyle(LcuService lcu, IEnumerable<int> perks)
    {
        var styles = perks.Select(p => StyleOf(lcu, p)).Where(s => s > 0).ToList();
        if (styles.Count == 0) return 0;

        return styles.GroupBy(s => s)
                     .OrderByDescending(g => g.Count())
                     .First().Key;
    }

    /// <summary>
    /// Assembles a page. Returns null when the inputs can't produce a page the client would
    /// accept, so a half-valid page is never pushed to the League client.
    /// </summary>
    public static RunePage? Build(
        LcuService lcu,
        IReadOnlyList<int> primary, IReadOnlyList<int> secondary, IReadOnlyList<int> shards,
        string label, string source, double winRate, double pickRate, int games,
        int primaryStyleHint = 0, int subStyleHint = 0)
    {
        if (primary.Count < 4 || secondary.Count < 2 || shards.Count < 3) return null;

        int primaryStyle = primaryStyleHint > 0 ? primaryStyleHint : DominantStyle(lcu, primary);
        int subStyle = subStyleHint > 0 ? subStyleHint : DominantStyle(lcu, secondary);
        if (primaryStyle == 0 || subStyle == 0 || primaryStyle == subStyle) return null;

        // Keep only runes that actually belong to the resolved tree, then order by row.
        var primaryOrdered = primary
            .Where(p => StyleOf(lcu, p) == primaryStyle)
            .Distinct()
            .OrderBy(p => SlotOf(lcu, p))
            .ToList();

        var secondaryOrdered = secondary
            .Where(p => StyleOf(lcu, p) == subStyle)
            .Distinct()
            .OrderBy(p => SlotOf(lcu, p))
            .ToList();

        if (primaryOrdered.Count < 4 || secondaryOrdered.Count < 2) return null;

        var perkIds = new List<int>(9);
        perkIds.AddRange(primaryOrdered.Take(4));
        perkIds.AddRange(secondaryOrdered.Take(2));
        perkIds.AddRange(shards.Take(3));

        if (perkIds.Count != 9 || perkIds.Any(id => id <= 0)) return null;

        return new RunePage
        {
            Label = label,
            Source = source,
            PrimaryStyleId = primaryStyle,
            SubStyleId = subStyle,
            PerkIds = perkIds,
            WinRate = winRate,
            PickRate = pickRate,
            Games = games
        };
    }

    public static string PerkName(LcuService lcu, int id) =>
        lcu.PerkNames.TryGetValue(id, out var n) ? n : $"Rune #{id}";

    public static string StyleName(LcuService lcu, int id) =>
        lcu.StyleNames.TryGetValue(id, out var n) ? n : "Tree";

    public static string ItemName(LcuService lcu, int id) =>
        lcu.ItemNames.TryGetValue(id, out var n) ? n : $"Item #{id}";
}
