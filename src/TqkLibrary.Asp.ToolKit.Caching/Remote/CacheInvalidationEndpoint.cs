using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TqkLibrary.Asp.ToolKit.Caching.Remote
{
    /// <summary>
    /// Internal endpoint through which another process says "these groups just changed":
    /// <c>POST {Path}</c> with body <c>{"groups":["GroupA","GroupB"]}</c> (enum names) invalidates those groups in the
    /// local <see cref="CacheRegistry{TGroup}"/>. It calls the registry directly, NOT
    /// <see cref="Interfaces.ICacheInvalidator{TGroup}"/>, so two processes never echo signals back and forth.
    /// <para>
    /// A request is accepted only when ALL hold (see <see cref="IsAllowed"/>): a key is configured, (by default) the
    /// raw TCP peer is loopback with no proxy forwarding header, and the key header matches. Otherwise the answer is
    /// 404 as if the endpoint did not exist. Register it BEFORE <c>UseForwardedHeaders</c>, HTTPS redirection and
    /// status code pages so the remote address is still raw and the response is not rewritten.
    /// </para>
    /// </summary>
    /// <typeparam name="TGroup">App-defined enum that names the cache groups.</typeparam>
    public sealed class CacheInvalidationEndpoint<TGroup> where TGroup : struct, Enum
    {
        private sealed class InvalidateRequest
        {
            public List<TGroup>? Groups { get; set; }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            // Only group NAMES: integers or unknown names are JSON errors -> 400.
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        };

        private readonly CacheRegistry<TGroup> _registry;
        private readonly IOptionsMonitor<CacheInvalidationEndpointOptions> _options;

        /// <summary>Creates the endpoint.</summary>
        /// <param name="registry">Registry whose caches are invalidated by incoming requests.</param>
        /// <param name="options">Endpoint options; the current value is read per request.</param>
        public CacheInvalidationEndpoint(CacheRegistry<TGroup> registry, IOptionsMonitor<CacheInvalidationEndpointOptions> options)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(options);
            _registry = registry;
            _options = options;
        }

        /// <summary>True when the request targets the configured path.</summary>
        public bool Matches(HttpContext context)
            => context.Request.Path.Equals(_options.CurrentValue.Path, StringComparison.OrdinalIgnoreCase);

        /// <summary>Handles one request (options are read per request so configuration changes apply).</summary>
        public async Task HandleAsync(HttpContext context)
        {
            var options = _options.CurrentValue;
            if (!HttpMethods.IsPost(context.Request.Method) || !IsAllowed(context, options))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var body = await ReadBodyAsync(context, options.MaxBodyBytes).ConfigureAwait(false);
            if (body is null)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            InvalidateRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<InvalidateRequest>(body, JsonOptions);
            }
            catch (JsonException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var groups = request?.Groups?.Distinct().ToArray() ?? [];
            if (groups.Length > 0)
                _registry.Invalidate(groups);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }

        /// <summary>Whether the request may invalidate caches (key configured, loopback rule, key matches).</summary>
        public static bool IsAllowed(HttpContext context, CacheInvalidationEndpointOptions options)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(options);
            if (string.IsNullOrEmpty(options.Key))
                return false;

            if (options.RequireLoopback)
            {
                // Raw TCP peer address: not yet rewritten by UseForwardedHeaders, so a header cannot fake it.
                var remote = context.Connection.RemoteIpAddress;
                if (remote is null || !IPAddress.IsLoopback(remote))
                    return false;

                // A reverse proxy on the same machine also shows up as loopback: a forwarding header means the
                // original request came from outside.
                foreach (var header in options.EffectiveForwardingHeaders)
                {
                    if (context.Request.Headers.ContainsKey(header))
                        return false;
                }
            }

            var actual = context.Request.Headers[options.KeyHeader].ToString();
            // Compare SHA-256 of both sides: equal lengths, so timing does not reveal the key length.
            return CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(actual)), SHA256.HashData(Encoding.UTF8.GetBytes(options.Key)));
        }

        // null = body larger than maxBytes. Content-Length is not trusted (missing when chunked): count bytes read.
        private static async Task<byte[]?> ReadBodyAsync(HttpContext context, int maxBytes)
        {
            if (context.Request.ContentLength > maxBytes)
                return null;
            using var buffer = new MemoryStream();
            var chunk = new byte[1024];
            int read;
            while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    return null;
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
    }
}
