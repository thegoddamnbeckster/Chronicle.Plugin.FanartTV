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
}
