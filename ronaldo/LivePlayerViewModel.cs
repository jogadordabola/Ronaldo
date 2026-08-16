using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using ronaldo.Stats;

namespace ronaldo;

/// <summary>Display-ready projection of one player in the current game.</summary>
public class LivePlayerViewModel
{
    public LivePlayerViewModel(LcuService lcu, LivePlayer player)
    {
        Name = string.IsNullOrWhiteSpace(player.Name) ? "Unknown" : player.Name;
        IsLocalPlayer = player.IsLocalPlayer;

        ChampionName = lcu.ChampionData.TryGetValue(player.ChampionId, out var c)
            ? c.Name
            : (player.ChampionId > 0 ? $"#{player.ChampionId}" : "No pick");

        ChampionIcon = IconCache.Get(ChampionIconPath(player.ChampionId));

        Spells = new[] { player.Spell1Id, player.Spell2Id }
            .Where(id => id > 0)
            .Select(id => new IconItem(
                IconCache.Get(lcu.SpellIcons.TryGetValue(id, out var p) ? p : null),
                lcu.SpellNames.TryGetValue(id, out var n) ? n : StatsCatalog.SpellName(id)))
            .ToList();

        RankText = player.RankText;
        WinRateText = player.WinRateText;
        MasteryText = player.MasteryText;
        PositionText = player.Position.ToUpperInvariant();
        Puuid = player.Puuid;
    }

    /// <summary>Champion portraits live alongside the other game assets.</summary>
    public static string? ChampionIconPath(int championId) =>
        championId > 0 ? $"/lol-game-data/assets/v1/champion-icons/{championId}.png" : null;

    public static IEnumerable<string?> IconPathsFor(LcuService lcu, LivePlayer player)
    {
        yield return ChampionIconPath(player.ChampionId);

        foreach (int id in new[] { player.Spell1Id, player.Spell2Id })
            if (id > 0 && lcu.SpellIcons.TryGetValue(id, out var p)) yield return p;
    }

    public string Name { get; }
    public string ChampionName { get; }
    public ImageSource? ChampionIcon { get; }
    public List<IconItem> Spells { get; }
    public string RankText { get; }
    public string WinRateText { get; }
    public string MasteryText { get; }
    public string PositionText { get; }
    public bool IsLocalPlayer { get; }
    public string Puuid { get; } = "";

    /// <summary>Only players the client identified can have a profile opened.</summary>
    public bool CanOpenProfile => Puuid.Length > 0;

    public bool HasRank => RankText.Length > 0;
    public bool HasWinRate => WinRateText.Length > 0;
    public bool HasMastery => MasteryText.Length > 0;
}
