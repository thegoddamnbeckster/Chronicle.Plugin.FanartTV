using System.Text.Json.Serialization;

namespace Chronicle.Plugin.FanartTV;

// ── Shared image record ───────────────────────────────────────────────────────

/// <summary>
/// A single image entry returned by the Fanart.tv API.
/// All numeric fields (likes, width, height) are strings in the API response.
/// </summary>
public sealed class FanartImage
{
    [JsonPropertyName("id")]    public string? Id       { get; set; }
    [JsonPropertyName("url")]   public string? Url      { get; set; }
    [JsonPropertyName("lang")]  public string? Language { get; set; }
    [JsonPropertyName("likes")] public string? Likes    { get; set; }
    [JsonPropertyName("width")] public string? Width    { get; set; }
    [JsonPropertyName("height")]public string? Height   { get; set; }
    [JsonPropertyName("added")] public string? Added    { get; set; }
    /// <summary>Season number — only set on season-scoped TV images.</summary>
    [JsonPropertyName("season")]public string? Season   { get; set; }
    /// <summary>Disc number — only set on cdart album images.</summary>
    [JsonPropertyName("disc")]  public string? Disc     { get; set; }
    /// <summary>Size label — only set on cdart album images.</summary>
    [JsonPropertyName("size")]  public string? Size     { get; set; }
}

// ── Movie response ────────────────────────────────────────────────────────────

/// <summary>
/// Root response from <c>GET /v3.2/movies/{tmdbId}</c>.
/// </summary>
public sealed class FanartMovieResponse
{
    [JsonPropertyName("name")]      public string? Name    { get; set; }
    [JsonPropertyName("tmdb_id")]   public string? TmdbId  { get; set; }
    [JsonPropertyName("imdb_id")]   public string? ImdbId  { get; set; }
    [JsonPropertyName("image_count")]public int ImageCount { get; set; }

    // Posters
    [JsonPropertyName("movieposter")]     public List<FanartImage>? MoviePosters    { get; set; }
    // Backdrops / fanart
    [JsonPropertyName("moviebackground")] public List<FanartImage>? MovieBackgrounds { get; set; }
    // HD clear logo (transparent PNG, ~800×310)
    [JsonPropertyName("hdmovielogo")]     public List<FanartImage>? HdMovieLogos    { get; set; }
    // SD clear logo (~400×155)
    [JsonPropertyName("movielogo")]       public List<FanartImage>? MovieLogos      { get; set; }
    // HD clear art (character art, transparent PNG, ~1000×562)
    [JsonPropertyName("hdmovieclearart")] public List<FanartImage>? HdMovieClearArts { get; set; }
    // SD clear art (~500×281)
    [JsonPropertyName("movieart")]        public List<FanartImage>? MovieArts       { get; set; }
    // Disc / DVD art (~1000×1000 circle)
    [JsonPropertyName("moviedisc")]       public List<FanartImage>? MovieDiscImages { get; set; }
    // Wide landscape thumb (~1000×562)
    [JsonPropertyName("moviethumb")]      public List<FanartImage>? MovieThumbs     { get; set; }
    // Wide banner (~1000×185)
    [JsonPropertyName("moviebanner")]     public List<FanartImage>? MovieBanners    { get; set; }
}

// ── TV response ───────────────────────────────────────────────────────────────

/// <summary>
/// Root response from <c>GET /v3.2/tv/{tvdbId}</c>.
/// Season-scoped images carry a non-null <see cref="FanartImage.Season"/> field.
/// </summary>
public sealed class FanartTvResponse
{
    [JsonPropertyName("name")]       public string? Name     { get; set; }
    [JsonPropertyName("thetvdb_id")] public string? TvdbId   { get; set; }
    [JsonPropertyName("image_count")]public int ImageCount   { get; set; }

