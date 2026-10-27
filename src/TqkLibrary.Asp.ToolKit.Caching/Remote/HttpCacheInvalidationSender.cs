using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace TqkLibrary.Asp.ToolKit.Caching.Remote
{
    /// <summary>
    /// Background service: reads groups from <see cref="HttpCacheInvalidationNotifier{TGroup}"/>, merges them within
    /// the debounce window, then POSTs <c>{"groups":[...]}</c> once to the receiving endpoint with the key header.
    /// Failures are retried (<see cref="CacheInvalidationSenderOptions.RetryDelays"/>); after the last attempt only a
    /// warning is logged (the remote cache expires by TTL). 401/403/404 mean misconfiguration: no retry, logged as error.
    /// </summary>
    /// <typeparam name="TGroup">App-defined enum that names the cache groups.</typeparam>
    public sealed class HttpCacheInvalidationSender<TGroup> : BackgroundService where TGroup : struct, Enum
    {
        /// <summary>Name of the <see cref="HttpClient"/> used to send.</summary>
        public const string HttpClientName = "TqkLibrary.Asp.ToolKit.Caching.Invalidation";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly HttpCacheInvalidationNotifier<TGroup> _notifier;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly CacheInvalidationSenderOptions _options;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger _logger;

        public HttpCacheInvalidationSender(
            HttpCacheInvalidationNotifier<TGroup> notifier,
            IHttpClientFactory httpClientFactory,
            IOptions<CacheInvalidationSenderOptions> options,
            TimeProvider timeProvider,
            ILogger<HttpCacheInvalidationSender<TGroup>> logger)
        {
            _notifier = notifier;
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.IsEnabled)
            {
                _logger.LogInformation("Remote cache invalidation is disabled (TargetUrl or Key is empty).");
                return;
            }
            LogStartup();

            var reader = _notifier.Reader;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var first = await reader.ReadAsync(stoppingToken).ConfigureAwait(false);
                    if (_options.Debounce > TimeSpan.Zero)
                        await Task.Delay(_options.Debounce, _timeProvider, stoppingToken).ConfigureAwait(false);

                    var batch = new HashSet<TGroup> { first };
                    while (reader.TryRead(out var next))
                        batch.Add(next);

                    await SendWithRetryAsync(batch, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ChannelClosedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // The background loop must not die because of one unexpected failure.
                    _logger.LogWarning(ex, "Unexpected error while sending cache invalidation");
                }
            }
        }

        private void LogStartup()
        {
            _logger.LogInformation("Remote cache invalidation enabled -> {Url}", _options.TargetUrl);
            if (Uri.TryCreate(_options.TargetUrl, UriKind.Absolute, out var uri) && !uri.IsLoopback)
                _logger.LogWarning(
                    "Cache invalidation target host {Host} is not loopback; a receiver that requires loopback will reject it (404)",
                    uri.Host);
        }

        private async Task SendWithRetryAsync(HashSet<TGroup> groups, CancellationToken ct)
        {
            string? lastError = null;
            var delays = _options.RetryDelays;
            for (int attempt = 0; attempt <= delays.Count; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(delays[attempt - 1], _timeProvider, ct).ConfigureAwait(false);

                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(_options.Timeout);
                    var client = _httpClientFactory.CreateClient(HttpClientName);
                    using var request = new HttpRequestMessage(HttpMethod.Post, _options.TargetUrl)
                    {
                        Content = JsonContent.Create(new { groups = groups.ToArray() }, options: JsonOptions),
                    };
                    request.Headers.TryAddWithoutValidation(_options.KeyHeader, _options.Key);

                    using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                        return;

                    if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        _logger.LogError("Cache invalidation rejected (HTTP {Status}); check the target URL and key. Not retrying ({Groups})",
                            (int)response.StatusCode, string.Join(",", groups));
                        return;
                    }
                    lastError = $"HTTP {(int)response.StatusCode}";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            _logger.LogWarning("Could not send cache invalidation after {Attempts} attempts ({Groups}): {Error}",
                delays.Count + 1, string.Join(",", groups), lastError);
        }
    }
}
