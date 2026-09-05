using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronicle.Plugin.FanartTV;

/// <summary>
/// Chronicle metadata provider for Fanart.tv.
///
/// Fanart.tv is an artwork-only provider — it has no text-search endpoint.
/// Rather than doing a full metadata search (title → candidates → best match),
/// this plugin resolves artwork by cross-referencing external IDs already stored
/// for the item by other providers (TMDB, TVDB, MusicBrainz).
///
/// Resolution logic in <see cref="SearchAsync"/>:
///   movies / fanedits  → KnownExternalIds["tmdb"] (format "movie:{id}")  → /v3.2/movies/{tmdbId}
///   tv / anime         → KnownExternalIds["tvdb"] (raw TVDB numeric ID)  → /v3.2/tv/{tvdbId}
///                        (seasons fall back to KnownExternalIds["parent_tvdb"])
///   music (level 0)    → KnownExternalIds["musicbrainz"] (artist MBID)   → /v3.2/music/{artistMbid}
///   music (level 1)    → KnownExternalIds["parent_musicbrainz"] (artist) +
///                         KnownExternalIds["musicbrainz"] (release-group)
///
/// External ID format stored by this plugin:
///   "movie:{tmdbId}"                           for movies and fan edits
///   "tv:{tvdbId}"                              for TV shows and anime (TVDB IDs, NOT TMDB)
///   "artist:{artistMbid}"                      for music artists
///   "album:{artistMbid}/{releaseGroupMbid}"    for music albums
///
/// TV items only receive artwork if a TVDB ID is available in media_external_ids
/// (populated by Trakt/SIMKL sync). Items enriched only by TMDB will be NotFound
/// until a TVDB ID is stored (via Trakt sync or manual Fix Match).
///
/// If none of the required cross-reference IDs are available, SearchAsync returns empty
/// and the enrichment service marks the row as NotFound for this provider.
/// </summary>
public sealed class FanartTvMetadataProvider : IMetadataProvider, IDisposable
{
    // ── IMetadataProvider identity ────────────────────────────────────────────

    public string PluginId => "chronicle.plugin.fanarttv";
    public string Name     => "Fanart.tv";
    public string Version  => "1.0.0";
    public string Author   => "Chronicle Contributors";

    // ── Settings keys ─────────────────────────────────────────────────────────

    private const string KeyApiKey    = "api_key";
    private const string KeyClientKey = "client_key";
    private const string KeyLanguage  = "language";

    // ── Live configuration ────────────────────────────────────────────────────

    private FanartTvClient? _client;
    private HttpClient? _ownedHttpClient;     // disposed when reconfigured or when provider is disposed
    private string _preferredLanguage = "en";
    private readonly ILogger<FanartTvMetadataProvider> _logger;

    // Short-lived result cache: avoids a redundant second HTTP call when the enrichment
    // service calls GetByIdAsync immediately after SearchAsync has already fetched the
    // same Fanart.tv ID in the same enrichment pipeline run.
    // Single volatile field provides an atomic snapshot — all three values are read
    // or written together, preventing torn reads under concurrent access.
    private static readonly long CacheTtlTicks = 30L * TimeSpan.TicksPerSecond;
    private sealed record CacheEntry(string Id, MediaMetadata Result, long ExpiresAtTicks);
    private volatile CacheEntry? _cache;

    /// <summary>Required for public instantiation by the host (no-arg).</summary>
    public FanartTvMetadataProvider()
        : this(NullLogger<FanartTvMetadataProvider>.Instance) { }

    /// <summary>Constructor for DI or test injection.</summary>
    public FanartTvMetadataProvider(ILogger<FanartTvMetadataProvider> logger)
    {
        _logger = logger;
    }

    /// <summary>Test-only constructor that injects a pre-built client.</summary>
    internal FanartTvMetadataProvider(FanartTvClient client, string language = "en")
        : this(NullLogger<FanartTvMetadataProvider>.Instance)
    {
        _client            = client;
        _preferredLanguage = language;
    }

