namespace Chronicle.Plugin.FanartTV.Tests;

/// <summary>
/// Root-caused live (2026-09-28): FanartTvClient's own class doc has always promised "a small
/// jitter between requests," but nothing in the file ever actually paced anything -- every call
/// went out back-to-back. These tests cover the pure delay computation
/// (FanartTvClient.ComputeThrottleDelay) added to make good on that promise.
/// </summary>
public class FanartTvClientThrottleTests
{
    [Fact]
    public void NoTimeElapsedSinceLastRequest_WaitsAtLeastTheBaseInterval()
    {
        var wait = FanartTvClient.ComputeThrottleDelay(TimeSpan.Zero, TimeSpan.Zero);

        Assert.True(wait >= TimeSpan.FromMilliseconds(400), $"Expected >= 400ms, got {wait}");
    }

    [Fact]
    public void JitterIsAddedOnTopOfTheBaseInterval()
    {
        var jitter = TimeSpan.FromMilliseconds(250);

        var wait = FanartTvClient.ComputeThrottleDelay(TimeSpan.Zero, jitter);

        Assert.Equal(TimeSpan.FromMilliseconds(650), wait); // 400ms base + 250ms jitter
    }

    [Fact]
    public void EnoughTimeAlreadyElapsed_RequiresNoAdditionalWait()
    {
        var wait = FanartTvClient.ComputeThrottleDelay(TimeSpan.FromSeconds(5), TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, wait);
    }

    [Fact]
    public void PartiallyElapsedTime_IsCreditedAgainstTheWait()
    {
        var wait = FanartTvClient.ComputeThrottleDelay(TimeSpan.FromMilliseconds(300), TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromMilliseconds(100), wait); // 400ms base - 300ms already elapsed
    }

    [Fact]
    public void NeverReturnsANegativeDelay()
    {
        var wait = FanartTvClient.ComputeThrottleDelay(TimeSpan.FromHours(1), TimeSpan.Zero);

        Assert.True(wait >= TimeSpan.Zero);
    }
}
