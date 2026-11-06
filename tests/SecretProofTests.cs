using System.Net;
using Microsoft.Extensions.Logging;

namespace Chronicle.Plugin.FanartTV.Tests;

/// <summary>The checklist's K7: a call that fails on purpose must not carry the API key into an error or a log line.</summary>
public class SecretProofTests
{
    private const string Key = "SECRET-KEY-1234567890";
    private const string ClientKey = "SECRET-CLIENT-0987654321";

    internal sealed class ProofHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class Capture : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_failing_call_leaves_both_keys_out_of_the_error_and_the_log(HttpStatusCode status)
    {
        var log = new Capture();
        var client = new Chronicle.Plugin.FanartTV.FanartTvClient(new HttpClient(new ProofHandler(_ => new HttpResponseMessage(status))), Key, ClientKey, log);
        var ex = await Record.ExceptionAsync(() => client.GetMovieAsync("603"));
        Assert.NotNull(ex);
        Assert.DoesNotContain(Key, ex!.ToString());
        Assert.DoesNotContain(ClientKey, ex.ToString());
        Assert.DoesNotContain(log.Lines, l => l.Contains(Key) || l.Contains(ClientKey));
    }
}
