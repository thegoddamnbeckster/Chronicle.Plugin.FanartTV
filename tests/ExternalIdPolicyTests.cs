using Chronicle.Plugin.FanartTV;
using Xunit;

namespace Chronicle.Plugin.FanartTV.Tests;

public class ExternalIdPolicyTests
{
    [Fact]
    public void TheProviderDeclaresHowItsIdsBehave()
    {
        var p = new FanartTvMetadataProvider().GetExternalIdPolicy();
        Assert.Equal("fanarttv", p.Source);
        Assert.True(p.IgnoredForConflicts);
    }

    [Theory]
    [InlineData("movie", "movie:")]
    [InlineData("tv", "tvdb:")]
    public void AnItemIsOnlySeededWithIdsOfItsOwnKind(string family, string accepted)
    {
        var prefixes = new FanartTvMetadataProvider().GetAcceptedCrossRefPrefixes(family);
        Assert.Equal([accepted], prefixes);
    }

    [Fact]
    public void ADeclaredShapeDoesNotAllowAShowIdOnAMovie()
    {
        var shapes = new FanartTvMetadataProvider().GetExternalIdPolicy().IdShapes;
        bool Fits(string id, string family) => shapes.Any(s =>
            System.Text.RegularExpressions.Regex.IsMatch(id, s.Pattern) && s.ItemFamilies.Contains(family));
        Assert.True(Fits("tv:22435", "tv"));
        Assert.False(Fits("tv:22435", "movie"));
        Assert.True(Fits("movie:18395", "movie"));
        Assert.False(Fits("movie:18395", "tv"));
    }
}