    // ── IMetadataProvider: static declarations ────────────────────────────────

    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        new MediaTypeSupport
        {
            MediaTypeName   = "movies",
            DisplayName     = "Movies",
            HierarchyLevels = 1,
            DefaultPriority = 20,  // supplements TMDB; lower priority so TMDB wins title/overview
            SupportedFields = ["poster_url", "backdrop_url", "logo_url", "banner_url",
                               "disc_url", "clearart_url", "thumb_url"],
        },
        new MediaTypeSupport
        {
            MediaTypeName   = "fanedits",
            DisplayName     = "Fan Edits",
            HierarchyLevels = 1,
            DefaultPriority = 20,
            SupportedFields = ["poster_url", "backdrop_url", "logo_url", "banner_url",
                               "disc_url", "clearart_url", "thumb_url"],
        },
        new MediaTypeSupport
        {
            MediaTypeName    = "tv",
            DisplayName      = "TV",
            HierarchyLevels  = 3,
            HierarchyLabels  = ["Show", "Season", "Episode"],
            DefaultPriority  = 20,
            SupportedFields  = ["poster_url", "backdrop_url", "logo_url", "banner_url",
                                "clearart_url", "thumb_url", "character_art_url"],
            LevelFields = new Dictionary<int, List<string>>
            {
                [1] = ["poster_url", "backdrop_url", "banner_url", "thumb_url"],
            },
        },
        // Standalone anime films — flat like "movies", not hierarchical like "anime" (real anime
        // TV series). See Chronicle.Plugin.TMDB's anime_movies declaration for the full rationale.
        new MediaTypeSupport
        {
            MediaTypeName   = "anime_movies",
            DisplayName     = "Anime Movies",
            HierarchyLevels = 1,
            DefaultPriority = 20,  // supplements TMDB; lower priority so TMDB wins title/overview
            SupportedFields = ["poster_url", "backdrop_url", "logo_url", "banner_url",
                               "disc_url", "clearart_url", "thumb_url"],
        },
        new MediaTypeSupport
        {
            MediaTypeName    = "anime",
            DisplayName      = "Anime",
            HierarchyLevels  = 3,
            HierarchyLabels  = ["Show", "Season", "Episode"],
            DefaultPriority  = 20,
            SupportedFields  = ["poster_url", "backdrop_url", "logo_url", "banner_url",
                                "clearart_url", "thumb_url", "character_art_url"],
            LevelFields = new Dictionary<int, List<string>>
            {
                [1] = ["poster_url", "backdrop_url", "banner_url", "thumb_url"],
            },
        },
        new MediaTypeSupport
        {
            MediaTypeName    = "music",
            DisplayName      = "Music",
            HierarchyLevels  = 2,   // Fanart.tv has no per-track artwork — Artist (0) + Album (1) only
            HierarchyLabels  = ["Artist", "Album"],
            DefaultPriority  = 20,
            SupportedFields  = ["poster_url", "backdrop_url", "logo_url", "banner_url",
                                "clearart_url", "thumb_url"],
            LevelFields = new Dictionary<int, List<string>>
            {
                [1] = ["poster_url", "disc_url"],   // album cover + CD art
            },
        },
    ];

    // Fanart.tv has no text-search endpoint. It resolves via cross-references:
    //   TV / anime  → TVDB ID ("tvdb:N") — NOT the TMDB tv: prefix, those are different IDs
    //   Movies      → TMDB movie ID ("movie:N")
    public IReadOnlyList<string> GetAcceptedCrossRefPrefixes() =>
        ["tvdb:", "movie:"];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key         = KeyApiKey,
                Label       = "Fanart.tv API Key",
                Description = "Your project API key from https://fanart.tv/get-an-api-key/",
                Type        = SettingType.Password,
                Required    = true,
            },
            new SettingDefinition
            {
                Key         = KeyClientKey,
                Label       = "Personal API Key (optional)",
                Description = "Your personal API key — unlocks images submitted in the last 7 days " +
                              "before they become publicly visible. Leave blank if you don't have one.",
                Type        = SettingType.Password,
                Required    = false,
            },
            new SettingDefinition
            {
                Key          = KeyLanguage,
                Label        = "Preferred Language",
                Description  = "ISO 639-1 language code (e.g. en, de, fr). Images in this language " +
                               "are ranked above English when available.",
                Type         = SettingType.Text,
                Required     = false,
                DefaultValue = "en",
            },
        ],
    };

    // ── IMetadataProvider: configuration ─────────────────────────────────────

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        settings.TryGetValue(KeyApiKey,    out var apiKey);
        settings.TryGetValue(KeyClientKey, out var clientKey);
        settings.TryGetValue(KeyLanguage,  out var language);

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Fanart.tv plugin requires 'api_key' to be configured.");

        // Dispose the previous HttpClient before creating a new one to avoid socket exhaustion
        // when Configure() is called more than once (e.g. user saves new settings).
        _ownedHttpClient?.Dispose();

        var http = new HttpClient
        {
            DefaultRequestHeaders = { { "User-Agent", "Chronicle/1.0" } }
        };
        _ownedHttpClient   = http;
        _client            = new FanartTvClient(http, apiKey, clientKey, _logger);
        _preferredLanguage = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim();
        _logger.LogInformation("Fanart.tv plugin configured (preferred language: {Language})",
            _preferredLanguage);
    }

    // ── IMetadataProvider: search ─────────────────────────────────────────────

    // External ID patterns this plugin produces / consumes
    private static readonly Regex _movieIdRe    = new(@"^movie:(\d+)$",                                       RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _tvIdRe       = new(@"^tv:(\d+)$",                                          RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _tvSeasonIdRe = new(@"^tv:(\d+)/season:(\d+)$",                             RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _artistIdRe   = new(@"^artist:([0-9a-f-]{36})$",                            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Album-with-no-release-group fallback (see ResolveExternalId's own comment) -- same
    // artist mbid as _artistIdRe, but with a per-album name suffix so this album's own
    // stored id never collides with its parent artist's or its siblings'. Only the mbid is
    // captured; the name half is decorative and never read back out.
    private static readonly Regex _artistFallbackAlbumIdRe = new(@"^artist:([0-9a-f-]{36})/noRelease:",       RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _albumIdRe    = new(@"^album:([0-9a-f-]{36})/([0-9a-f-]{36})$",             RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Fanart.tv URL patterns for Fix Match normalization
    private static readonly Regex _fanartMovieUrlRe  = new(@"fanart\.tv/movie/(\d+)",   RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _fanartSeriesUrlRe = new(@"fanart\.tv/series/(\d+)",  RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Album URL must be checked before artist URL (more specific path comes first)
    private static readonly Regex _fanartAlbumUrlRe  = new(@"fanart\.tv/(?:music|artist)/([0-9a-f-]{36})/album/([0-9a-f-]{36})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _fanartMusicUrlRe  = new(@"fanart\.tv/(?:music|artist)/([0-9a-f-]{36})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Fanart.tv has no text-search endpoint, so this method resolves by cross-referencing
    /// IDs already stored by other providers (TMDB, TVDB, MusicBrainz) via
    /// <see cref="MediaSearchContext.KnownExternalIds"/>.
    ///
    /// If the required cross-reference ID is unavailable, returns an empty list.
    /// The enrichment service will then mark the row as NotFound for this provider.
    /// </summary>
    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
        MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();

        var resolvedId = ResolveExternalId(context);
        if (resolvedId is null)
        {
            _logger.LogDebug(
                "Fanart.tv: no cross-reference ID available for item '{Name}' " +
                "(type={Type}, level={Level}). Skipping.",
                context.Name, context.MediaTypeName, context.HierarchyLevel);
            return [];
        }

        _logger.LogDebug("Fanart.tv: resolved external ID '{Id}' for item '{Name}'",
            resolvedId, context.Name);

        // Fetch immediately — Fanart.tv has no search endpoint, so we already know
        // the exact lookup ID. Cache the result so the enrichment service's subsequent
        // GetByIdAsync call for the same ID does not make a redundant second HTTP request.
        //
        // Pass seasonNumber only for season items (level 1). For episodes (level 2) we
        // have no season number in context — ItemNumber is the episode number — so pass
        // null and let the fetch fall back to series-wide artwork.
        var seasonNumber = context.HierarchyLevel == 1 ? context.ItemNumber : null;
        var metadata = await FetchByResolvedIdAsync(resolvedId, seasonNumber, ct)
                            .ConfigureAwait(false);
        if (metadata is null)
        {
            _logger.LogDebug("Fanart.tv: ID '{Id}' not found for item '{Name}'",
                resolvedId, context.Name);
            return [];
        }

        StoreInCache(resolvedId, metadata);
        return [new ScoredCandidate(metadata, 100, "cross-reference ID match")];
    }

    /// <summary>
    /// Fetches artwork by a previously-stored Fanart.tv external ID.
    /// Supports "movie:{id}", "tv:{tvdbId}", "artist:{mbid}", "album:{artistMbid}/{rgMbid}" formats.
    /// Also accepts Fanart.tv web URLs for Fix Match convenience:
    ///   https://fanart.tv/movie/550/    → movie:550
    ///   https://fanart.tv/series/76290/ → tv:76290
    ///   https://fanart.tv/music/{mbid}/ → artist:{mbid}
    /// </summary>
    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();
        externalId = NormalizeFanartUrl(externalId);

        // Cross-ref form: "tvdb:344643" → Fanart.tv native form "tv:344643"
        if (externalId.StartsWith("tvdb:", StringComparison.OrdinalIgnoreCase))
            externalId = "tv:" + externalId[5..];

        // Return cached result from a preceding SearchAsync call for the same ID
        // without making a second HTTP request.
        var cached = TryGetFromCache(externalId);
        if (cached is not null)
        {
            _logger.LogDebug("Fanart.tv: returning cached result for ID '{Id}'", externalId);
            return cached;
        }

        var metadata = await FetchByResolvedIdAsync(externalId, seasonNumber: null, ct)
                            .ConfigureAwait(false);

        if (metadata is null)
        {
            // Fanart.tv returned 404 for a previously-stored ID. Log and return an empty
            // result (the enrichment service will mark the row Completed with no artwork).
            // This can happen when an item is removed from Fanart.tv. A future resync will
            // update the record when / if it reappears.
            _logger.LogWarning(
                "Fanart.tv: ID '{Id}' returned 404. " +
                "Storing empty result — run Re-sync All Artwork if the item is added to Fanart.tv.",
                externalId);
            return new MediaMetadata { ExternalId = externalId };
        }

        _logger.LogDebug("Fanart.tv: fetched artwork for ID '{Id}'", externalId);
        return metadata;
    }

    /// <summary>
    /// Fanart.tv does not provide image downloads — all images are direct CDN URLs
    /// that the host fetches itself. Calling this method is a logic error.
    /// </summary>
    public Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
        => throw new NotSupportedException(
            "Fanart.tv provides direct CDN image URLs; GetImageAsync is not required. " +
            "Use the URL from PosterUrl / BackdropUrl / LogoUrl etc. directly.");

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_client is null)
        {
            _logger.LogWarning("Fanart.tv health check skipped — plugin not configured");
            return false;
        }
        return await _client.HealthCheckAsync(ct).ConfigureAwait(false);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _ownedHttpClient?.Dispose();
        _ownedHttpClient = null;
        _client          = null;
    }

    // ── ID resolution ─────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a <see cref="MediaSearchContext"/> to a Fanart.tv lookup ID using the
    /// cross-reference IDs stored by other providers.
    ///
    /// Priority per media type:
    ///   movies / fanedits → KnownExternalIds["tmdb"] value, strip "movie:" prefix → "movie:{id}"
    ///   tv / anime        → KnownExternalIds["tvdb"] (own or parent_tvdb for seasons) → "tv:{tvdbId}"
    ///   music (level 0)   → KnownExternalIds["musicbrainz"] (artist MBID) → "artist:{mbid}"
    ///   music (level 1)   → KnownExternalIds["musicbrainz"] (release-group) +
    ///                        KnownExternalIds["parent_musicbrainz"] (artist MBID)
    ///                        → "album:{artistMbid}/{rgMbid}"
    ///   music (level 2+)  → null (tracks have no Fanart.tv artwork)
    /// </summary>
    private static string? ResolveExternalId(MediaSearchContext context)
    {
        var known = context.KnownExternalIds;
        if (known is null || known.Count == 0) return null;

        var mediaType = context.MediaTypeName?.ToLowerInvariant();

        if (mediaType is "movies" or "movie" or "fanedits")
        {
            // TMDB stores "movie:{id}" — extract the numeric part
            if (known.TryGetValue("tmdb", out var tmdbId))
            {
                var numericId = ExtractNumericId(tmdbId, "movie:");
                if (numericId is not null) return $"movie:{numericId}";
            }
        }
        else if (mediaType is "tv" or "anime")
        {
            // TVDB stores a raw numeric ID. For seasons/episodes, the TVDB ID is on the
            // parent show, not the child — check both the item's own ID and the parent's.
            // TVDB IDs may be stored under "tvdb" (raw legacy), "thetvdb" (via
            // PluginIdHelper.ToSource("chronicle.plugin.thetvdb")), or arrive from the cascade
            // as "tvdb:344643" or "series:344643". Extract just the numeric part.
            var rawTvdb = GetFirstValue(known, "tvdb", "thetvdb");
            if (!string.IsNullOrWhiteSpace(rawTvdb))
            {
                var tvdbNum = ExtractNumericId(rawTvdb, "tvdb:", "series:");
                if (tvdbNum is not null) return $"tv:{tvdbNum}";
            }
            var rawParentTvdb = GetFirstValue(known, "parent_tvdb", "parent_thetvdb");
            if (!string.IsNullOrWhiteSpace(rawParentTvdb))
            {
                var parentNum = ExtractNumericId(rawParentTvdb, "tvdb:", "series:");
                if (parentNum is not null) return $"tv:{parentNum}";
            }
        }
        else if (mediaType is "music")
        {
            // Explicit type guard — do not fall into music logic for unknown types.
            if (context.HierarchyLevel == 0)
            {
                // Root level = artist. MusicBrainz stores "artist:{mbid}" or a bare UUID.
                // Reject "release-group:{mbid}" — that ID would 404 on the artist endpoint.
                if (known.TryGetValue("musicbrainz", out var mbId))
                {
                    var colon = mbId.IndexOf(':');
                    if (colon >= 0 && !mbId.StartsWith("artist:", StringComparison.OrdinalIgnoreCase))
                        return null; // wrong entity type for artist-level lookup
                    var mbid = ExtractMbid(mbId);
                    if (mbid is not null) return $"artist:{mbid}";
                }
            }
            else if (context.HierarchyLevel == 1)
            {
                // Album level. We need:
                //   - the artist's MBID (to call /v3.2/music/{artistMbid})
                //   - the release-group MBID (to select the album in the response)
                // The album item itself stores a release-group MBID in its own external IDs.
                // The parent artist's MBID is injected as "parent_musicbrainz" by the
                // enrichment service from the parent's media_external_ids.
                known.TryGetValue("musicbrainz", out var albumMbId);
                known.TryGetValue("parent_musicbrainz", out var artistMbId);

                // Only accept "release-group:{mbid}" or bare UUID for the album slot —
                // reject "artist:{mbid}" which the MusicBrainz plugin can store on album items
                // when it matched at the artist level rather than the release-group level.
                string? albumMbid = null;
                if (albumMbId is not null)
                {
                    var colon = albumMbId.IndexOf(':');
                    if (colon < 0 || albumMbId.StartsWith("release-group:", StringComparison.OrdinalIgnoreCase))
                        albumMbid = ExtractMbid(albumMbId);
                    // else: wrong prefix type (e.g. "artist:") — treat as not available
                }

                var artistMbid = artistMbId is not null ? ExtractMbid(artistMbId) : null;

                if (artistMbid is not null && albumMbid is not null)
                    return $"album:{artistMbid}/{albumMbid}";

                // If we only have the artist MBID (no release-group), fall back to
                // artist-level artwork which at least gives the artist backdrop/logo.
                //
                // Bug (confirmed live, 2026-08-03): this used to return the bare
                // "artist:{mbid}" -- byte-identical to the PARENT artist item's own stored
                // fanarttv external id. Persisting that as this album's own identity made
                // Chronicle's core enrichment see one external id "owned" by two different
                // items, which silently merged ~19 Limp Bizkit albums into their own parent
                // artist item. The artist mbid alone is never enough to keep this id unique
                // per album (every sibling album missing a release-group id would resolve to
                // the exact same string), so the album's own name is folded in too --
                // FetchByResolvedIdAsync's _artistFallbackAlbumIdRe below only needs the mbid
                // back out to fetch the artist endpoint; the name suffix exists purely to
                // keep this album's stored id distinct from its parent's and its siblings'.
                if (artistMbid is not null)
                    return $"artist:{artistMbid}/noRelease:{Uri.EscapeDataString(context.Name)}";
            }
            // Tracks (level 2+) have no Fanart.tv artwork — skip.
        }

        return null;
    }

    /// <summary>Strips a known prefix and returns the remainder, or null if it doesn't match.</summary>
    private static string? ExtractNumericId(string rawId, string prefix)
    {
        if (rawId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var part = rawId[prefix.Length..];
            return string.IsNullOrWhiteSpace(part) ? null : part;
        }
        // Plain numeric ID with no prefix
        if (rawId.Length > 0 && rawId.All(char.IsDigit))
            return rawId;
        return null;
    }

    // Accepts multiple possible prefixes (e.g. "tvdb:" and "series:") and strips the first match.
    private static string? ExtractNumericId(string rawId, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            var result = ExtractNumericId(rawId, prefix);
            if (result is not null) return result;
        }
        return null;
    }

    // Returns the first non-empty value found for any of the given keys.
    private static string? GetFirstValue(IReadOnlyDictionary<string, string> dict, params string[] keys)
    {
        foreach (var key in keys)
            if (dict.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
                return val;
        return null;
    }

    /// <summary>Extracts a MusicBrainz UUID from "artist:{mbid}", "release-group:{mbid}", or a bare UUID.</summary>
    private static string? ExtractMbid(string rawId)
    {
        var colonIdx = rawId.IndexOf(':');   // use IndexOf — prefix is always "word:", UUID never contains ':'
        if (colonIdx >= 0)
        {
            var after = rawId[(colonIdx + 1)..].Trim();
            return after.Length == 36 ? after : null;   // MBID is always 36-char UUID
        }
        return rawId.Length == 36 ? rawId : null;
    }

    // ── Fanart.tv fetch ───────────────────────────────────────────────────────

    private async Task<MediaMetadata?> FetchByResolvedIdAsync(
        string resolvedId, int? seasonNumber, CancellationToken ct)
    {
        var movieMatch    = _movieIdRe.Match(resolvedId);
        var tvSeasonMatch = _tvSeasonIdRe.Match(resolvedId);
        var tvMatch       = _tvIdRe.Match(resolvedId);
        var artistMatch   = _artistIdRe.Match(resolvedId);
        var artistFallbackAlbumMatch = _artistFallbackAlbumIdRe.Match(resolvedId);
        var albumMatch    = _albumIdRe.Match(resolvedId);

        if (movieMatch.Success)
            return await FetchMovieAsync(movieMatch.Groups[1].Value, resolvedId, ct).ConfigureAwait(false);

        // Compound season ID (tv:{tvdbId}/season:{N}) takes precedence over bare show ID
        if (tvSeasonMatch.Success)
            return await FetchTvAsync(
                tvSeasonMatch.Groups[1].Value,
                resolvedId,
                int.Parse(tvSeasonMatch.Groups[2].Value),
                ct).ConfigureAwait(false);

        if (tvMatch.Success)
            return await FetchTvAsync(tvMatch.Groups[1].Value, resolvedId, seasonNumber, ct).ConfigureAwait(false);

        if (albumMatch.Success)
            return await FetchAlbumAsync(
                albumMatch.Groups[1].Value,   // artistMbid
                albumMatch.Groups[2].Value,   // releaseGroupMbid
                resolvedId, ct).ConfigureAwait(false);

        // Checked before the bare artist match: a compound "artist:{mbid}/noRelease:{name}"
        // string does NOT satisfy _artistIdRe's own end-of-string anchor, so there's no
        // ordering ambiguity between the two -- this just has to run first since it's the
        // more specific pattern conceptually (same fetch, different stored-id shape).
        if (artistFallbackAlbumMatch.Success)
            return await FetchArtistAsync(artistFallbackAlbumMatch.Groups[1].Value, resolvedId, ct).ConfigureAwait(false);

        if (artistMatch.Success)
            return await FetchArtistAsync(artistMatch.Groups[1].Value, resolvedId, ct).ConfigureAwait(false);

        _logger.LogWarning("Fanart.tv: unrecognised external ID format '{Id}'", resolvedId);
        return null;
    }

    private async Task<MediaMetadata?> FetchMovieAsync(
        string tmdbId, string externalId, CancellationToken ct)
    {
        var response = await _client!.GetMovieAsync(tmdbId, ct).ConfigureAwait(false);
        if (response is null) return null;

        var poster   = BestImage(response.MoviePosters);
        var backdrop = BestImage(response.MovieBackgrounds);
        var logo     = BestImage(response.HdMovieLogos) ?? BestImage(response.MovieLogos);
        var banner   = BestImage(response.MovieBanners);
        var disc     = BestImage(response.MovieDiscImages);
        var clearart = BestImage(response.HdMovieClearArts) ?? BestImage(response.MovieArts);
        var thumb    = BestImage(response.MovieThumbs);

        _logger.LogDebug(
            "Fanart.tv movie {TmdbId}: poster={Poster}, backdrop={Backdrop}, logo={Logo}",
            tmdbId,
            poster?.Url is not null ? "yes" : "no",
            backdrop?.Url is not null ? "yes" : "no",
            logo?.Url is not null ? "yes" : "no");

        // Lossless ingestion (see Chronicle/CLAUDE.md): BestImage() above picks ONE winner per
        // type for the first-class fields Kodi actively displays, but Fanart.tv routinely has
        // many more candidates per type than that -- discarding them was a real data-loss bug,
        // not a simplification. Every candidate this response actually has, for every type,
        // is preserved in AdditionalImages (tagged with the same art-type strings
        // ScraperController.ArtworkFieldMap already uses) so nothing the provider returned is
        // silently thrown away, and Kodi's own "Choose Art" picker has every real alternate to
        // offer -- not just the one this plugin happened to rank first.
        var additionalImages = new List<AdditionalImage>();
        additionalImages.AddRange(AllImages(response.MoviePosters, "poster"));
        additionalImages.AddRange(AllImages(response.MovieBackgrounds, "fanart"));
        additionalImages.AddRange(AllImages(response.HdMovieLogos, "clearlogo"));
        additionalImages.AddRange(AllImages(response.MovieLogos, "clearlogo"));
        additionalImages.AddRange(AllImages(response.MovieBanners, "banner"));
        additionalImages.AddRange(AllImages(response.MovieDiscImages, "discart"));
        additionalImages.AddRange(AllImages(response.HdMovieClearArts, "clearart"));
        additionalImages.AddRange(AllImages(response.MovieArts, "clearart"));
        additionalImages.AddRange(AllImages(response.MovieThumbs, "thumb"));

        return new MediaMetadata
        {
            ExternalId  = externalId,
            Source      = "fanarttv",
            Title       = response.Name ?? string.Empty,
            PosterUrl   = poster?.Url,
            BackdropUrl = backdrop?.Url,
            LogoUrl     = logo?.Url,
            BannerUrl   = banner?.Url,
            DiscUrl     = disc?.Url,
            ClearartUrl = clearart?.Url,
            ThumbUrl    = thumb?.Url,
            AdditionalImages = additionalImages,
        };
    }

    private async Task<MediaMetadata?> FetchTvAsync(
        string tvdbId, string externalId, int? seasonNumber, CancellationToken ct)
    {
        var response = await _client!.GetTvShowAsync(tvdbId, ct).ConfigureAwait(false);
        if (response is null) return null;

        var poster   = BestImage(response.TvPosters);
        var backdrop = BestSeasonBackground(response.ShowBackgrounds, seasonNumber);
        var logo     = BestImage(response.HdTvLogos) ?? BestImage(response.ClearLogos);
        var banner   = BestImage(response.TvBanners);
        var clearart = BestImage(response.HdClearArts) ?? BestImage(response.ClearArts);
        var thumb    = BestImage(response.TvThumbs);
        var charArt  = BestImage(response.CharacterArts);

        // Season-scoped images for child items
        var seasonPoster = seasonNumber.HasValue
            ? BestSeasonImage(response.SeasonPosters, seasonNumber.Value)
            : null;
        var seasonThumb = seasonNumber.HasValue
            ? BestSeasonImage(response.SeasonThumbs, seasonNumber.Value)
            : null;

        _logger.LogDebug(
            "Fanart.tv TV {TvdbId} (season={Season}): poster={Poster}, logo={Logo}",
            tvdbId, seasonNumber?.ToString() ?? "show",
            (seasonPoster ?? poster)?.Url is not null ? "yes" : "no",
            logo?.Url is not null ? "yes" : "no");

        // Embed season number in ExternalId so re-enrichment and Fix Match can recover season artwork.
        // IsIdValidForLevel accepts "tv:{N}/season:{N}" (has '/') for child items.
        var storedExternalId = seasonNumber.HasValue
            ? $"{externalId}/season:{seasonNumber}"
            : externalId;

        // Lossless ingestion (see Chronicle/CLAUDE.md) -- see FetchMovieAsync's identical comment.
        var additionalImages = new List<AdditionalImage>();
        additionalImages.AddRange(AllImages(response.TvPosters, "poster"));
        additionalImages.AddRange(AllImages(response.ShowBackgrounds, "fanart"));
        additionalImages.AddRange(AllImages(response.HdTvLogos, "clearlogo"));
        additionalImages.AddRange(AllImages(response.ClearLogos, "clearlogo"));
        additionalImages.AddRange(AllImages(response.TvBanners, "banner"));
        additionalImages.AddRange(AllImages(response.HdClearArts, "clearart"));
        additionalImages.AddRange(AllImages(response.ClearArts, "clearart"));
        additionalImages.AddRange(AllImages(response.TvThumbs, "thumb"));
        additionalImages.AddRange(AllImages(response.CharacterArts, "characterart"));
        additionalImages.AddRange(AllImages(response.SeasonPosters, "poster"));
        additionalImages.AddRange(AllImages(response.SeasonThumbs, "thumb"));

        return new MediaMetadata
        {
            ExternalId      = storedExternalId,
            Source          = "fanarttv",
            Title           = response.Name ?? string.Empty,
            PosterUrl       = seasonPoster?.Url ?? poster?.Url,
            BackdropUrl     = backdrop?.Url,
            LogoUrl         = logo?.Url,
            BannerUrl       = banner?.Url,
            ClearartUrl     = clearart?.Url,
            ThumbUrl        = seasonThumb?.Url ?? thumb?.Url,
            CharacterArtUrl = charArt?.Url,
            AdditionalImages = additionalImages,
        };
    }

    private async Task<MediaMetadata?> FetchArtistAsync(
        string artistMbid, string externalId, CancellationToken ct)
    {
        var response = await _client!.GetArtistAsync(artistMbid, ct).ConfigureAwait(false);
        if (response is null) return null;

        var thumb    = BestImage(response.ArtistThumbs);
        var backdrop = BestImage(response.ArtistBackgrounds);
        var logo     = BestImage(response.HdMusicLogos) ?? BestImage(response.MusicLogos);
        var banner   = BestImage(response.MusicBanners);
        var clearart = BestImage(response.HdMusicArts) ?? BestImage(response.MusicArts);

        _logger.LogDebug(
            "Fanart.tv artist {ArtistMbid}: thumb={Thumb}, logo={Logo}, albums={AlbumCount}",
            artistMbid,
            thumb?.Url is not null ? "yes" : "no",
            logo?.Url is not null ? "yes" : "no",
            response.Albums?.Count ?? 0);

        // Lossless ingestion (see Chronicle/CLAUDE.md) -- see FetchMovieAsync's identical comment.
        var additionalImages = new List<AdditionalImage>();
        additionalImages.AddRange(AllImages(response.ArtistThumbs, "poster"));
        additionalImages.AddRange(AllImages(response.ArtistBackgrounds, "fanart"));
        additionalImages.AddRange(AllImages(response.HdMusicLogos, "clearlogo"));
        additionalImages.AddRange(AllImages(response.MusicLogos, "clearlogo"));
        additionalImages.AddRange(AllImages(response.MusicBanners, "banner"));
        additionalImages.AddRange(AllImages(response.HdMusicArts, "clearart"));
        additionalImages.AddRange(AllImages(response.MusicArts, "clearart"));

        // artistthumb is landscape (~500x281) but it's the only artist photo Fanart.tv provides.
        // Put it in PosterUrl so it flows through the normal metadata assignment pipeline
        // and appears in the poster slot on the media detail page (same as TMDB posters for movies/TV).
        return new MediaMetadata
        {
            ExternalId  = externalId,
            Source      = "fanarttv",
            Title       = response.Name ?? string.Empty,
            PosterUrl   = thumb?.Url,
            BackdropUrl = backdrop?.Url,
            LogoUrl     = logo?.Url,
            BannerUrl   = banner?.Url,
            ClearartUrl = clearart?.Url,
            AdditionalImages = additionalImages,
        };
    }

    /// <summary>
    /// Fetches artwork for a specific album (release-group) from the artist endpoint.
    /// The Fanart.tv API embeds per-album art inside the artist response keyed by release-group MBID,
    /// so we always call the artist endpoint and extract the matching album entry.
    /// </summary>
    private async Task<MediaMetadata?> FetchAlbumAsync(
        string artistMbid, string releaseGroupMbid, string externalId, CancellationToken ct)
    {
        var response = await _client!.GetArtistAsync(artistMbid, ct).ConfigureAwait(false);
        if (response is null) return null;

        FanartAlbum? album = null;
        response.Albums?.TryGetValue(releaseGroupMbid, out album);

        var cover = BestImage(album?.AlbumCovers);
        var cdart = BestImage(album?.CdArts);

        _logger.LogDebug(
            "Fanart.tv album {RgMbid} (artist {ArtistMbid}): cover={Cover}, cdart={Cdart}",
            releaseGroupMbid, artistMbid,
            cover?.Url is not null ? "yes" : "no",
            cdart?.Url is not null ? "yes" : "no");

        // Lossless ingestion (see Chronicle/CLAUDE.md) -- see FetchMovieAsync's identical comment.
        var additionalImages = new List<AdditionalImage>();
        additionalImages.AddRange(AllImages(album?.AlbumCovers, "poster"));
        additionalImages.AddRange(AllImages(album?.CdArts, "discart"));

        // If this specific album has no art on Fanart.tv yet, still return a result so the
        // enrichment row is marked Completed rather than left Pending indefinitely.
        // A scheduled Re-sync All Artwork task will pick up new community-submitted images.
        return new MediaMetadata
        {
            ExternalId = externalId,
            Source     = "fanarttv",
            Title      = string.Empty,    // title not available from the album art response
            PosterUrl  = cover?.Url,
            DiscUrl    = cdart?.Url,
            AdditionalImages = additionalImages,
        };
    }

    // ── Result cache helpers ──────────────────────────────────────────────────

    private void StoreInCache(string id, MediaMetadata result)
    {
        _cache = new CacheEntry(id, result, DateTime.UtcNow.Ticks + CacheTtlTicks);
    }

    private MediaMetadata? TryGetFromCache(string id)
    {
        var entry = _cache;   // single volatile read — snapshot
        if (entry is not null
            && entry.Id == id
            && DateTime.UtcNow.Ticks < entry.ExpiresAtTicks)
        {
            return entry.Result;
        }
        return null;
    }

    // ── URL normalisation ─────────────────────────────────────────────────────

    /// <summary>
    /// Converts a Fanart.tv web URL into the plugin's internal ID format.
    /// Passes through anything that is already in internal format.
    /// </summary>
    private static string NormalizeFanartUrl(string input)
    {
        if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return input;

        var movieMatch = _fanartMovieUrlRe.Match(input);
        if (movieMatch.Success) return $"movie:{movieMatch.Groups[1].Value}";

        var seriesMatch = _fanartSeriesUrlRe.Match(input);
        if (seriesMatch.Success) return $"tv:{seriesMatch.Groups[1].Value}";

        // Album URL must be checked before artist URL (it's a more-specific path)
        var albumMatch = _fanartAlbumUrlRe.Match(input);
        if (albumMatch.Success) return $"album:{albumMatch.Groups[1].Value}/{albumMatch.Groups[2].Value}";

        var musicMatch = _fanartMusicUrlRe.Match(input);
        if (musicMatch.Success) return $"artist:{musicMatch.Groups[1].Value}";

        return input; // unrecognised URL — pass through, will fail gracefully in FetchByResolvedIdAsync
    }

    // ── Image selection helpers ───────────────────────────────────────────────

    /// <summary>
    /// Selects the best image from a list, preferring the configured language,
    /// then English, then language-neutral ("00"), then any.
    /// Within each tier, images are ranked by likes (descending).
    /// "00" in the lang field means language-neutral in the Fanart.tv API.
    /// </summary>
    private FanartImage? BestImage(IReadOnlyList<FanartImage>? images)
    {
        if (images is null or { Count: 0 }) return null;

        return images
            .OrderByDescending(i => LanguageScore(i.Language))
            .ThenByDescending(i => ParseInt(i.Likes))
            .FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Url));
    }

    /// <summary>
    /// Every image in the list with a non-empty URL, converted to AdditionalImage and tagged
    /// with artType (one of ScraperController.ArtworkFieldMap's strings: "poster", "fanart",
    /// "clearlogo", "banner", "clearart", "discart", "characterart", or "thumb" for the one
    /// slot that map doesn't cover). Ranked best-first (same ordering as BestImage) so a
    /// downstream consumer that only wants the top few still gets them in the right order.
    /// Deliberately includes the same image BestImage() already picked as the primary field --
    /// downstream dedup (ScraperController.CollectArtwork's `seen` set) collapses the repeat,
    /// and excluding it here would require this method to duplicate BestImage()'s own ranking
    /// logic just to skip one entry.
    /// </summary>
    private List<AdditionalImage> AllImages(IReadOnlyList<FanartImage>? images, string artType)
    {
        if (images is null or { Count: 0 }) return [];

        return images
            .Where(i => !string.IsNullOrWhiteSpace(i.Url))
            .OrderByDescending(i => LanguageScore(i.Language))
            .ThenByDescending(i => ParseInt(i.Likes))
            .Select(i => new AdditionalImage { Url = i.Url!, Type = artType })
            .ToList();
    }

    /// <summary>
    /// Selects the best backdrop for a given season, falling back to "all" season backdrops
    /// and then series-wide backdrops.
    /// </summary>
    private FanartImage? BestSeasonBackground(
        IReadOnlyList<FanartImage>? images, int? seasonNumber)
    {
        if (images is null or { Count: 0 }) return null;

        var seasonStr = seasonNumber?.ToString();
        var ranked = images.OrderByDescending(i => LanguageScore(i.Language))
                           .ThenByDescending(i => ParseInt(i.Likes));

        // 1. Exact season match
        if (seasonStr is not null)
        {
            var exact = ranked.FirstOrDefault(i =>
                string.Equals(i.Season, seasonStr, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(i.Url));
            if (exact is not null) return exact;
        }

        // 2. "all" backdrops cover every season
        var all = ranked.FirstOrDefault(i =>
            string.Equals(i.Season, "all", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(i.Url));
        if (all is not null) return all;

        // 3. Series-wide (no Season field)
        return ranked.FirstOrDefault(i =>
            string.IsNullOrEmpty(i.Season) && !string.IsNullOrWhiteSpace(i.Url));
    }

    /// <summary>
    /// Selects the best image for a specific season number from a season-scoped list.
    /// </summary>
    private FanartImage? BestSeasonImage(IReadOnlyList<FanartImage>? images, int seasonNumber)
    {
        if (images is null or { Count: 0 }) return null;
        var seasonStr = seasonNumber.ToString();
        return images
            .Where(i => string.Equals(i.Season, seasonStr, StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(i.Url))
            .OrderByDescending(i => LanguageScore(i.Language))
            .ThenByDescending(i => ParseInt(i.Likes))
            .FirstOrDefault();
    }

    /// <summary>
    /// Returns a score for image language selection priority.
    /// Higher score = preferred. Tiebreaker is likes count.
    ///   3 = user's preferred language
    ///   2 = English (broadly understood fallback)
    ///   1 = language-neutral ("00" or empty — lower quality on average)
    ///   0 = other language
    /// </summary>
    private int LanguageScore(string? lang)
    {
        if (string.Equals(lang, _preferredLanguage, StringComparison.OrdinalIgnoreCase)) return 3;
        if (string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase)) return 2;
        if (string.IsNullOrEmpty(lang) || lang == "00") return 1;   // language-neutral
        return 0;
    }

    private static int ParseInt(string? s) =>
        int.TryParse(s, out var n) ? n : 0;

    // ── Guard ─────────────────────────────────────────────────────────────────

    private void EnsureConfigured()
    {
        if (_client is null)
            throw new Chronicle.Plugins.PluginAuthException(
                "chronicle.plugin.fanarttv",
                "Fanart.tv plugin is not configured — set an API key in Settings → Plugins → Fanart.tv.");
    }
}
