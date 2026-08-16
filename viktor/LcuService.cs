using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace viktor;

public class LcuService
{
    private HttpClient? _httpClient;
    public bool IsConnected => _httpClient != null;

    public Dictionary<int, (string Name, string Key)> ChampionData { get; } = new();
    public Dictionary<int, string> PerkNames { get; } = new();
    public Dictionary<int, string> StyleNames { get; } = new();
    public Dictionary<int, string> ItemNames { get; } = new();

    /// <summary>Rune id -> the tree it belongs to. Lets us derive a page's styles from its runes.</summary>
    public Dictionary<int, int> PerkStyleOf { get; } = new();

    /// <summary>Rune id -> its slot row within its tree (0 = keystone). Used to order a page correctly.</summary>
    public Dictionary<int, int> PerkSlotOf { get; } = new();

    public Dictionary<int, string> SpellNames { get; } = new();

    // Icon paths as the game data reports them, e.g. "/lol-game-data/assets/ASSETS/Items/...png".
    public Dictionary<int, string> PerkIcons { get; } = new();
    public Dictionary<int, string> StyleIcons { get; } = new();
    public Dictionary<int, string> ItemIcons { get; } = new();
    public Dictionary<int, string> SpellIcons { get; } = new();

    public async Task<bool> TryConnectAsync()
    {
        var process = Process.GetProcessesByName("LeagueClientUx").FirstOrDefault();
        if (process?.MainModule?.FileName == null) return false;

        string? dir = Path.GetDirectoryName(process.MainModule.FileName);
        string lockfilePath = Path.Combine(dir ?? "", "lockfile");

        if (!File.Exists(lockfilePath)) return false;

        try
        {
            using var stream = File.Open(lockfilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string content = reader.ReadToEnd();

            var parts = content.Split(':');
            if (parts.Length < 5) return false;

            string port = parts[2];
            string password = parts[3];

            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri($"https://127.0.0.1:{port}/")
            };

            string auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{password}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);

            await LoadGameDataAsync();
            return true;
        }
        catch
        {
            _httpClient = null;
            return false;
        }
    }

    private async Task LoadGameDataAsync()
    {
        try
        {
            // 1. Champions
            string? champJson = await GetAsync("lol-game-data/assets/v1/champion-summary.json");
            if (!string.IsNullOrEmpty(champJson))
            {
                using var doc = JsonDocument.Parse(champJson);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    int id = item.GetProperty("id").GetInt32();
                    if (id <= 0) continue;
                    ChampionData[id] = (item.GetProperty("name").GetString() ?? "Unknown", item.GetProperty("alias").GetString() ?? "");
                }
            }

            // 2. Perks / Runes
            string? perksJson = await GetAsync("lol-game-data/assets/v1/perks.json");
            if (!string.IsNullOrEmpty(perksJson))
            {
                using var doc = JsonDocument.Parse(perksJson);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out int id) || id <= 0)
                        continue;

                    PerkNames[id] = item.GetProperty("name").GetString() ?? $"Rune #{id}";
                    if (item.TryGetProperty("iconPath", out var icon))
                        PerkIcons[id] = icon.GetString() ?? "";
                }
            }

            // 3. Perk Styles
            string? stylesJson = await GetAsync("lol-game-data/assets/v1/perkstyles.json");
            if (!string.IsNullOrEmpty(stylesJson))
            {
                using var doc = JsonDocument.Parse(stylesJson);
                var stylesArray = doc.RootElement.TryGetProperty("styles", out var s) ? s : doc.RootElement;
                if (stylesArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in stylesArray.EnumerateArray())
                    {
                        int styleId = item.GetProperty("id").GetInt32();
                        StyleNames[styleId] = item.GetProperty("name").GetString() ?? "Style";
                        if (item.TryGetProperty("iconPath", out var styleIcon))
                            StyleIcons[styleId] = styleIcon.GetString() ?? "";

                        // Record which tree each rune lives in and which row it sits on, so a
                        // page coming from an external source can be ordered the way the LCU
                        // expects (keystone, then one rune per row).
                        if (!item.TryGetProperty("slots", out var slots) || slots.ValueKind != JsonValueKind.Array)
                            continue;

                        int slotIndex = 0;
                        foreach (var slot in slots.EnumerateArray())
                        {
                            string slotType = slot.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                            if (slotType == "kStatMod") continue;   // shards are ordered by the source
                            if (!slot.TryGetProperty("perks", out var perks) || perks.ValueKind != JsonValueKind.Array)
                                continue;

                            foreach (var perk in perks.EnumerateArray())
                            {
                                int perkId = perk.GetInt32();
                                PerkStyleOf[perkId] = styleId;
                                PerkSlotOf[perkId] = slotIndex;
                            }
                            slotIndex++;
                        }
                    }
                }
            }

            // 4. Items (For mapping IDs like 3089 -> Rabadon's Deathcap)
            string? itemsJson = await GetAsync("lol-game-data/assets/v1/items.json");
            if (!string.IsNullOrEmpty(itemsJson))
            {
                using var doc = JsonDocument.Parse(itemsJson);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out int id) || id <= 0)
                        continue;

                    ItemNames[id] = item.GetProperty("name").GetString() ?? $"Item #{id}";
                    if (item.TryGetProperty("iconPath", out var icon))
                        ItemIcons[id] = icon.GetString() ?? "";
                }
            }

            // 5. Summoner spells
            string? spellsJson = await GetAsync("lol-game-data/assets/v1/summoner-spells.json");
            if (!string.IsNullOrEmpty(spellsJson))
            {
                using var doc = JsonDocument.Parse(spellsJson);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    // Some entries carry a placeholder id of 4294967295 ("Primal Smite"),
                    // which overflows Int32 — skip them rather than aborting the whole load.
                    if (!item.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out int id) || id <= 0)
                        continue;

                    SpellNames[id] = item.GetProperty("name").GetString() ?? $"Spell #{id}";
                    if (item.TryGetProperty("iconPath", out var icon))
                        SpellIcons[id] = icon.GetString() ?? "";
                }
            }
        }
        catch { }
    }

    public async Task<string?> GetAsync(string endpoint)
    {
        if (_httpClient == null) return null;
        try
        {
            var response = await _httpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync();
        }
        catch
        {
            _httpClient = null;
            return null;
        }
    }

    public async Task<bool> PostAsync(string endpoint, string jsonBody)
    {
        if (_httpClient == null) return false;
        try
        {
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            var res = await _httpClient.PostAsync(endpoint, content);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            _httpClient = null;
            return false;
        }
    }

    public async Task<bool> PutAsync(string endpoint, string jsonBody)
    {
        if (_httpClient == null) return false;
        try
        {
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            var res = await _httpClient.PutAsync(endpoint, content);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            _httpClient = null;
            return false;
        }
    }

    public async Task DeleteAsync(string endpoint)
    {
        if (_httpClient == null) return;
        try
        {
            await _httpClient.DeleteAsync(endpoint);
        }
        catch { _httpClient = null; }
    }
}