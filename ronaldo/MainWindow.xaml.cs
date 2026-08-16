using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ronaldo.Stats;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ronaldo;

public partial class MainWindow
{
    private readonly LcuService _lcu = new();
    private readonly BuildProvider _builds = new();
    private readonly ItemSetService _itemSets;
    private readonly GameSessionService _gameSession;
    private readonly System.Timers.Timer _pollTimer = new(350);

    /// <summary>Name used for the rune page this app writes, so it never clobbers a user's own pages.</summary>
    private const string ManagedPageName = "Ronaldo Build";

    /// <summary>The page name used before the app was renamed, still cleaned up so none are left behind.</summary>
    private const string LegacyPageName = "Viktor Build";

    /// <summary>Accent used for toggles, buttons and focus states, to match the card palette.</summary>
    private static readonly Color AccentColor = Color.FromRgb(0x9F, 0x7A, 0xEA);

    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly AppSettings _settings;

    private int _lastChampionId;
    private Lane? _lastAssignedLane;
    private ChampionBuildData? _current;
    private List<RunePageViewModel> _pageViewModels = new();
    private RunePageViewModel? _selected;

    private bool _filtersReady;
    private bool _inChampSelect;
    private bool _gameWasRunning;
    private bool _inGame;
    private long _shownGameId;

