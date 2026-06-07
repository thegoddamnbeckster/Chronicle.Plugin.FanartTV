using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Chronicle.Plugin.FanartTV;

/// <summary>
/// Thin HTTP wrapper around the Fanart.tv v3.2 REST API.
/// Base URL: <c>https://webservice.fanart.tv/v3.2/{type}/{id}?api_key={key}</c>
///
/// Authentication: every request requires <c>api_key</c> (project key from fanart.tv account).
/// An optional <c>client_key</c> (personal API key) unlocks images submitted within the last 7 days
/// that would otherwise require VIP membership to see immediately.
///
/// Rate limiting: Fanart.tv imposes no published hard limit for reasonable personal use, but we add
/// a small jitter between requests and respect 429 / Retry-After headers.
/// </summary>
internal sealed class FanartTvClient
{
    private const string BaseUrl = "https://webservice.fanart.tv/v3.2";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string? _clientKey;
    private readonly ILogger _logger;

    /// <param name="http">Owned HttpClient — caller controls lifetime.</param>
    /// <param name="apiKey">Fanart.tv project API key (required).</param>
    /// <param name="clientKey">Fanart.tv personal API key (optional, unlocks recent images).</param>
    /// <param name="logger">Logger for HTTP diagnostics.</param>
    public FanartTvClient(HttpClient http, string apiKey, string? clientKey, ILogger logger)
    {
        _http      = http;
        _apiKey    = apiKey;
        _clientKey = clientKey;
        _logger    = logger;
    }

    // ── Movie ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches all artwork for a movie by its TMDB ID.
    /// Returns null when the movie is not found on Fanart.tv (404).
    /// </summary>
    public Task<FanartMovieResponse?> GetMovieAsync(string tmdbId, CancellationToken ct = default)
        => GetAsync<FanartMovieResponse>($"{BaseUrl}/movies/{Uri.EscapeDataString(tmdbId)}", ct);

    // ── TV ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches all artwork for a TV series by its TVDB ID.
    /// Returns null when the series is not found on Fanart.tv (404).
    /// </summary>
    public Task<FanartTvResponse?> GetTvShowAsync(string tvdbId, CancellationToken ct = default)
        => GetAsync<FanartTvResponse>($"{BaseUrl}/tv/{Uri.EscapeDataString(tvdbId)}", ct);

    // ── Music ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches all artwork for a music artist by MusicBrainz artist MBID.
    /// The response includes per-album art nested under <c>Albums</c>, keyed by release-group MBID.
    /// Returns null when the artist is not found on Fanart.tv (404).
    /// </summary>
    public Task<FanartArtistResponse?> GetArtistAsync(string artistMbid, CancellationToken ct = default)
        => GetAsync<FanartArtistResponse>($"{BaseUrl}/music/{Uri.EscapeDataString(artistMbid)}", ct);

    // ── Health ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pings Fanart.tv with a well-known movie (Fight Club, TMDB 550) to confirm the API key works.
    /// </summary>
    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await GetMovieAsync("550", ct).ConfigureAwait(false);
            return result is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fanart.tv health check failed");
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct) where T : class
    {
        var fullUrl = AppendKeys(url);
        _logger.LogDebug("Fanart.tv GET {Url}", url); // log without keys for security

        using var response = await _http.GetAsync(fullUrl, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogDebug("Fanart.tv 404 for {Url}", url);
            return null;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Forbidden)
        {
            _logger.LogError("Fanart.tv rejected the API key (HTTP {Status}) for {Url}",
                (int)response.StatusCode, url);
            throw new InvalidOperationException(
                "Fanart.tv rejected the API key. Check your api_key in plugin settings.");
        }

        if (response.StatusCode == (HttpStatusCode)429)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
            _logger.LogWarning("Fanart.tv rate limit hit (429) for {Url}. Retry-After: {Delay}s",
                url, retryAfter.TotalSeconds);
            // Respect Retry-After, then propagate as transient failure so the
            // enrichment service marks the row as Failed and retries at next scheduled task.
            await Task.Delay(retryAfter, ct).ConfigureAwait(false);
            throw new HttpRequestException("Fanart.tv rate limit hit (429). Retry later.");
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Fanart.tv returned unexpected HTTP {Status} for {Url}",
                (int)response.StatusCode, url);
            response.EnsureSuccessStatusCode(); // throws HttpRequestException with status details
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, ct).ConfigureAwait(false);
    }

    private string AppendKeys(string url)
    {
        var sep = url.Contains('?') ? '&' : '?';
        var result = $"{url}{sep}api_key={Uri.EscapeDataString(_apiKey)}";
        if (!string.IsNullOrWhiteSpace(_clientKey))
            result += $"&client_key={Uri.EscapeDataString(_clientKey)}";
        return result;
    }
}
