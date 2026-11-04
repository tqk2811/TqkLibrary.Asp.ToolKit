using System.Net;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using TqkLibrary.Asp.ToolKit.Caching.Remote;

using Xunit;

namespace TqkLibrary.Asp.ToolKit.Caching.Tests;

/// <summary>The sender re-checks enabled/URL per batch after a configuration reload.</summary>
public sealed class HttpCacheInvalidationSenderReloadTests
{
    private const string ValidUrl = "http://localhost:5000/_internal/cache/invalidate";

    private sealed class MutableOptionsMonitor(CacheInvalidationSenderOptions value) : IOptionsMonitor<CacheInvalidationSenderOptions>
    {
        public CacheInvalidationSenderOptions CurrentValue { get; set; } = value;
        public CacheInvalidationSenderOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<CacheInvalidationSenderOptions, string?> listener) => null;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<Uri?> _requests = [];

        public IReadOnlyList<Uri?> Requests { get { lock (_gate) return _requests.ToArray(); } }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate)
                _requests.Add(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public MutableOptionsMonitor Monitor { get; }
        public RecordingHandler Handler { get; } = new();
        public ListLogger Logger { get; } = new();
        public HttpCacheInvalidationNotifier<TestGroup> Notifier { get; }
        public HttpCacheInvalidationSender<TestGroup> Sender { get; }

        public Harness()
        {
            var options = NewOptions(ValidUrl);
            Monitor = new MutableOptionsMonitor(options);
            Notifier = new HttpCacheInvalidationNotifier<TestGroup>(Options.Create(options));
            // A fake clock never advances, so a retry delay would block forever: any retry makes the test time out.
            Sender = new HttpCacheInvalidationSender<TestGroup>(Notifier, new SingleClientFactory(Handler), Monitor,
                new FakeTimeProvider(), new Logger<HttpCacheInvalidationSender<TestGroup>>(new LoggerFactory([Logger])));
        }

        /// <summary>Starts the sender and waits until its startup check has read the (valid) options.</summary>
        public async Task StartAsync()
        {
            await Sender.StartAsync(CancellationToken.None);
            // ExecuteAsync may run on another thread: reloading before the startup check would test the startup path.
            await WaitUntilAsync(() => Logger.Entries.Any(e => e.Message.StartsWith("Remote cache invalidation enabled")), Logger);
        }

        public async ValueTask DisposeAsync()
        {
            await Sender.StopAsync(CancellationToken.None);
            Sender.Dispose();
        }
    }

    private static CacheInvalidationSenderOptions NewOptions(string url) => new()
    {
        TargetUrl = url,
        Key = "key-1",
        Debounce = TimeSpan.Zero,
        RetryDelays = [TimeSpan.FromHours(1)],
    };

    private static async Task WaitUntilAsync(Func<bool> condition, ListLogger? logger = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached in time. Log: " + string.Join(" | ", logger?.Entries.Select(e => e.Level + ": " + e.Message) ?? []));
            await Task.Delay(10);
        }
    }

    [Theory]
    [InlineData("/_internal/cache/invalidate")]
    [InlineData("ftp://localhost/invalidate")]
    [InlineData("ht tp:/broken")]
    public async Task ReloadToInvalidUrl_DropsBatchWithErrorAndNoRetry(string badUrl)
    {
        await using var h = new Harness();
        await h.StartAsync();

        h.Monitor.CurrentValue = NewOptions(badUrl);
        h.Notifier.OnInvalidated([TestGroup.A]);
        await WaitUntilAsync(() => h.Logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("dropped without retry")));

        // The loop is still alive: a fixed URL is used for the next batch.
        h.Monitor.CurrentValue = NewOptions(ValidUrl);
        h.Notifier.OnInvalidated([TestGroup.B]);
        await WaitUntilAsync(() => h.Handler.Requests.Count == 1);

        Assert.Equal(new Uri(ValidUrl), h.Handler.Requests.Single());
        Assert.DoesNotContain(h.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Could not send"));
    }

    [Fact]
    public async Task ReloadToDisabled_DropsBatchesWithoutSendingAndLogsOnce()
    {
        await using var h = new Harness();
        await h.StartAsync();

        var disabled = NewOptions(string.Empty);
        disabled.DisabledLogLevel = LogLevel.Information;
        h.Monitor.CurrentValue = disabled;
        h.Notifier.OnInvalidated([TestGroup.A]);
        await WaitUntilAsync(() => h.Logger.Entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains("disabled by a configuration reload")), h.Logger);

        // A second dropped batch is not logged again at DisabledLogLevel (only Debug).
        h.Notifier.OnInvalidated([TestGroup.B]);
        // Re-enable: the next request must be the only one, proving the dropped batches never went out.
        h.Monitor.CurrentValue = NewOptions(ValidUrl);
        h.Notifier.OnInvalidated([TestGroup.C]);
        await WaitUntilAsync(() => h.Handler.Requests.Count == 1, h.Logger);

        Assert.Single(h.Handler.Requests);
        Assert.Single(h.Logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("disabled by a configuration reload"));
    }

    [Fact]
    public async Task ReloadToOtherValidUrl_SendsToNewUrl()
    {
        await using var h = new Harness();
        await h.StartAsync();

        const string otherUrl = "http://127.0.0.1:6000/other/invalidate";
        h.Monitor.CurrentValue = NewOptions(otherUrl);
        h.Notifier.OnInvalidated([TestGroup.A]);
        await WaitUntilAsync(() => h.Handler.Requests.Count == 1);

        Assert.Equal(new Uri(otherUrl), h.Handler.Requests.Single());
    }
}