    // Series-level posters (~1000×1426)
    [JsonPropertyName("tvposter")]      public List<FanartImage>? TvPosters       { get; set; }
    // Series + season backdrops (~1920×1080)
    [JsonPropertyName("showbackground")]public List<FanartImage>? ShowBackgrounds  { get; set; }
    // HD TV logo (~800×310)
    [JsonPropertyName("hdtvlogo")]      public List<FanartImage>? HdTvLogos        { get; set; }
    // SD clear logo (~400×155)
    [JsonPropertyName("clearlogo")]     public List<FanartImage>? ClearLogos       { get; set; }
    // HD clear art (~1000×562)
    [JsonPropertyName("hdclearart")]    public List<FanartImage>? HdClearArts      { get; set; }
    // SD clear art (~500×281)
    [JsonPropertyName("clearart")]      public List<FanartImage>? ClearArts        { get; set; }
    // Wide banner (~1000×185)
    [JsonPropertyName("tvbanner")]      public List<FanartImage>? TvBanners        { get; set; }
    // TV thumb / landscape (~1000×562)
    [JsonPropertyName("tvthumb")]       public List<FanartImage>? TvThumbs         { get; set; }
    // Character art (~512×512 transparent)
    [JsonPropertyName("characterart")]  public List<FanartImage>? CharacterArts    { get; set; }
    // Season-specific posters (FanartImage.Season = season number or "all")
    [JsonPropertyName("seasonposter")]  public List<FanartImage>? SeasonPosters    { get; set; }
    // Season thumbs (~500×281, FanartImage.Season set)
    [JsonPropertyName("seasonthumb")]   public List<FanartImage>? SeasonThumbs     { get; set; }
    // Season banners (FanartImage.Season set)
    [JsonPropertyName("seasonbanner")]  public List<FanartImage>? SeasonBanners    { get; set; }
}

// ── Music artist response ─────────────────────────────────────────────────────

/// <summary>
/// Root response from <c>GET /v3.2/music/{artistMbid}</c>.
/// Contains both artist-level images and per-album art via <see cref="Albums"/>.
/// </summary>
public sealed class FanartArtistResponse
{
    [JsonPropertyName("name")]    public string? Name          { get; set; }
    [JsonPropertyName("mbid_id")] public string? MusicBrainzId { get; set; }

    // Artist thumb / headshot (~500×281)
    [JsonPropertyName("artistthumb")]      public List<FanartImage>? ArtistThumbs     { get; set; }
    // Artist backdrop / fanart (~1920×1080)
    [JsonPropertyName("artistbackground")] public List<FanartImage>? ArtistBackgrounds { get; set; }
    // HD music logo (~800×310)
    [JsonPropertyName("hdmusiclogo")]      public List<FanartImage>? HdMusicLogos      { get; set; }
    // SD music logo (~400×155)
    [JsonPropertyName("musiclogo")]        public List<FanartImage>? MusicLogos        { get; set; }
    // Music banner (~1000×185)
    [JsonPropertyName("musicbanner")]      public List<FanartImage>? MusicBanners      { get; set; }
    // HD music clear-art (~1000×562)
    [JsonPropertyName("hdmusicarts")]      public List<FanartImage>? HdMusicArts       { get; set; }
    // SD music clear-art (~500×281)
    [JsonPropertyName("musicarts")]        public List<FanartImage>? MusicArts         { get; set; }
    // Per-album artwork (keyed by MusicBrainz release-group MBID)
    [JsonPropertyName("albums")]
    public Dictionary<string, FanartAlbum>? Albums { get; set; }
}

/// <summary>
/// Per-album artwork nested inside <see cref="FanartArtistResponse.Albums"/>.
/// The dictionary key is the MusicBrainz release-group MBID.
/// </summary>
public sealed class FanartAlbum
{
    // CD art / disc image (~1000×1000 circle)
    [JsonPropertyName("cdart")]      public List<FanartImage>? CdArts      { get; set; }
    // Album cover / front art
    [JsonPropertyName("albumcover")] public List<FanartImage>? AlbumCovers { get; set; }
}
