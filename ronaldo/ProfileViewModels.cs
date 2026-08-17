using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Media;
using ronaldo.Stats;

namespace ronaldo;

/// <summary>A champion in the most-played box.</summary>
public class ChampionStatViewModel
{
    public ChampionStatViewModel(LcuService lcu, ChampionStat stat)
    {
        Icon = IconCache.Get(LivePlayerViewModel.ChampionIconPath(stat.ChampionId));
        Name = lcu.ChampionData.TryGetValue(stat.ChampionId, out var c) ? c.Name : $"#{stat.ChampionId}";
        RecordText = $"{stat.Wins}W {stat.Losses}L";
        WinRateText = stat.WinRate.ToString("0", CultureInfo.InvariantCulture) + "%";
        GamesText = stat.Games == 1 ? "1 game" : $"{stat.Games} games";
        IsStrong = stat.WinRate >= 50;
    }

    public ImageSource? Icon { get; }
    public string Name { get; }
    public string RecordText { get; }
    public string WinRateText { get; }
    public string GamesText { get; }
    public bool IsStrong { get; }
}

/// <summary>One item option in a purchase-slot column.</summary>
public class SlotItemViewModel
{
    public SlotItemViewModel(LcuService lcu, SlotItem item)
    {
        Icon = IconCache.Get(lcu.ItemIcons.TryGetValue(item.ItemId, out var path) ? path : null);
        Name = lcu.ItemNames.TryGetValue(item.ItemId, out var n) ? n : $"#{item.ItemId}";

        WinRateText = item.WinRatePercent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        GamesText = $"{item.Play:N0} games";
        IsStrong = item.WinRatePercent >= 50;
    }

    public ImageSource? Icon { get; }
    public string Name { get; }
    public string WinRateText { get; }
    public string GamesText { get; }
    public bool IsStrong { get; }
}

/// <summary>One column of the fourth/fifth/sixth item table.</summary>
public class ItemSlotViewModel
{
    public ItemSlotViewModel(LcuService lcu, ItemSlot slot, int take)
    {
        Title = slot.Slot switch
        {
            4 => "FOURTH ITEM",
            5 => "FIFTH ITEM",
            6 => "SIXTH ITEM",
            _ => $"ITEM {slot.Slot}"
        };

        // Already ordered most-picked-first by op.gg, which is the order worth keeping: the
        // top of each column is what people actually build, not the highest win rate.
        Items = slot.Items.Take(take).Select(i => new SlotItemViewModel(lcu, i)).ToList();
    }

    public string Title { get; }
    public List<SlotItemViewModel> Items { get; }
}

/// <summary>One lane matchup in the strong/weak lists.</summary>
public class MatchupViewModel
{
    public MatchupViewModel(LcuService lcu, ChampionMatchup matchup)
    {
        Icon = IconCache.Get(LivePlayerViewModel.ChampionIconPath(matchup.OpponentId));
        Name = lcu.ChampionData.TryGetValue(matchup.OpponentId, out var c)
            ? c.Name
            : $"#{matchup.OpponentId}";

        WinRateText = matchup.WinRatePercent.ToString("0", CultureInfo.InvariantCulture) + "%";

        // The sample is worth showing: these are single matchups, so they are far thinner than
        // the champion-wide numbers on the rune cards.
        GamesText = matchup.Play == 1 ? "1 game" : $"{matchup.Play:N0} games";
        IsFavourable = matchup.IsFavourable;
    }

    public ImageSource? Icon { get; }
    public string Name { get; }
    public string WinRateText { get; }
    public string GamesText { get; }
    public bool IsFavourable { get; }
}