    public MainWindow() : this(AppSettings.Load()) { }

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();

        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.None, false);
        ApplicationAccentColorManager.Apply(AccentColor, ApplicationTheme.Dark, false, false);
        ApplicationThemeManager.Apply(this);

        _settings = settings;
        _itemSets = new ItemSetService(_lcu);
        _gameSession = new GameSessionService(_lcu);

        WindowPlacement.Restore(this, settings);

        PopulateFilters();
        ApplySettingsToControls();
        UpdateIdleChips();

        AutoAcceptToggle.Checked += (_, _) => OnSettingChanged();
        AutoAcceptToggle.Unchecked += (_, _) => OnSettingChanged();
        AutoApplyToggle.Checked += (_, _) => OnSettingChanged();
        AutoApplyToggle.Unchecked += (_, _) => OnSettingChanged();
        ItemSetToggle.Checked += (_, _) => OnSettingChanged();
        ItemSetToggle.Unchecked += (_, _) => OnItemSetsDisabled();

        // Best effort on quit, so closing mid-game doesn't leave an item set behind.
        // Run off the UI thread and time-box it: awaiting here would deadlock on Wait().
        Closing += (_, _) =>
        {
            WindowPlacement.Capture(this, _settings);
            SaveSettings();
            try { Task.Run(() => _itemSets.ClearAsync()).Wait(TimeSpan.FromSeconds(2)); }
            catch { }
        };

        _pollTimer.Elapsed += async (_, _) => await PollLeagueClient();
        _pollTimer.Start();
    }

    private void PopulateFilters()
    {
        // "Auto" is first so the default keeps the previous behaviour.
        LaneCombo.Items.Add(new ComboBoxItem { Content = "Auto", Tag = null });
        foreach (var lane in new[] { Lane.Top, Lane.Jungle, Lane.Mid, Lane.Bottom, Lane.Support })
            LaneCombo.Items.Add(new ComboBoxItem { Content = StatsCatalog.LaneLabel(lane), Tag = lane });

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

        LaneCombo.SelectedIndex = 0;   // Auto
        RankCombo.SelectedIndex = 2;   // Diamond+
        RegionCombo.SelectedIndex = 0; // World
    }

    /// <summary>Restores the saved toggles and filters, then arms change tracking.</summary>
    private void ApplySettingsToControls()
    {
        AutoAcceptToggle.IsChecked = _settings.AutoAccept;
        AutoApplyToggle.IsChecked = _settings.AutoApplyRunes;
        ItemSetToggle.IsChecked = _settings.ImportItemSets;

        SelectByTag(LaneCombo, _settings.Lane);
        SelectByTag(RankCombo, _settings.Rank);
        SelectByTag(RegionCombo, _settings.Region);

        // Only now, so restoring selections doesn't trigger a build reload.
        _filtersReady = true;
    }

    private static void SelectByTag(ComboBox combo, object? tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (Equals(item.Tag, tag))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>Captures the current control state and writes it to disk.</summary>
    private void SaveSettings()
    {
        _settings.AutoAccept = AutoAcceptToggle.IsChecked ?? false;
        _settings.AutoApplyRunes = AutoApplyToggle.IsChecked ?? false;
        _settings.ImportItemSets = ItemSetToggle.IsChecked ?? false;
        _settings.Lane = SelectedLaneOverride;
        _settings.Rank = SelectedRank;
        _settings.Region = SelectedRegion;
        _settings.Save();
    }

    private void OnSettingChanged()
    {
        UpdateIdleChips();
        SaveSettings();
    }

    private StatsRank SelectedRank =>
        (RankCombo.SelectedItem as ComboBoxItem)?.Tag is StatsRank r ? r : StatsRank.DiamondPlus;

    private StatsRegion SelectedRegion =>
        (RegionCombo.SelectedItem as ComboBoxItem)?.Tag is StatsRegion r ? r : StatsRegion.World;

    /// <summary>The lane the user forced, or null when the dropdown is on "Auto".</summary>
    private Lane? SelectedLaneOverride =>
        (LaneCombo.SelectedItem as ComboBoxItem)?.Tag is Lane l ? l : null;

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
                    Dispatcher.Invoke(() => SetStatus("Searching for League Client...", false));
                    return;
                }
            }

            Dispatcher.Invoke(() => SetStatus("Connected to League Client", true));

            await HandleAutoAcceptAsync();
            await HandleGameflowAsync();
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

    /// <summary>
    /// Watches the game phase so an imported item set is removed once the game is over,
    /// rather than piling up in the player's shop.
    /// </summary>
    private async Task HandleGameflowAsync()
    {
        string? phase = await _lcu.GetAsync("lol-gameflow/v1/gameflow-phase");
        if (string.IsNullOrEmpty(phase)) return;

        phase = phase.Trim('"');

        if (phase == "InProgress")
        {
            _gameWasRunning = true;
            _inGame = true;
            await ShowLiveGameAsync();
            return;
        }

        if (_inGame)
        {
            _inGame = false;
            _shownGameId = 0;
            Dispatcher.Invoke(ClearDisplay);
        }

        if (!_gameWasRunning) return;

        // The game just ended: take back whatever we added to the shop.
        _gameWasRunning = false;
        await _itemSets.ClearAsync();
        Dispatcher.Invoke(() => SettingsStatus.Text = "Item set removed after the game.");
    }

    /// <summary>
    /// Loads the live game once per game and renders it as a loading-screen style scoreboard.
    /// </summary>
    private async Task ShowLiveGameAsync()
    {
        var game = await _gameSession.GetLiveGameAsync();
        if (game == null) return;

        // The roster doesn't change mid-game, so only build it once.
        if (game.GameId != 0 && game.GameId == _shownGameId) return;

        var localPuuid = await GetLocalPuuidAsync();
        foreach (var p in game.TeamOne.Concat(game.TeamTwo))
            p.IsLocalPlayer = localPuuid.Length > 0 && p.Puuid == localPuuid;

        // Put the local player's team on top, like the loading screen does.
        if (game.TeamTwo.Any(p => p.IsLocalPlayer))
            (game.TeamOne, game.TeamTwo) = (game.TeamTwo, game.TeamOne);

        await IconCache.PreloadAsync(
            game.TeamOne.Concat(game.TeamTwo)
                .SelectMany(p => LivePlayerViewModel.IconPathsFor(_lcu, p)));

        _shownGameId = game.GameId;

        Dispatcher.Invoke(() => RenderLiveGame(game));
    }

    private async Task<string> GetLocalPuuidAsync()
    {
        string? json = await _lcu.GetAsync("lol-summoner/v1/current-summoner");
        if (string.IsNullOrEmpty(json)) return "";

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("puuid", out var p) ? p.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    private void RenderLiveGame(LiveGame game)
    {
        HeaderLabel.Text = "STATUS";
        ChampNameText.Text = "Currently in game";
        RoleBadge.Text = string.IsNullOrEmpty(game.QueueName) ? "LIVE" : game.QueueName.ToUpperInvariant();
        PatchBadge.Text = "";
        SourceLine.Text = "Builds pause until the next champion select.";
        SourceLine.Foreground = (Brush)FindResource("TextLo");

        TeamOneList.ItemsSource = game.TeamOne.Select(p => new LivePlayerViewModel(_lcu, p)).ToList();
        TeamTwoList.ItemsSource = game.TeamTwo.Select(p => new LivePlayerViewModel(_lcu, p)).ToList();

        var everyone = game.TeamOne.Concat(game.TeamTwo).ToList();
        var notes = new List<string>();

        if (everyone.Count > 0 && !everyone.Any(p => p.RankText.Length > 0))
            notes.Add("The client only reports ranked stats for some players, so ranks may be blank.");

        if (everyone.Any(p => p.ChampionId > 0) && !everyone.Any(p => p.MasteryText.Length > 0))
            notes.Add($"No champion mastery came back; the endpoints tried are listed in {MasteryService.DiagnosticPath}");

        InGameNote.Text = string.Join("  ", notes);

        if (!game.HasPlayers)
        {
            InGameNote.Text = "Could not read the player list from the client. " +
                              $"The raw data was saved to {GameSessionService.DiagnosticPath}";
        }

        PagesList.ItemsSource = null;
        _pageViewModels = new List<RunePageViewModel>();
        _selected = null;

        InGamePanel.Visibility = Visibility.Visible;
        IdlePanel.Visibility = Visibility.Collapsed;
        ImportBtn.Visibility = Visibility.Collapsed;
        HintText.Text = "";
    }

    private async Task HandleChampSelectAsync()
    {
        // While a game is running the scoreboard owns the view; champ select is over.
        if (_inGame) return;

        string? session = await _lcu.GetAsync("lol-champ-select/v1/session");

        if (string.IsNullOrEmpty(session))
        {
            if (_lastChampionId != 0 || _inChampSelect)
            {
                _lastChampionId = 0;
                _lastAssignedLane = null;
                _inChampSelect = false;
                Dispatcher.Invoke(ClearDisplay);
            }
            return;
        }

        _inChampSelect = true;

        var (championId, isLocked, assignedLane) = ParseChampSelect(session);
        if (championId <= 0) return;

        // Refetch when the champion changes, or when the assigned role resolves later.
        if (championId == _lastChampionId && assignedLane == _lastAssignedLane)
        {
            Dispatcher.Invoke(() => UpdateChampionHeader(championId, isLocked));
            return;
        }

        _lastChampionId = championId;
        _lastAssignedLane = assignedLane;

        await LoadBuildAsync(championId, isLocked, assignedLane);
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

    /// <summary>
    /// Resolves which lane to build for. A manual override always wins; otherwise champ
    /// select's assigned position is used, and failing that op.gg picks the most-played lane.
    /// </summary>
    private (Lane? Lane, LaneSource Source) ResolveLane(Lane? assignedLane)
    {
        var manual = Dispatcher.Invoke(() => SelectedLaneOverride);
        if (manual.HasValue) return (manual, LaneSource.Manual);
        if (assignedLane.HasValue) return (assignedLane, LaneSource.Assigned);
        return (null, LaneSource.Detected);
    }

    private async Task LoadBuildAsync(int championId, bool isLocked, Lane? assignedLane)
    {
        string name = _lcu.ChampionData.TryGetValue(championId, out var d) ? d.Name : $"Champion #{championId}";
        string alias = _lcu.ChampionData.TryGetValue(championId, out var d2) ? d2.Key : name;

        Dispatcher.Invoke(() =>
        {
            UpdateChampionHeader(championId, isLocked);
            SetStatus($"Loading {name} builds...", true);
        });

        var rank = Dispatcher.Invoke(() => SelectedRank);
        var region = Dispatcher.Invoke(() => SelectedRegion);
        var (lane, laneSource) = ResolveLane(assignedLane);

        var data = await _builds.GetBuildAsync(_lcu, championId, name, alias, lane, laneSource, rank, region);

        // The user may have hovered a different champion while this was in flight.
        if (championId != _lastChampionId) return;

        // Fetch icons before building the cards so they render complete.
        await IconCache.PreloadAsync(data.Pages.SelectMany(p => RunePageViewModel.IconPathsFor(_lcu, p)));

        if (championId != _lastChampionId) return;

        _current = data;

        Dispatcher.Invoke(() =>
        {
            RenderBuild(data, isLocked);
            SetStatus("Connected to League Client", true);
        });

        bool autoApply = Dispatcher.Invoke(() => AutoApplyToggle.IsChecked ?? false);
        if (autoApply && _pageViewModels.Count > 0)
            await ApplyPageAsync(_pageViewModels[0]);
    }

    // ---- Rendering ----

    private void SetStatus(string text, bool connected)
    {
        StatusText.Text = text;
        ConnDot.Fill = connected
            ? (Brush)FindResource("Mint")
            : (Brush)FindResource("TextLo");
    }

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

        string laneNote = data.LaneSource switch
        {
            LaneSource.Manual => "role set manually",
            LaneSource.Assigned => "role from champ select",
            _ => "role guessed from play rate — set it above if wrong"
        };

        string filters = $"{StatsCatalog.RankLabel(data.Rank)} · {StatsCatalog.RegionLabel(data.Region)}";
        SourceLine.Text = string.IsNullOrEmpty(data.StatusLine)
            ? $"{filters} · {laneNote}"
            : $"{filters} · {data.StatusLine} · {laneNote}";

        // Warn when the role was a guess, since builds differ wildly by role.
        SourceLine.Foreground = data.LaneSource == LaneSource.Detected
            ? (Brush)FindResource("Amber")
            : (Brush)FindResource("TextLo");

        _pageViewModels = data.Pages
            .Select((p, i) => new RunePageViewModel(_lcu, p, i))
            .ToList();

        PagesList.ItemsSource = _pageViewModels;
        _selected = _pageViewModels.FirstOrDefault();

        bool hasPages = _pageViewModels.Count > 0;
        HeaderLabel.Text = "DETECTED CHAMPION";
        InGamePanel.Visibility = Visibility.Collapsed;
        IdlePanel.Visibility = hasPages ? Visibility.Collapsed : Visibility.Visible;
        ImportBtn.Visibility = hasPages ? Visibility.Visible : Visibility.Collapsed;

        if (!hasPages)
        {
            IdleTitle.Text = $"No data for {data.ChampionName}";
            IdleSubtitle.Text = "Try a different role, rank or region.";
        }

        HintText.Text = hasPages ? "Click a card to apply that rune page" : "";
    }

    private void ClearDisplay()
    {
        _current = null;
        _pageViewModels = new List<RunePageViewModel>();
        _selected = null;

        PagesList.ItemsSource = null;
        TeamOneList.ItemsSource = null;
        TeamTwoList.ItemsSource = null;
        ChampNameText.Text = "None (hover a champion)";
        RoleBadge.Text = "—";
        PatchBadge.Text = "";
        SourceLine.Text = "";
        SourceLine.Foreground = (Brush)FindResource("TextLo");

        HeaderLabel.Text = "DETECTED CHAMPION";
        InGamePanel.Visibility = Visibility.Collapsed;
        IdlePanel.Visibility = Visibility.Visible;
        IdleTitle.Text = "Waiting for champion select";
        IdleSubtitle.Text = "Hover or lock a champion and your builds appear here.";
        ImportBtn.Visibility = Visibility.Collapsed;
        HintText.Text = "";

        UpdateIdleChips();
    }

    /// <summary>Shows which options are on, so the idle screen says something useful.</summary>
    private void UpdateIdleChips()
    {
        var chips = new List<string>();
        if (AutoAcceptToggle.IsChecked == true) chips.Add("Auto-accept queue");
        if (AutoApplyToggle.IsChecked == true) chips.Add("Auto-apply runes");
        if (ItemSetToggle.IsChecked == true) chips.Add("Item set import");
        if (chips.Count == 0) chips.Add("All automation off");

        IdleChips.ItemsSource = chips;
    }

    private async void OnItemSetsDisabled()
    {
        OnSettingChanged();
        await _itemSets.ClearAsync();
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
                SetStatus($"Applied: {vm.Label} — {vm.KeystoneName}", true);
            });

            await MaybeImportItemSetAsync(vm);
        }
        else
        {
            Dispatcher.Invoke(() => SetStatus("Could not write rune page (all pages may be in use)", true));
        }

        return ok;
    }

    private async Task MaybeImportItemSetAsync(RunePageViewModel vm)
    {
        bool enabled = Dispatcher.Invoke(() => ItemSetToggle.IsChecked ?? false);
        if (!enabled || _current == null) return;

        bool ok = await _itemSets.ApplyAsync(_lcu, _current, vm.Page);
        Dispatcher.Invoke(() => SettingsStatus.Text = ok
            ? $"Item set imported for {_current.ChampionName}."
            : "Could not import the item set.");
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

                string pageName = n.GetString() ?? "";
                if (pageName != ManagedPageName && pageName != LegacyPageName) continue;
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

    private void SettingsBtn_Click(object sender, RoutedEventArgs e) =>
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;

    private async void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filtersReady) return;

        SaveSettings();

        if (_current == null) return;
        await LoadBuildAsync(_current.ChampionId, true, _lastAssignedLane);
    }
}
