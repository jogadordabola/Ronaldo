using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ronaldo.Stats;

/// <summary>
/// Records LP gained or lost per game.
///
/// The League client does not report a historical LP change for past matches — its own
/// end-of-game screen only knows it because it watched the game finish. So this snapshots
/// ranked LP when a game starts, compares it once the game ends, and stores the delta
/// against that game id. That means LP shows only for games played while this app was
/// running; older matches simply show nothing rather than a guess.
/// </summary>
public class LpTracker
{
    private const string SoloQueue = "RANKED_SOLO_5x5";
    private const string FlexQueue = "RANKED_FLEX_SR";

    private readonly LcuService _lcu;
    private Dictionary<long, int> _deltas = new();
    private bool _loaded;

    /// <summary>LP per queue captured when the current game started.</summary>
    private Dictionary<string, int>? _pending;
    private long _pendingGameId;

    public LpTracker(LcuService lcu) => _lcu = lcu;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ronaldo", "lp-history.json");

    public int? GetDelta(long gameId)
    {
        Load();
        return _deltas.TryGetValue(gameId, out int d) ? d : null;
    }

    /// <summary>Called while a game is running, to remember the LP it started from.</summary>
    public async Task NoteGameStartedAsync(long gameId)
    {
        if (gameId <= 0 || _pendingGameId == gameId) return;

        var snapshot = await ReadLpAsync();
        if (snapshot == null) return;

        _pendingGameId = gameId;
        _pending = snapshot;
    }

    /// <summary>
    /// Called once the game is over. Ranked stats take a moment to settle after the game
    /// ends, so the caller should allow for that before invoking this.
    /// </summary>
    public async Task NoteGameEndedAsync()
    {
        if (_pending == null || _pendingGameId <= 0) return;

        long gameId = _pendingGameId;
        var before = _pending;

        _pending = null;
        _pendingGameId = 0;

        var after = await ReadLpAsync();
        if (after == null) return;

        // Whichever ranked queue actually moved is the one that game belonged to.
        foreach (var queue in new[] { SoloQueue, FlexQueue })
        {
            if (!before.TryGetValue(queue, out int b) || !after.TryGetValue(queue, out int a)) continue;
            if (a == b) continue;

            int delta = a - b;

            // A promotion or demotion resets LP, so the raw difference is meaningless.
            // Clamp to a believable single-game swing instead of storing nonsense.
            if (Math.Abs(delta) > 100) delta = delta > 0 ? 100 : -100;

            Load();
            _deltas[gameId] = delta;
            Save();
            return;
        }
    }

    private async Task<Dictionary<string, int>?> ReadLpAsync()
    {
        string? json = await _lcu.GetAsync("lol-ranked/v1/current-ranked-stats");
        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("queueMap", out var queues)) return null;

            var result = new Dictionary<string, int>();
            foreach (var queue in new[] { SoloQueue, FlexQueue })
            {
                if (!queues.TryGetProperty(queue, out var q)) continue;
                if (q.TryGetProperty("leaguePoints", out var lp) && lp.ValueKind == JsonValueKind.Number)
                    result[queue] = lp.GetInt32();
            }

            return result.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (!File.Exists(FilePath)) return;
            _deltas = JsonSerializer.Deserialize<Dictionary<long, int>>(File.ReadAllText(FilePath))
                      ?? new Dictionary<long, int>();
        }
        catch
        {
            _deltas = new Dictionary<long, int>();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Keep the file small; only recent games are ever displayed.
            var trimmed = _deltas.OrderByDescending(kv => kv.Key).Take(200)
                                 .ToDictionary(kv => kv.Key, kv => kv.Value);
            _deltas = trimmed;

            File.WriteAllText(FilePath, JsonSerializer.Serialize(trimmed));
        }
        catch { }
    }
}
