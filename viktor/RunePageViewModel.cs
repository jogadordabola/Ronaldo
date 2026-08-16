using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using viktor.Stats;

namespace viktor;

/// <summary>One icon plus the name shown in its tooltip.</summary>
public class IconItem
{
    public IconItem(ImageSource? source, string name)
    {
        Source = source;
        Name = name;
    }

    public ImageSource? Source { get; }
    public string Name { get; }
}

/// <summary>
/// Display-ready projection of a <see cref="RunePage"/>: ids resolved to names and icons
/// using the League client's own game data, and numbers formatted for the card layout.
/// </summary>
public class RunePageViewModel : INotifyPropertyChanged
{
    public RunePage Page { get; }

    public RunePageViewModel(LcuService lcu, RunePage page, int index)
    {
        Page = page;
        Index = index;

        var perks = page.PerkIds;

        KeystoneName = Perk(lcu, perks[0]).Name;
        KeystoneIcon = Perk(lcu, perks[0]).Source;

        PrimaryTreeText = RuneAssembler.StyleName(lcu, page.PrimaryStyleId).ToUpperInvariant();
        SecondaryTreeText = RuneAssembler.StyleName(lcu, page.SubStyleId).ToUpperInvariant();
        PrimaryTreeIcon = Style(lcu, page.PrimaryStyleId);
        SecondaryTreeIcon = Style(lcu, page.SubStyleId);

        PrimaryRunes = perks.Skip(1).Take(3).Select(id => Perk(lcu, id)).ToList();
        SecondaryRunes = perks.Skip(4).Take(2).Select(id => Perk(lcu, id)).ToList();
        Shards = perks.Skip(6).Take(3).Select(id => Perk(lcu, id)).ToList();

        Label = page.Label;
        SourceText = page.Source;

        PickRateValue = page.PickRate > 0
            ? page.PickRate.ToString("0.#", CultureInfo.InvariantCulture) + "%"
            : "—";
        WinRateValue = page.WinRate > 0
            ? page.WinRate.ToString("0.0", CultureInfo.InvariantCulture) + "%"
            : "—";
        GamesValue = page.Games > 0
            ? page.Games.ToString("N0", CultureInfo.InvariantCulture)
            : "—";

        var items = page.Items;
        if (items == null) return;

        CoreItems = items.CoreIds.Select(id => Item(lcu, id)).ToList();
        StarterItems = items.StarterIds.Select(id => Item(lcu, id)).ToList();
        Situational = items.SituationalIds.Select(id => Item(lcu, id)).ToList();
        Spells = items.SpellIds.Select(id => Spell(lcu, id)).ToList();

        if (items.BootsId > 0) Boots = new List<IconItem> { Item(lcu, items.BootsId) };

        ItemsNote = items.KeystoneSpecific
            ? $"Items from {KeystoneName} games"
            : $"Champion-wide build ({items.Source})";

        ItemsWinRateText = items.WinRate > 0
            ? $"{items.WinRate.ToString("0.0", CultureInfo.InvariantCulture)}% win " +
              $"· {items.Games.ToString("N0", CultureInfo.InvariantCulture)} games"
            : "";
    }

    // ---- Icon lookups ----

    private static IconItem Perk(LcuService lcu, int id) => new(
        IconCache.Get(lcu.PerkIcons.TryGetValue(id, out var p) ? p : null),
        RuneAssembler.PerkName(lcu, id));

    private static ImageSource? Style(LcuService lcu, int id) =>
        IconCache.Get(lcu.StyleIcons.TryGetValue(id, out var p) ? p : null);

    private static IconItem Item(LcuService lcu, int id) => new(
        IconCache.Get(lcu.ItemIcons.TryGetValue(id, out var p) ? p : null),
        RuneAssembler.ItemName(lcu, id));

    private static IconItem Spell(LcuService lcu, int id) => new(
        IconCache.Get(lcu.SpellIcons.TryGetValue(id, out var p) ? p : null),
        lcu.SpellNames.TryGetValue(id, out var n) ? n : StatsCatalog.SpellName(id));

    /// <summary>Every icon this page needs, so they can be fetched before the card is built.</summary>
    public static IEnumerable<string?> IconPathsFor(LcuService lcu, RunePage page)
    {
        foreach (int id in page.PerkIds)
            if (lcu.PerkIcons.TryGetValue(id, out var p)) yield return p;

        if (lcu.StyleIcons.TryGetValue(page.PrimaryStyleId, out var s1)) yield return s1;
        if (lcu.StyleIcons.TryGetValue(page.SubStyleId, out var s2)) yield return s2;

        var items = page.Items;
        if (items == null) yield break;

        var ids = items.CoreIds
            .Concat(items.StarterIds)
            .Concat(items.SituationalIds)
            .Append(items.BootsId);

        foreach (int id in ids)
            if (id > 0 && lcu.ItemIcons.TryGetValue(id, out var p)) yield return p;

        foreach (int id in items.SpellIds)
            if (lcu.SpellIcons.TryGetValue(id, out var p)) yield return p;
    }

    // ---- Bound properties ----

    public int Index { get; }

    public string Label { get; } = "";
    public string SourceText { get; } = "";
    public string KeystoneName { get; } = "";
    public ImageSource? KeystoneIcon { get; }

    public string PickRateValue { get; } = "";
    public string WinRateValue { get; } = "";
    public string GamesValue { get; } = "";

    public string PrimaryTreeText { get; } = "";
    public string SecondaryTreeText { get; } = "";
    public ImageSource? PrimaryTreeIcon { get; }
    public ImageSource? SecondaryTreeIcon { get; }

    public List<IconItem> PrimaryRunes { get; } = new();
    public List<IconItem> SecondaryRunes { get; } = new();
    public List<IconItem> Shards { get; } = new();

    public List<IconItem> CoreItems { get; } = new();
    public List<IconItem> StarterItems { get; } = new();
    public List<IconItem> Boots { get; } = new();
    public List<IconItem> Situational { get; } = new();
    public List<IconItem> Spells { get; } = new();

    public string ItemsNote { get; } = "";
    public string ItemsWinRateText { get; } = "";

    public bool HasSituational => Situational.Count > 0;
    public bool HasSpells => Spells.Count > 0;

    private bool _isApplied;

    /// <summary>True for the page currently written to the League client.</summary>
    public bool IsApplied
    {
        get => _isApplied;
        set
        {
            if (_isApplied == value) return;
            _isApplied = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActionHint));
        }
    }

    /// <summary>Prompt shown in the card footer; blank once this page is the active one.</summary>
    public string ActionHint => _isApplied ? "" : "CLICK TO APPLY";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
