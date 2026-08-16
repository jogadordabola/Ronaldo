using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using viktor.Stats;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace viktor;

public partial class MainWindow
{
    private readonly LcuService _lcu = new();
    private readonly BuildProvider _builds = new();
    private readonly System.Timers.Timer _pollTimer = new(350);

    /// <summary>Name used for the rune page this app writes, so it never clobbers a user's own pages.</summary>
    private const string ManagedPageName = "Viktor Build";

    private readonly SemaphoreSlim _pollGate = new(1, 1);

    private int _lastChampionId;
    private Lane? _lastLane;
    private ChampionBuildData? _current;
    private List<RunePageViewModel> _pageViewModels = new();
    private RunePageViewModel? _selected;

    private bool _filtersReady;

    /// <summary>Accent used for toggles, buttons and focus states, to match the card palette.</summary>
    private static readonly System.Windows.Media.Color AccentColor =
        System.Windows.Media.Color.FromRgb(0x9F, 0x7A, 0xEA);

    public MainWindow()
    {
        InitializeComponent();

        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.None, false);
        ApplicationAccentColorManager.Apply(AccentColor, ApplicationTheme.Dark, false, false);
        ApplicationThemeManager.Apply(this);

        PopulateFilters();

        _pollTimer.Elapsed += async (_, _) => await PollLeagueClient();
        _pollTimer.Start();
    }

    private void PopulateFilters()
    {
        foreach (var rank in new[]
                 {
                     StatsRank.PlatinumPlus, StatsRank.EmeraldPlus, StatsRank.DiamondPlus,
                     StatsRank.MasterPlus, StatsRank.Challenger, StatsRank.Overall
                 })
        {
            RankCombo.Items.Add(new ComboBoxItem { Content = StatsCatalog.RankLabel(rank), Tag = rank });
        }

        foreach (var region in new[]
                 {
                     StatsRegion.World, StatsRegion.NA, StatsRegion.EUW, StatsRegion.EUN,
                     StatsRegion.KR, StatsRegion.BR, StatsRegion.JP, StatsRegion.LAN,
                     StatsRegion.LAS, StatsRegion.OCE, StatsRegion.RU, StatsRegion.TR
                 })
        {
            RegionCombo.Items.Add(new ComboBoxItem { Content = StatsCatalog.RegionLabel(region), Tag = region });
        }

        RankCombo.SelectedIndex = 2;   // Diamond+
        RegionCombo.SelectedIndex = 0; // World
        _filtersReady = true;
    }

    private StatsRank SelectedRank =>
        (RankCombo.SelectedItem as ComboBoxItem)?.Tag is StatsRank r ? r : StatsRank.DiamondPlus;

    private StatsRegion SelectedRegion =>
        (RegionCombo.SelectedItem as ComboBoxItem)?.Tag is StatsRegion r ? r : StatsRegion.World;

    // ---- Polling ----

    private async Task PollLeagueClient()
    {
        // A fetch can outlast the poll interval; skip ticks rather than stacking requests.
        if (!await _pollGate.WaitAsync(0)) return;

        try
        {
            if (!_lcu.IsConnected)
            {
                if (!await _lcu.TryConnectAsync())
                {
                    Dispatcher.Invoke(() => StatusText.Text = "Searching for League Client...");
                    return;
                }
            }

            Dispatcher.Invoke(() => StatusText.Text = "Connected to League Client");

            await HandleAutoAcceptAsync();
            await HandleChampSelectAsync();
        }
        catch { }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task HandleAutoAcceptAsync()
    {
        bool enabled = Dispatcher.Invoke(() => AutoAcceptToggle.IsChecked ?? false);
        if (!enabled) return;

        string? readyCheck = await _lcu.GetAsync("lol-matchmaking/v1/ready-check");
        if (string.IsNullOrEmpty(readyCheck)) return;

        try
        {
            using var doc = JsonDocument.Parse(readyCheck);
            if (doc.RootElement.TryGetProperty("state", out var state) &&
                state.GetString() == "InProgress")
            {
                await _lcu.PostAsync("lol-matchmaking/v1/ready-check/accept", "{}");
            }
        }
        catch { }
    }

    private async Task HandleChampSelectAsync()
    {
        string? session = await _lcu.GetAsync("lol-champ-select/v1/session");

        if (string.IsNullOrEmpty(session))
        {
            if (_lastChampionId != 0)
            {
                _lastChampionId = 0;
                _lastLane = null;
                Dispatcher.Invoke(ClearDisplay);
            }
            return;
        }

        var (championId, isLocked, lane) = ParseChampSelect(session);
        if (championId <= 0) return;

        // Refetch when the champion changes, or when the assigned role resolves later.
        if (championId == _lastChampionId && lane == _lastLane)
        {
            Dispatcher.Invoke(() => UpdateChampionHeader(championId, isLocked));
            return;
        }

        _lastChampionId = championId;
        _lastLane = lane;

        await LoadBuildAsync(championId, isLocked, lane);
    }

    private (int ChampionId, bool IsLocked, Lane? Lane) ParseChampSelect(string session)
    {
        try
        {
            using var doc = JsonDocument.Parse(session);
            var root = doc.RootElement;

            int localCellId = root.GetProperty("localPlayerCellId").GetInt32();
            int championId = 0;
            bool isLocked = false;
            Lane? lane = null;

            if (root.TryGetProperty("myTeam", out var myTeam))
            {
                foreach (var player in myTeam.EnumerateArray())
                {
                    if (player.GetProperty("cellId").GetInt32() != localCellId) continue;

                    int picked = player.GetProperty("championId").GetInt32();
                    int intent = player.TryGetProperty("championPickIntent", out var pi) ? pi.GetInt32() : 0;

                    if (picked > 0) { championId = picked; isLocked = true; }
                    else if (intent > 0) championId = intent;

                    if (player.TryGetProperty("assignedPosition", out var pos))
                        lane = StatsCatalog.LaneFromLcuPosition(pos.GetString());

                    break;
                }
            }

            // Hovering before the pick lands only shows up in the action list.
            if (championId == 0 && root.TryGetProperty("actions", out var actions))
            {
                foreach (var group in actions.EnumerateArray())
                {
                    foreach (var act in group.EnumerateArray())
                    {
                        if (act.GetProperty("actorCellId").GetInt32() != localCellId) continue;
                        if (act.GetProperty("type").GetString() != "pick") continue;

                        int hovered = act.GetProperty("championId").GetInt32();
                        if (hovered <= 0) continue;

                        championId = hovered;
                        isLocked = act.GetProperty("completed").GetBoolean();
                        break;
                    }
                    if (championId > 0) break;
                }
            }

            return (championId, isLocked, lane);
        }
        catch
        {
            return (0, false, null);
        }
    }

    private async Task LoadBuildAsync(int championId, bool isLocked, Lane? lane)
    {
        string name = _lcu.ChampionData.TryGetValue(championId, out var d) ? d.Name : $"Champion #{championId}";
        string alias = _lcu.ChampionData.TryGetValue(championId, out var d2) ? d2.Key : name;

        Dispatcher.Invoke(() =>
        {
            UpdateChampionHeader(championId, isLocked);
            StatusText.Text = $"Loading {name} builds...";
        });

        var rank = Dispatcher.Invoke(() => SelectedRank);
        var region = Dispatcher.Invoke(() => SelectedRegion);

        var data = await _builds.GetBuildAsync(_lcu, championId, name, alias, lane, rank, region);

        // The user may have hovered a different champion while this was in flight.
        if (championId != _lastChampionId) return;

        _current = data;

        Dispatcher.Invoke(() =>
        {
            RenderBuild(data, isLocked);
            StatusText.Text = "Connected to League Client";
        });

        bool autoApply = Dispatcher.Invoke(() => AutoApplyToggle.IsChecked ?? false);
        if (autoApply && _pageViewModels.Count > 0)
            await ApplyPageAsync(_pageViewModels[0]);
    }

    // ---- Rendering ----

    private void UpdateChampionHeader(int championId, bool isLocked)
    {
        string name = _lcu.ChampionData.TryGetValue(championId, out var d) ? d.Name : $"Champion #{championId}";
        ChampNameText.Text = isLocked ? $"{name} (Locked)" : $"{name} (Hovering)";
    }

    private void RenderBuild(ChampionBuildData data, bool isLocked)
    {
        UpdateChampionHeader(data.ChampionId, isLocked);

        RoleBadge.Text = StatsCatalog.LaneLabel(data.Lane);
        PatchBadge.Text = string.IsNullOrEmpty(data.Patch) ? "" : $"Patch {data.Patch}";

        string filters = $"{StatsCatalog.RankLabel(data.Rank)} · {StatsCatalog.RegionLabel(data.Region)}";
        SourceLine.Text = string.IsNullOrEmpty(data.StatusLine)
            ? filters
            : $"{filters} · {data.StatusLine}";

        _pageViewModels = data.Pages
            .Select((p, i) => new RunePageViewModel(_lcu, p, i))
            .ToList();

        PagesList.ItemsSource = _pageViewModels;
        _selected = _pageViewModels.FirstOrDefault();

        EmptyHint.Visibility = _pageViewModels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_pageViewModels.Count == 0)
            EmptyHint.Text = $"No build data found for {data.ChampionName} at this rank/region.";
    }

    private void ClearDisplay()
    {
        _current = null;
        _pageViewModels = new List<RunePageViewModel>();
        _selected = null;

        PagesList.ItemsSource = null;
        ChampNameText.Text = "In Lobby / In Game";
        RoleBadge.Text = "—";
        PatchBadge.Text = "";
        SourceLine.Text = "";
        EmptyHint.Visibility = Visibility.Visible;
        EmptyHint.Text = "Standby for champion select...";
    }

    // ---- Applying runes ----

    /// <summary>
    /// Writes a page to the League client. Only the page this app manages is deleted; a user's
    /// own pages are removed only if the client has no free slot left.
    /// </summary>
    private async Task<bool> ApplyPageAsync(RunePageViewModel vm)
    {
        var page = vm.Page;
        if (!page.IsValid || !_lcu.IsConnected) return false;

        await DeleteManagedPagesAsync();

        string body = JsonSerializer.Serialize(new
        {
            name = ManagedPageName,
            primaryStyleId = page.PrimaryStyleId,
            selectedPerkIds = page.PerkIds,
            subStyleId = page.SubStyleId,
            current = true
        });

        bool ok = await _lcu.PostAsync("lol-perks/v1/pages", body);

        if (!ok)
        {
            // Most likely the rune page slots are full; free one and try once more.
            if (await DeleteOneDeletablePageAsync())
                ok = await _lcu.PostAsync("lol-perks/v1/pages", body);
        }

        if (ok)
        {
            Dispatcher.Invoke(() =>
            {
                foreach (var other in _pageViewModels) other.IsApplied = false;
                vm.IsApplied = true;
                _selected = vm;
                StatusText.Text = $"Applied: {vm.Label} — {vm.KeystoneName}";
            });
        }
        else
        {
            Dispatcher.Invoke(() => StatusText.Text = "Could not write rune page (all pages may be in use)");
        }

        return ok;
    }

    private async Task DeleteManagedPagesAsync()
    {
        string? json = await _lcu.GetAsync("lol-perks/v1/pages");
        if (string.IsNullOrEmpty(json)) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            var ids = new List<int>();
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                if (!p.TryGetProperty("name", out var n)) continue;
                if (n.GetString() != ManagedPageName) continue;
                if (p.TryGetProperty("isDeletable", out var del) && !del.GetBoolean()) continue;
                ids.Add(p.GetProperty("id").GetInt32());
            }

            foreach (int id in ids)
                await _lcu.DeleteAsync($"lol-perks/v1/pages/{id}");
        }
        catch { }
    }

    private async Task<bool> DeleteOneDeletablePageAsync()
    {
        string? json = await _lcu.GetAsync("lol-perks/v1/pages");
        if (string.IsNullOrEmpty(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;

            foreach (var p in doc.RootElement.EnumerateArray())
            {
                if (!p.TryGetProperty("isDeletable", out var del) || !del.GetBoolean()) continue;
                await _lcu.DeleteAsync($"lol-perks/v1/pages/{p.GetProperty("id").GetInt32()}");
                return true;
            }
        }
        catch { }

        return false;
    }

    // ---- Events ----

    private async void PageCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is RunePageViewModel vm)
            await ApplyPageAsync(vm);
    }

    private async void ImportBtn_Click(object sender, RoutedEventArgs e)
    {
        var target = _selected ?? _pageViewModels.FirstOrDefault();
        if (target != null) await ApplyPageAsync(target);
    }

    private async void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filtersReady || _current == null) return;

        int championId = _current.ChampionId;
        await LoadBuildAsync(championId, true, _lastLane);
    }
}
