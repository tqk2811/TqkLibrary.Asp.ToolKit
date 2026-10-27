using Microsoft.AspNetCore.Http;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Adds the viewer's IP (<c>HttpContext.Connection.RemoteIpAddress</c>, already processed by
    /// <c>UseForwardedHeaders</c> when the web sits behind a reverse proxy) as <c>X-Forwarded-For</c> on every API
    /// call, so API rate limits partition by the real user instead of the web server IP. The API must trust this
    /// header only from known proxies. A header set by the caller is kept; outside a request nothing is added.
    /// Stateless (handlers are shared between users).
    /// </summary>
    public sealed class ClientIpForwardingHandler : DelegatingHandler
    {
        /// <summary>Header name.</summary>
        public const string HeaderName = "X-Forwarded-For";

        private readonly IHttpContextAccessor _httpContextAccessor;

        public ClientIpForwardingHandler(IHttpContextAccessor httpContextAccessor) => _httpContextAccessor = httpContextAccessor;

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ip = _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress;
            if (ip is not null && !request.Headers.Contains(HeaderName))
                request.Headers.TryAddWithoutValidation(HeaderName, (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString());
            return base.SendAsync(request, cancellationToken);
        }
    }
}