public class RankEntryViewModel
{
    public RankEntryViewModel(RankEntry entry)
    {
        QueueName = entry.QueueName;
        Emblem = IconCache.Get(entry.EmblemUrl);

        if (!entry.IsRanked)
        {
            TierText = "Unranked";
            RecordText = "";
            WinRateText = "";
            return;
        }

        string tier = Capitalize(entry.Tier);
        TierText = entry.Division.Length > 0 && entry.Division != "NA"
            ? $"{tier} {entry.Division}"
            : tier;

        LpText = $"{entry.LeaguePoints} LP";

        // Without the losses there is no ratio to show, so say why rather than implying 100%.
        if (!entry.LossesKnown)
        {
            RecordText = entry.Wins == 1 ? "1 win" : $"{entry.Wins} wins";

            // Kept short: the rank card is narrow and a longer line is clipped.
            WinRateText = "losses hidden";
            return;
        }

        RecordText = $"{entry.Wins}W {entry.Losses}L";
        WinRateText = entry.HasWinRate
            ? entry.WinRate.ToString("0", CultureInfo.InvariantCulture) + "% win rate"
            : "";
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    public string QueueName { get; } = "";
    public string TierText { get; } = "";
    public string LpText { get; } = "";
    public string RecordText { get; } = "";
    public string WinRateText { get; } = "";
    public ImageSource? Emblem { get; }

    public bool HasLp => LpText.Length > 0;
    public bool HasRecord => RecordText.Length > 0;
}

/// <summary>One player row inside an expanded match scoreboard.</summary>
public class ScoreboardRowViewModel
{
    public ScoreboardRowViewModel(LcuService lcu, ScoreboardPlayer p)
    {
        Name = p.Name.Length > 0 ? p.Name : "Unknown";
        ChampionIcon = IconCache.Get(LivePlayerViewModel.ChampionIconPath(p.ChampionId));
        ChampionName = lcu.ChampionData.TryGetValue(p.ChampionId, out var c) ? c.Name : "";
        KdaText = $"{p.Kills} / {p.Deaths} / {p.Assists}";
        CsText = $"{p.Cs} CS";
        DamageText = p.Damage >= 1000
            ? (p.Damage / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K dmg"
            : $"{p.Damage} dmg";
        LevelText = $"Lv {p.Level}";
        IsMe = p.IsMe;
        Puuid = p.Puuid;

        Items = p.Items
            .Select(id => new IconItem(
                IconCache.Get(lcu.ItemIcons.TryGetValue(id, out var path) ? path : null),
                lcu.ItemNames.TryGetValue(id, out var n) ? n : $"#{id}"))
            .ToList();
    }

    public string Name { get; }
    public string ChampionName { get; }
    public ImageSource? ChampionIcon { get; }
    public string KdaText { get; }
    public string CsText { get; }
    public string DamageText { get; }
    public string LevelText { get; }
    public bool IsMe { get; }
    public string Puuid { get; } = "";
    public List<IconItem> Items { get; }

    /// <summary>
    /// Bots and players the client anonymises come back with a blank or all-zero puuid,
    /// and there is no profile to open for them.
    /// </summary>
    public bool CanOpenProfile =>
        Puuid.Length > 0 && Puuid.Any(c => c != '0' && c != '-');
}

/// <summary>A match in the history list, expandable into its full scoreboard.</summary>
public class MatchViewModel : INotifyPropertyChanged
{
    private readonly LcuService _lcu;
    private readonly MatchSummary _match;
    private readonly Func<long, Task<List<ScoreboardPlayer>>>? _loadScoreboard;

    /// <param name="loadScoreboard">
    /// Fetches the full participant list on demand. The match-history list only carries the
    /// signed-in player, so the scoreboard needs a per-game request the first time it opens.
    /// </param>
    public MatchViewModel(LcuService lcu, MatchSummary match,
                          Func<long, Task<List<ScoreboardPlayer>>>? loadScoreboard = null)
    {
        _lcu = lcu;
        _match = match;
        _loadScoreboard = loadScoreboard;

        ChampionIcon = IconCache.Get(LivePlayerViewModel.ChampionIconPath(match.ChampionId));
        ChampionName = lcu.ChampionData.TryGetValue(match.ChampionId, out var c) ? c.Name : "";

        ResultText = match.Won ? "VICTORY" : "DEFEAT";
        QueueText = match.QueueName;
        KdaText = $"{match.Kills} / {match.Deaths} / {match.Assists}";
        RatioText = match.Kda.ToString("0.00", CultureInfo.InvariantCulture) + " KDA";
        CsText = $"{match.Cs} CS";
        DurationText = match.Duration.TotalMinutes >= 1
            ? $"{(int)match.Duration.TotalMinutes}m {match.Duration.Seconds}s"
            : $"{match.Duration.Seconds}s";
        WhenText = Ago(match.PlayedAt);

        if (match.LpDelta is { } lp)
            LpText = lp > 0 ? $"+{lp} LP" : $"{lp} LP";

        Items = match.Items
            .Select(id => new IconItem(
                IconCache.Get(lcu.ItemIcons.TryGetValue(id, out var path) ? path : null),
                lcu.ItemNames.TryGetValue(id, out var n) ? n : $"#{id}"))
            .ToList();
    }

