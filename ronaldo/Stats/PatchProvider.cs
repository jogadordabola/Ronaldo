using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ronaldo.Stats;

/// <summary>
/// Resolves the live patch number, which Lolalytics requires on every request.
/// Cached for a few hours since it only changes on patch day.
/// </summary>
public class PatchProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _fetchedAt = DateTime.MinValue;
    private string _patch = "";

    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    /// <summary>Returns the current patch as "16.16", or an empty string if it can't be resolved.</summary>
    public async Task<string> GetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_patch.Length > 0 && DateTime.UtcNow - _fetchedAt < CacheFor) return _patch;

            string? json = await StatsHttp.GetStringAsync(
                "https://ddragon.leagueoflegends.com/api/versions.json", ct);
            if (string.IsNullOrEmpty(json)) return _patch;

            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var v in doc.RootElement.EnumerateArray())
                {
                    var parts = (v.GetString() ?? "").Split('.');
                    if (parts.Length < 2) continue;

                    _patch = parts[0] + "." + parts[1];
                    _fetchedAt = DateTime.UtcNow;
                    break;
                }
            }
            catch { }

            return _patch;
        }
        finally
        {
            _gate.Release();
        }
    }
}
