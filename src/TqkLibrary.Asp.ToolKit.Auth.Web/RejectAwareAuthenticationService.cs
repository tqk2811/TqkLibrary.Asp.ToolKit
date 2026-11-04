using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Decorates the app's <see cref="IAuthenticationService"/> (installed by <c>AddTqkWebAuth</c>): once the DBSC proof of
    /// a request was rejected (<see cref="WebSessionTicket.IsRejected"/>), authenticating the web cookie scheme again
    /// answers "no result" for the rest of that request. Without it an endpoint whose policy names the cookie scheme
    /// (<c>[Authorize(AuthenticationSchemes = ...)]</c>, <c>AddAuthenticationSchemes</c>) gets the cookie principal back
    /// from <c>PolicyEvaluator</c> and runs although the request was made a guest.
    /// <para>
    /// A decorator, not an <see cref="IClaimsTransformation"/>: <see cref="AuthenticationService"/> transforms a principal
    /// only once per request (it remembers the principals it produced), so a transformation could not see a rejection
    /// that happens after <c>UseAuthentication</c>; and an app may have its own transformation (only one is resolved).
    /// Every other call goes straight to the decorated service; the check costs nothing until a request is rejected.
    /// </para>
    /// </summary>
    internal sealed class RejectAwareAuthenticationService : IAuthenticationService
    {
        private readonly IAuthenticationService _inner;
        private readonly IOptions<WebAuthOptions> _options;

        public RejectAwareAuthenticationService(IAuthenticationService inner, IOptions<WebAuthOptions> options)
        {
            _inner = inner;
            _options = options;
        }

        /// <summary>The decorated service.</summary>
        internal IAuthenticationService Inner => _inner;

        public async Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            var result = await _inner.AuthenticateAsync(context, scheme).ConfigureAwait(false);
            if (result.Succeeded
                && string.Equals(result.Ticket?.AuthenticationScheme, _options.Value.Scheme, StringComparison.Ordinal)
                && WebSessionTicket.IsRejected(context))
                return AuthenticateResult.NoResult();
            return result;
        }

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => _inner.ChallengeAsync(context, scheme, properties);

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => _inner.ForbidAsync(context, scheme, properties);

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
            => _inner.SignInAsync(context, scheme, principal, properties);

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => _inner.SignOutAsync(context, scheme, properties);
    }
}
