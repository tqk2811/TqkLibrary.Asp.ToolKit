using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
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
    /// <para>
    /// The options are read through <see cref="IOptionsMonitor{TOptions}"/> once per batch (after the debounce
    /// window), so a changed URL, key, timeout or retry list applies without a restart. A batch is dropped when a
    /// reload has disabled sending (logged at <see cref="CacheInvalidationSenderOptions.DisabledLogLevel"/>) or set an
    /// invalid URL (logged as error, not retried). The startup check only decides whether the send loop runs at all:
    /// sending disabled or an invalid URL at startup needs a restart to turn on. A batch still waiting for the debounce
    /// window or for a retry when the application stops is dropped (the remote cache then expires by TTL).
    /// </para>
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
        private readonly IOptionsMonitor<CacheInvalidationSenderOptions> _options;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger _logger;

        /// <summary>Creates the background sender.</summary>
        /// <param name="notifier">Source of the invalidated groups.</param>
        /// <param name="httpClientFactory">Factory of the named client <see cref="HttpClientName"/>.</param>
        /// <param name="options">Sender options; the current value is read per batch.</param>
        /// <param name="timeProvider">Clock used for debounce and retry delays.</param>
        /// <param name="logger">Logger.</param>
        public HttpCacheInvalidationSender(
            HttpCacheInvalidationNotifier<TGroup> notifier,
            IHttpClientFactory httpClientFactory,
            IOptionsMonitor<CacheInvalidationSenderOptions> options,
            TimeProvider timeProvider,
            ILogger<HttpCacheInvalidationSender<TGroup>> logger)
        {
            _notifier = notifier;
            _httpClientFactory = httpClientFactory;
            _options = options;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var startup = _options.CurrentValue;
            if (!startup.IsEnabled)
            {
                _logger.Log(startup.DisabledLogLevel,
                    "Remote cache invalidation is disabled (TargetUrl or Key is empty): invalidations are NOT sent to other processes.");
                return;
            }
            if (!TryGetTargetUri(startup, out var startupUri))
            {
                _logger.LogError("Remote cache invalidation is NOT running: TargetUrl '{Url}' is not an absolute http/https URL.",
                    startup.TargetUrl);
                return;
            }
            LogStartup(startup, startupUri);

            var reader = _notifier.Reader;
            bool disabledByReload = false;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var first = await reader.ReadAsync(stoppingToken).ConfigureAwait(false);
                    var debounce = _options.CurrentValue.Debounce;
                    if (debounce > TimeSpan.Zero)
                        await Task.Delay(debounce, _timeProvider, stoppingToken).ConfigureAwait(false);

                    var batch = new HashSet<TGroup> { first };
                    while (reader.TryRead(out var next))
                        batch.Add(next);

                    // One snapshot per batch, taken after the debounce so the batch uses the newest configuration.
                    var options = _options.CurrentValue;
                    if (!options.IsEnabled)
                    {
                        // Log once per enabled -> disabled switch; later dropped batches only at Debug.
                        _logger.Log(disabledByReload ? LogLevel.Debug : options.DisabledLogLevel,
                            "Remote cache invalidation was disabled by a configuration reload; dropped ({Groups})",
                            string.Join(",", batch));
                        disabledByReload = true;
                        continue;
                    }
                    disabledByReload = false;
                    if (!TryGetTargetUri(options, out var targetUri))
                    {
                        _logger.LogError(
                            "Cache invalidation TargetUrl '{Url}' is not an absolute http/https URL; dropped without retry ({Groups})",
                            options.TargetUrl, string.Join(",", batch));
                        continue;
                    }

                    await SendWithRetryAsync(batch, options, targetUri, stoppingToken).ConfigureAwait(false);
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

        private static bool TryGetTargetUri(CacheInvalidationSenderOptions options, [NotNullWhen(true)] out Uri? uri)
        {
            if (Uri.TryCreate(options.TargetUrl, UriKind.Absolute, out var parsed)
                && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
            {
                uri = parsed;
                return true;
            }
            uri = null;
            return false;
        }

        private void LogStartup(CacheInvalidationSenderOptions options, Uri uri)
        {
            _logger.LogInformation("Remote cache invalidation enabled -> {Url}", options.TargetUrl);
            if (!uri.IsLoopback)
                _logger.LogWarning(
                    "Cache invalidation target host {Host} is not loopback; a receiver that requires loopback will reject it (404)",
                    uri.Host);
        }

        private async Task SendWithRetryAsync(HashSet<TGroup> groups, CacheInvalidationSenderOptions options, Uri targetUri, CancellationToken ct)
        {
            string? lastError = null;
            var delays = options.EffectiveRetryDelays;
            for (int attempt = 0; attempt <= delays.Count; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(delays[attempt - 1], _timeProvider, ct).ConfigureAwait(false);

                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(options.Timeout);
                    var client = _httpClientFactory.CreateClient(HttpClientName);
                    using var request = new HttpRequestMessage(HttpMethod.Post, targetUri)
                    {
                        Content = JsonContent.Create(new { groups = groups.ToArray() }, options: JsonOptions),
                    };
                    request.Headers.TryAddWithoutValidation(options.KeyHeader, options.Key);

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
