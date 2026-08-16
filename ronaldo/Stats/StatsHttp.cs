using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ronaldo.Stats;

/// <summary>
/// Shared outbound HTTP client for the stats sources. Both u.gg's CDN and Lolalytics
/// reject requests without a browser User-Agent, so one is set globally here.
/// </summary>
public static class StatsHttp
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/131.0.0.0 Safari/537.36";

    public static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        return client;
    }

    /// <summary>GETs a URL, returning null on any failure rather than throwing.</summary>
    public static async Task<string?> GetStringAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var res = await Client.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }
}
