using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace viktor.Stats;

/// <summary>
/// Writes the shown build into the League client as an in-game item set, and removes it again
/// once the game is over so the shop doesn't fill up with leftovers.
///
/// Every set this app creates is titled with <see cref="TitlePrefix"/>; cleanup only ever
/// touches sets carrying that prefix, so the player's own item sets are left alone.
/// </summary>
public class ItemSetService
{
    public const string TitlePrefix = "Viktor · ";

    private readonly LcuService _lcu;
    private long _summonerId;

    public ItemSetService(LcuService lcu) => _lcu = lcu;

    private async Task<long> GetSummonerIdAsync()
    {
        if (_summonerId > 0) return _summonerId;

        string? json = await _lcu.GetAsync("lol-summoner/v1/current-summoner");
        if (string.IsNullOrEmpty(json)) return 0;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("summonerId", out var id) &&
                id.ValueKind == JsonValueKind.Number)
                _summonerId = id.GetInt64();
        }
        catch { }

        return _summonerId;
    }

    /// <summary>Replaces this app's item set with one built from the given page.</summary>
    public async Task<bool> ApplyAsync(LcuService lcu, ChampionBuildData data, RunePage page)
    {
        long summonerId = await GetSummonerIdAsync();
        if (summonerId <= 0 || page.Items == null) return false;

        var payload = await LoadSetsAsync(summonerId);
        if (payload == null) return false;

        var sets = payload["itemSets"] as JsonArray ?? new JsonArray();

        // Drop any set we previously wrote before adding the new one.
        RemoveOurSets(sets);

        sets.Insert(0, BuildSet(lcu, data, page));
        payload["itemSets"] = sets;

        return await _lcu.PutAsync($"lol-item-sets/v1/item-sets/{summonerId}/sets", payload.ToJsonString());
    }

    /// <summary>Removes every set this app created. Called once the game ends.</summary>
    public async Task<bool> ClearAsync()
    {
        long summonerId = await GetSummonerIdAsync();
        if (summonerId <= 0) return false;

        var payload = await LoadSetsAsync(summonerId);
        if (payload?["itemSets"] is not JsonArray sets) return false;

        if (RemoveOurSets(sets) == 0) return true;   // nothing of ours to clean up

        payload["itemSets"] = sets;
        return await _lcu.PutAsync($"lol-item-sets/v1/item-sets/{summonerId}/sets", payload.ToJsonString());
    }

    private async Task<JsonObject?> LoadSetsAsync(long summonerId)
    {
        string? json = await _lcu.GetAsync($"lol-item-sets/v1/item-sets/{summonerId}/sets");
        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static int RemoveOurSets(JsonArray sets)
    {
        var ours = sets
            .Select((node, index) => (node, index))
            .Where(x => (x.node?["title"]?.GetValue<string>() ?? "").StartsWith(TitlePrefix, StringComparison.Ordinal))
            .Select(x => x.index)
            .OrderByDescending(i => i)
            .ToList();

        foreach (int i in ours) sets.RemoveAt(i);
        return ours.Count;
    }

    private static JsonObject BuildSet(LcuService lcu, ChampionBuildData data, RunePage page)
    {
        var items = page.Items!;
        var blocks = new JsonArray();

        AddBlock(blocks, "Starting Items", items.StarterIds);
        AddBlock(blocks, "Core Build", items.CoreIds);
        AddBlock(blocks, "Boots", items.BootsId > 0 ? new List<int> { items.BootsId } : new List<int>());
        AddBlock(blocks, "Situational", items.SituationalIds);

        string keystone = lcu.PerkNames.TryGetValue(page.KeystoneId, out var k) ? k : "Build";

        return new JsonObject
        {
            ["associatedChampions"] = new JsonArray(data.ChampionId),
            ["associatedMaps"] = new JsonArray(11, 12),
            ["blocks"] = blocks,
            ["map"] = "any",
            ["mode"] = "any",
            ["preferredItemSlots"] = new JsonArray(),
            ["sortrank"] = 0,
            ["startedFrom"] = "blank",
            ["title"] = $"{TitlePrefix}{data.ChampionName} {StatsCatalog.LaneLabel(data.Lane)} · {keystone}",
            ["type"] = "custom",
            ["uid"] = Guid.NewGuid().ToString()
        };
    }

    private static void AddBlock(JsonArray blocks, string title, IReadOnlyCollection<int> itemIds)
    {
        if (itemIds.Count == 0) return;

        var items = new JsonArray();
        foreach (int id in itemIds.Where(i => i > 0).Distinct())
        {
            items.Add(new JsonObject
            {
                ["id"] = id.ToString(),
                ["count"] = 1
            });
        }

        if (items.Count == 0) return;

        blocks.Add(new JsonObject
        {
            ["type"] = title,
            ["items"] = items
        });
    }
}
