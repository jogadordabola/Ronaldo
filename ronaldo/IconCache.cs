using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ronaldo.Stats;

namespace ronaldo;

/// <summary>
/// Resolves game asset icons (items, runes, trees, summoner spells) to images.
///
/// The game data reports icons as client paths like
/// "/lol-game-data/assets/ASSETS/Items/Icons2D/3020_Class_T2_SorcerersShoes.png".
/// Community Dragon mirrors that tree, so the path maps onto a public URL by stripping the
/// prefix and lowercasing the rest. Downloads are cached in memory and on disk, so a champion
/// only pays the network cost the first time it is ever shown.
/// </summary>
public static class IconCache
{
    private const string BaseUrl = "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/";
    private const string PathPrefix = "/lol-game-data/assets/";

    private static readonly ConcurrentDictionary<string, ImageSource?> Memory = new();
    private static readonly SemaphoreSlimWrapper Throttle = new(8);

    private static readonly string DiskDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ronaldo", "icons");

    /// <summary>Returns an already-loaded icon, or null if it hasn't been fetched yet.</summary>
    public static ImageSource? Get(string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath)) return null;
        return Memory.TryGetValue(Normalize(iconPath), out var img) ? img : null;
    }

    /// <summary>
    /// Fetches every icon that isn't cached yet. Awaited before building the view models so
    /// the cards render complete rather than popping images in one by one.
    /// </summary>
    public static async Task PreloadAsync(IEnumerable<string?> iconPaths)
    {
        var pending = iconPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Normalize(p!))
            .Distinct()
            .Where(key => !Memory.ContainsKey(key))
            .ToList();

        if (pending.Count == 0) return;

        await Task.WhenAll(pending.Select(LoadAsync));
    }

    private static async Task LoadAsync(string key)
    {
        await Throttle.WaitAsync();
        try
        {
            byte[]? bytes = ReadFromDisk(key);

            if (bytes == null)
            {
                string url = BaseUrl + key;
                try
                {
                    using var res = await StatsHttp.Client.GetAsync(url);
                    if (res.IsSuccessStatusCode)
                    {
                        bytes = await res.Content.ReadAsByteArrayAsync();
                        WriteToDisk(key, bytes);
                    }
                }
                catch { }
            }

            Memory[key] = bytes == null ? null : Decode(bytes);
        }
        finally
        {
            Throttle.Release();
        }
    }

    /// <summary>
    /// Decodes off the UI thread and freezes the result, which is what makes it safe to hand
    /// a background-loaded image straight to a binding.
    /// </summary>
    private static ImageSource? Decode(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            using (var stream = new MemoryStream(bytes))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static string Normalize(string iconPath)
    {
        string p = iconPath.Trim();
        if (p.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase))
            p = p[PathPrefix.Length..];
        return p.TrimStart('/').ToLowerInvariant();
    }

    private static string DiskPath(string key)
    {
        // Hash the key so nested asset paths become a flat, filesystem-safe cache.
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string name = Convert.ToHexString(hash)[..24];
        return Path.Combine(DiskDir, name + Path.GetExtension(key));
    }

    private static byte[]? ReadFromDisk(string key)
    {
        try
        {
            string path = DiskPath(key);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch { return null; }
    }

    private static void WriteToDisk(string key, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(DiskDir);
            File.WriteAllBytes(DiskPath(key), bytes);
        }
        catch { }
    }

    /// <summary>Minimal async gate so a champion swap doesn't open dozens of sockets at once.</summary>
    private sealed class SemaphoreSlimWrapper
    {
        private readonly System.Threading.SemaphoreSlim _inner;
        public SemaphoreSlimWrapper(int count) => _inner = new System.Threading.SemaphoreSlim(count, count);
        public Task WaitAsync() => _inner.WaitAsync();
        public void Release() => _inner.Release();
    }
}