    private static string Ago(DateTime when)
    {
        if (when == default) return "";

        var span = DateTime.Now - when;
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        return when.ToString("d MMM", CultureInfo.CurrentCulture);
    }

    public ImageSource? ChampionIcon { get; }
    public string ChampionName { get; }
    public string ResultText { get; }
    public string QueueText { get; }
    public string KdaText { get; }
    public string RatioText { get; }
    public string CsText { get; }
    public string DurationText { get; }
    public string WhenText { get; }
    public List<IconItem> Items { get; }

    public string LpText { get; } = "";
    public bool HasLp => LpText.Length > 0;
    public bool LpGained => _match.LpDelta is > 0;

    public bool Won => _match.Won;

    /// <summary>Team colours the way the end-of-game screen splits them.</summary>
    public List<ScoreboardRowViewModel> BlueTeam { get; private set; } = new();
    public List<ScoreboardRowViewModel> RedTeam { get; private set; } = new();

    public bool HasScoreboard => _match.Scoreboard.Count > 0;

    private bool _isExpanded;
    private bool _loaded;

    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExpandHint));
        }
    }

    private bool _isLoading;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value) return;
            _isLoading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExpandHint));
        }
    }

    /// <summary>Opens or closes the scoreboard, fetching the participants the first time.</summary>
    public async Task ToggleAsync()
    {
        if (_isExpanded)
        {
            IsExpanded = false;
            return;
        }

        if (!_loaded)
        {
            _loaded = true;
            IsLoading = true;

            var players = _match.Scoreboard;

            // The list payload only has us, so pull the real roster on first open.
            if (players.Count <= 1 && _loadScoreboard != null)
            {
                var fetched = await _loadScoreboard(_match.GameId);
                if (fetched.Count > 0)
                {
                    players = fetched;
                    _match.Scoreboard = fetched;
                }
            }

            await IconCache.PreloadAsync(players.SelectMany(p =>
                new[] { LivePlayerViewModel.ChampionIconPath(p.ChampionId) }
                    .Concat(p.Items.Select(id =>
                        _lcu.ItemIcons.TryGetValue(id, out var path) ? path : null))));

            BlueTeam = players.Where(p => p.TeamId == 100)
                .Select(p => new ScoreboardRowViewModel(_lcu, p)).ToList();
            RedTeam = players.Where(p => p.TeamId != 100)
                .Select(p => new ScoreboardRowViewModel(_lcu, p)).ToList();

            IsLoading = false;
            OnPropertyChanged(nameof(BlueTeam));
            OnPropertyChanged(nameof(RedTeam));
            OnPropertyChanged(nameof(ScoreboardMissing));
            OnPropertyChanged(nameof(HasScoreboard));
        }

        IsExpanded = true;
    }

    /// <summary>True when the client would not give us the other players.</summary>
    public bool ScoreboardMissing => _loaded && BlueTeam.Count + RedTeam.Count <= 1;

    public string ExpandHint =>
        _isLoading ? "LOADING..." : _isExpanded ? "HIDE SCOREBOARD" : "VIEW SCOREBOARD";

    /// <summary>Every icon this match needs, for preloading before the list is shown.</summary>
    public static IEnumerable<string?> IconPathsFor(LcuService lcu, MatchSummary match)
    {
        yield return LivePlayerViewModel.ChampionIconPath(match.ChampionId);

        foreach (var p in match.Scoreboard)
        {
            yield return LivePlayerViewModel.ChampionIconPath(p.ChampionId);

            foreach (int id in p.Items)
                if (lcu.ItemIcons.TryGetValue(id, out var path)) yield return path;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
