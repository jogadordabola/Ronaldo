using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using viktor.Stats;

namespace viktor;

/// <summary>
/// Display-ready projection of a <see cref="RunePage"/>: ids resolved to names using the
/// League client's own game data, and numbers formatted for the card layout.
/// </summary>
public class RunePageViewModel : INotifyPropertyChanged
{
    public RunePage Page { get; }

    public RunePageViewModel(LcuService lcu, RunePage page, int index)
    {
        Page = page;
        Index = index;

        var perks = page.PerkIds;
        KeystoneName = RuneAssembler.PerkName(lcu, perks[0]);

        PrimaryTreeText = RuneAssembler.StyleName(lcu, page.PrimaryStyleId).ToUpperInvariant();
        SecondaryTreeText = RuneAssembler.StyleName(lcu, page.SubStyleId).ToUpperInvariant();

        PrimaryRunesText = string.Join("  •  ", perks.Skip(1).Take(3).Select(id => RuneAssembler.PerkName(lcu, id)));
        SecondaryRunesText = string.Join("  •  ", perks.Skip(4).Take(2).Select(id => RuneAssembler.PerkName(lcu, id)));
        ShardsText = string.Join("  •  ", perks.Skip(6).Take(3).Select(id => RuneAssembler.PerkName(lcu, id)));

        Label = page.Label;
        SourceText = page.Source;

        PickRateText = page.PickRate > 0
            ? page.PickRate.ToString("0.#", CultureInfo.InvariantCulture) + "% pick"
            : "";
        WinRateText = page.WinRate > 0
            ? page.WinRate.ToString("0.0", CultureInfo.InvariantCulture) + "% WR"
            : "";
        GamesText = page.Games > 0
            ? page.Games.ToString("N0", CultureInfo.InvariantCulture) + " games"
            : "";

        var items = page.Items;
        if (items != null)
        {
            CoreItemsText = items.CoreIds.Count > 0
                ? string.Join("  ➔  ", items.CoreIds.Select(id => RuneAssembler.ItemName(lcu, id)))
                : "—";

            StartersText = items.StarterIds.Count > 0
                ? string.Join(" + ", items.StarterIds.Select(id => RuneAssembler.ItemName(lcu, id)))
                : "—";

            BootsText = items.BootsId > 0 ? RuneAssembler.ItemName(lcu, items.BootsId) : "—";

            SituationalText = items.SituationalIds.Count > 0
                ? string.Join("  •  ", items.SituationalIds.Select(id => RuneAssembler.ItemName(lcu, id)))
                : "";

            SkillOrderText = string.IsNullOrEmpty(items.SkillOrder) ? "—" : items.SkillOrder;

            SpellsText = items.SpellIds.Count > 0
                ? string.Join(" + ", items.SpellIds.Select(StatsCatalog.SpellName))
                : "";

            ItemsNote = items.KeystoneSpecific
                ? $"Items from {KeystoneName} games"
                : $"Champion-wide build ({items.Source})";

            ItemsWinRateText = items.WinRate > 0
                ? items.WinRate.ToString("0.0", CultureInfo.InvariantCulture) + "% WR on this build"
                : "";
        }
        else
        {
            CoreItemsText = "—";
            StartersText = "—";
            BootsText = "—";
            SkillOrderText = "—";
        }
    }

    public int Index { get; }

    public string Label { get; } = "";
    public string SourceText { get; } = "";
    public string KeystoneName { get; } = "";

    public string PickRateText { get; } = "";
    public string WinRateText { get; } = "";
    public string GamesText { get; } = "";

    public string PrimaryTreeText { get; } = "";
    public string PrimaryRunesText { get; } = "";
    public string SecondaryTreeText { get; } = "";
    public string SecondaryRunesText { get; } = "";
    public string ShardsText { get; } = "";

    public string CoreItemsText { get; } = "";
    public string StartersText { get; } = "";
    public string BootsText { get; } = "";
    public string SituationalText { get; } = "";
    public string SkillOrderText { get; } = "";
    public string SpellsText { get; } = "";
    public string ItemsNote { get; } = "";
    public string ItemsWinRateText { get; } = "";

    public bool HasSituational => !string.IsNullOrEmpty(SituationalText);
    public bool HasSpells => !string.IsNullOrEmpty(SpellsText);

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
            OnPropertyChanged(nameof(AppliedBadge));
        }
    }

    public string AppliedBadge => _isApplied ? "APPLIED" : "CLICK TO APPLY";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
