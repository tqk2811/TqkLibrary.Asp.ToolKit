using Microsoft.Extensions.Options;
using System.Security.Claims;
using TqkLibrary.Asp.ToolKit.Auth.Web.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>Builds the principals of the web session (singleton).</summary>
    public sealed class WebSessionPrincipalFactory
    {
        private readonly WebAuthOptions _options;

        public WebSessionPrincipalFactory(IOptions<WebAuthOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _options = options.Value;
        }

        /// <summary>
        /// Principal carrying only the API session id: the content of the cookie, and the request principal when the
        /// API could not be asked (authenticated, but no name and no permission).
        /// </summary>
        public ClaimsPrincipal CreateSessionOnly(string sessionId)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            return new(new ClaimsIdentity([new Claim(_options.SessionIdClaimType, sessionId)],
                _options.Scheme, ClaimTypes.Name, ClaimTypes.Role));
        }

        /// <summary>
        /// Request principal: session id from the cookie; id, name, email, roles and permissions CURRENT from the API.
        /// </summary>
        public ClaimsPrincipal Create(string sessionId, CurrentUserInfo user)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            ArgumentNullException.ThrowIfNull(user);
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id),
                new(ClaimTypes.Name, user.UserName),
                new(_options.SessionIdClaimType, sessionId),
            };
            if (!string.IsNullOrEmpty(user.Email))
                claims.Add(new Claim(ClaimTypes.Email, user.Email));
            claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
            claims.AddRange(user.Permissions.Select(permission => new Claim(_options.PermissionClaimType, permission)));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, _options.Scheme, ClaimTypes.Name, ClaimTypes.Role));
        }
    }
}
