using System.Text.Json;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using System.Text.RegularExpressions;

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
///   music (level 0)    → KnownExternalIds["musicbrainz"] (artist MBID)   → /v3.2/music/{artistMbid}
///   music (level 1+)   → parent's KnownExternalIds["musicbrainz"] (artist MBID), album MBID
///                         used to select the correct album from the artist response
///
/// External ID format stored by this plugin:
///   "movie:{tmdbId}"        for movies and fan edits
///   "tv:{tvdbId}"           for TV shows and anime  (TVDB IDs, NOT TMDB)
///   "artist:{artistMbid}"   for music artists
///   "album:{artistMbid}/{releaseGroupMbid}"  for music albums
///
/// TV items only receive artwork if a TVDB ID is available in media_external_ids
/// (populated by Trakt/SIMKL sync). Items enriched only by TMDB will be NotFound
/// until a TVDB ID is stored (via Trakt sync or manual Fix Match).
///
/// If none of the required cross-reference IDs are available, SearchAsync returns empty
/// and the enrichment service marks the row as NotFound for this provider.
/// </summary>
public sealed class FanartTvMetadataProvider : IMetadataProvider
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
    private string _preferredLanguage = "en";

    /// <summary>Test-only constructor that injects a pre-built client.</summary>
    internal FanartTvMetadataProvider(FanartTvClient client, string language = "en")
    {
        _client           = client;
        _preferredLanguage = language;
    }

    /// <summary>Required for public instantiation by the host (no-arg).</summary>
    public FanartTvMetadataProvider() { }

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
            HierarchyLevels  = 3,
            HierarchyLabels  = ["Artist", "Album", "Track"],
            DefaultPriority  = 20,
            SupportedFields  = ["poster_url", "backdrop_url", "logo_url", "banner_url",
                                "clearart_url", "thumb_url"],
            LevelFields = new Dictionary<int, List<string>>
            {
                [1] = ["poster_url", "disc_url"],   // album cover + CD art
            },
        },
    ];

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

        var http = new HttpClient
        {
            DefaultRequestHeaders = { { "User-Agent", "Chronicle/1.0" } }
        };
        _client           = new FanartTvClient(http, apiKey, clientKey);
        _preferredLanguage = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim();
    }

    // ── IMetadataProvider: search ─────────────────────────────────────────────

    // External ID patterns this plugin produces / consumes
    private static readonly Regex _movieIdRe  = new(@"^movie:(\d+)$",     RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _tvIdRe     = new(@"^tv:(\d+)$",        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _artistIdRe = new(@"^artist:([0-9a-f-]{36})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _albumIdRe  = new(@"^album:([0-9a-f-]{36})/([0-9a-f-]{36})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Fanart.tv URL patterns for Fix Match normalization
    private static readonly Regex _fanartMovieUrlRe  = new(@"fanart\.tv/movie/(\d+)",   RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _fanartSeriesUrlRe = new(@"fanart\.tv/series/(\d+)",  RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _fanartMusicUrlRe  = new(@"fanart\.tv/music/([0-9a-f-]{36})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
            return [];

        // We already know the Fanart.tv lookup ID — fetch full data immediately.
        // Return as a single 100-score candidate so the enrichment service short-circuits
        // to GetByIdAsync without a second network call.
        var metadata = await FetchByResolvedIdAsync(resolvedId, context.ItemNumber, ct);
        if (metadata is null)
            return [];

        return [new ScoredCandidate(metadata, 100, "cross-reference ID match")];
    }

    /// <summary>
    /// Fetches artwork by a previously-stored Fanart.tv external ID.
    /// Supports "movie:{id}", "tv:{tvdbId}", "artist:{mbid}", "album:{artistMbid}/{rgMbid}" formats.
    /// Also accepts Fanart.tv web URLs for Fix Match convenience:
    ///   https://fanart.tv/movie/550/   → movie:550
    ///   https://fanart.tv/series/76290/ → tv:76290
    ///   https://fanart.tv/music/{mbid}/ → artist:{mbid}
    /// Returns an empty <see cref="MediaMetadata"/> when the ID is not found on Fanart.tv.
    /// </summary>
    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();
        externalId = NormalizeFanartUrl(externalId);
        return await FetchByResolvedIdAsync(externalId, seasonNumber: null, ct)
               ?? new MediaMetadata { ExternalId = externalId };
    }

    /// <summary>
    /// Converts a Fanart.tv web URL into the plugin's internal ID format.
    /// Passes through anything that is already in internal format.
    /// </summary>
    private static string NormalizeFanartUrl(string input)
    {
        if (!input.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return input;

        var movieMatch = _fanartMovieUrlRe.Match(input);
        if (movieMatch.Success) return $"movie:{movieMatch.Groups[1].Value}";

        var seriesMatch = _fanartSeriesUrlRe.Match(input);
        if (seriesMatch.Success) return $"tv:{seriesMatch.Groups[1].Value}";

        var musicMatch = _fanartMusicUrlRe.Match(input);
        if (musicMatch.Success) return $"artist:{musicMatch.Groups[1].Value}";

        return input; // unrecognised URL — pass through, will fail gracefully
    }

    /// <summary>
    /// Fanart.tv does not provide image downloads — images are direct CDN URLs.
    /// This method is not used; return empty.
    /// </summary>
    public Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_client is null) return false;
        return await _client.HealthCheckAsync(ct);
    }

    // ── ID resolution ─────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a <see cref="MediaSearchContext"/> to a Fanart.tv lookup ID using the
    /// cross-reference IDs stored by other providers.
    ///
    /// Priority per media type:
    ///   movies / fanedits → KnownExternalIds["tmdb"] value, strip "movie:" prefix → "movie:{id}"
    ///   tv / anime        → KnownExternalIds["tvdb"]        → "tv:{tvdbId}"
    ///   music (artist)    → KnownExternalIds["musicbrainz"] → "artist:{mbid}"
    ///   music (album, level 1) → KnownExternalIds["musicbrainz"] (release-group) → "artist:{artistMbid}"
    ///                            (album art is embedded inside the artist response)
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
            if (known.TryGetValue("tvdb", out var tvdbId) && !string.IsNullOrWhiteSpace(tvdbId))
                return $"tv:{tvdbId}";
            if (known.TryGetValue("parent_tvdb", out var parentTvdbId) && !string.IsNullOrWhiteSpace(parentTvdbId))
                return $"tv:{parentTvdbId}";
        }
        else if (mediaType is "music" or null)
        {
            if (context.HierarchyLevel == 0)
            {
                // Root level = artist. MusicBrainz stores "artist:{mbid}".
                if (known.TryGetValue("musicbrainz", out var mbId))
                {
                    var mbid = ExtractMbid(mbId);
                    if (mbid is not null) return $"artist:{mbid}";
                }
            }
            else if (context.HierarchyLevel == 1)
            {
                // Album level. We need:
                //   - the artist's MBID (to call /v3.2/music/{artistMbid})
                //   - the release-group MBID (to select the album in the response)
                // The album item itself stores a release-group MBID. The parent artist
                // stores the artist MBID. We use ParentExternalId convention: the enrichment
                // service stores the parent's musicbrainz ID in KnownExternalIds under
                // "parent_musicbrainz" when it populates the context.
                // Fallback: if both artist and release-group MBIDs are in KnownExternalIds
                // (e.g. populated from media_external_ids with multiple rows), use them.
                known.TryGetValue("musicbrainz", out var albumMbId);
                known.TryGetValue("parent_musicbrainz", out var artistMbId);

                var albumMbid  = albumMbId  is not null ? ExtractMbid(albumMbId)  : null;
                var artistMbid = artistMbId is not null ? ExtractMbid(artistMbId) : null;

                if (artistMbid is not null && albumMbid is not null)
                    return $"album:{artistMbid}/{albumMbid}";

                // If we only have one MBID and it's an artist MBID (no release-group available),
                // fall back to artist-level artwork.
                if (artistMbid is not null)
                    return $"artist:{artistMbid}";
            }
            // Tracks (level 2) have no useful Fanart.tv artwork — skip.
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

    /// <summary>Extracts a MusicBrainz UUID from "artist:{mbid}", "release-group:{mbid}", or a bare UUID.</summary>
    private static string? ExtractMbid(string rawId)
    {
        var colonIdx = rawId.LastIndexOf(':');
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
        var movieMatch  = _movieIdRe.Match(resolvedId);
        var tvMatch     = _tvIdRe.Match(resolvedId);
        var artistMatch = _artistIdRe.Match(resolvedId);
        var albumMatch  = _albumIdRe.Match(resolvedId);

        if (movieMatch.Success)
            return await FetchMovieAsync(movieMatch.Groups[1].Value, resolvedId, ct);

        if (tvMatch.Success)
            return await FetchTvAsync(tvMatch.Groups[1].Value, resolvedId, seasonNumber, ct);

        if (albumMatch.Success)
            return await FetchAlbumAsync(
                albumMatch.Groups[1].Value,   // artistMbid
                albumMatch.Groups[2].Value,   // releaseGroupMbid
                resolvedId, ct);

        if (artistMatch.Success)
            return await FetchArtistAsync(artistMatch.Groups[1].Value, resolvedId, ct);

        return null;
    }

    private async Task<MediaMetadata?> FetchMovieAsync(
        string tmdbId, string externalId, CancellationToken ct)
    {
        var response = await _client!.GetMovieAsync(tmdbId, ct);
        if (response is null) return null;

        var poster   = BestImage(response.MoviePosters);
        var backdrop = BestImage(response.MovieBackgrounds);
        var logo     = BestImage(response.HdMovieLogos) ?? BestImage(response.MovieLogos);
        var banner   = BestImage(response.MovieBanners);
        var disc     = BestImage(response.MovieDiscImages);
        var clearart = BestImage(response.HdMovieClearArts) ?? BestImage(response.MovieArts);
        var thumb    = BestImage(response.MovieThumbs);

        return new MediaMetadata
        {
            ExternalId    = externalId,
            Source        = "fanarttv",
            Title         = response.Name ?? string.Empty,
            PosterUrl     = poster?.Url,
            BackdropUrl   = backdrop?.Url,
            LogoUrl       = logo?.Url,
            BannerUrl     = banner?.Url,
            DiscUrl       = disc?.Url,
            ClearartUrl   = clearart?.Url,
            ThumbUrl      = thumb?.Url,
        };
    }

    private async Task<MediaMetadata?> FetchTvAsync(
        string tvdbId, string externalId, int? seasonNumber, CancellationToken ct)
    {
        var response = await _client!.GetTvShowAsync(tvdbId, ct);
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

        return new MediaMetadata
        {
            ExternalId      = externalId,
            Source          = "fanarttv",
            Title           = response.Name ?? string.Empty,
            PosterUrl       = seasonPoster?.Url ?? poster?.Url,
            BackdropUrl     = backdrop?.Url,
            LogoUrl         = logo?.Url,
            BannerUrl       = banner?.Url,
            ClearartUrl     = clearart?.Url,
            ThumbUrl        = seasonThumb?.Url ?? thumb?.Url,
            CharacterArtUrl = charArt?.Url,
        };
    }

    private async Task<MediaMetadata?> FetchArtistAsync(
        string artistMbid, string externalId, CancellationToken ct)
    {
        var response = await _client!.GetArtistAsync(artistMbid, ct);
        if (response is null) return null;

        var thumb    = BestImage(response.ArtistThumbs);
        var backdrop = BestImage(response.ArtistBackgrounds);
        var logo     = BestImage(response.HdMusicLogos) ?? BestImage(response.MusicLogos);
        var banner   = BestImage(response.MusicBanners);
        var clearart = BestImage(response.HdMusicArts) ?? BestImage(response.MusicArts);

        // Build per-album art map so child album items can retrieve cover art
        // by their MusicBrainz release-group MBID.
        Dictionary<string, object?>? albumArt = null;
        if (response.Albums is { Count: > 0 })
        {
            albumArt = [];
            foreach (var (rgMbid, album) in response.Albums)
            {
                var cover = BestImage(album.AlbumCovers);
                var cdart = BestImage(album.CdArts);
                if (cover is not null || cdart is not null)
                {
                    albumArt[rgMbid] = new Dictionary<string, string?>
                    {
                        ["cover_url"] = cover?.Url,
                        ["cdart_url"] = cdart?.Url,
                    };
                }
            }
        }

        // Serialise album art map into ExtendedData so it's preserved in metadata_json
        // for future UI use (e.g. showing album cover per-album child item).
        JsonElement? extendedData = null;
        if (albumArt is { Count: > 0 })
        {
            var json = JsonSerializer.Serialize(new { albums = albumArt });
            extendedData = JsonDocument.Parse(json).RootElement;
        }

        return new MediaMetadata
        {
            ExternalId   = externalId,
            Source       = "fanarttv",
            Title        = response.Name ?? string.Empty,
            PosterUrl    = thumb?.Url,
            BackdropUrl  = backdrop?.Url,
            LogoUrl      = logo?.Url,
            BannerUrl    = banner?.Url,
            ClearartUrl  = clearart?.Url,
            ExtendedData = extendedData,
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
        var response = await _client!.GetArtistAsync(artistMbid, ct);
        if (response is null) return null;

        FanartAlbum? album = null;
        response.Albums?.TryGetValue(releaseGroupMbid, out album);

        var cover = BestImage(album?.AlbumCovers);
        var cdart = BestImage(album?.CdArts);

        // If this specific album has no art, still return a result so the row is marked
        // Completed rather than left Pending indefinitely. Artwork may be added to Fanart.tv later
        // and a resync will pick it up.
        return new MediaMetadata
        {
            ExternalId = externalId,
            Source     = "fanarttv",
            Title      = string.Empty,    // title not available from album art response
            PosterUrl  = cover?.Url,
            DiscUrl    = cdart?.Url,
        };
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

    private int LanguageScore(string? lang)
    {
        if (string.IsNullOrEmpty(lang) || lang == "00") return 2;   // language-neutral
        if (string.Equals(lang, _preferredLanguage, StringComparison.OrdinalIgnoreCase)) return 3;
        if (string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    private static int ParseInt(string? s) =>
        int.TryParse(s, out var n) ? n : 0;

    // ── Metadata helpers ──────────────────────────────────────────────────────

    private void EnsureConfigured()
    {
        if (_client is null)
            throw new InvalidOperationException(
                "Fanart.tv plugin is not configured. Call Configure() first.");
    }
}
