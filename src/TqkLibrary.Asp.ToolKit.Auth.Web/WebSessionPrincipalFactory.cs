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
        /// Principal carrying only the API session id and the user id (<c>ClaimTypes.NameIdentifier</c>, omitted when
        /// null), plus <see cref="WebAuthOptions.StableIdentityClaimType"/> with the session id: the content of the cookie,
        /// and the request principal when the API could not be asked (authenticated, but no name and no permission).
        /// </summary>
        public ClaimsPrincipal CreateSessionOnly(string sessionId, string? userId)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            var claims = new List<Claim> { new(_options.SessionIdClaimType, sessionId) };
            if (!string.IsNullOrEmpty(userId))
                claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
            AddStableIdentity(claims, sessionId);
            return new(new ClaimsIdentity(claims, _options.Scheme, ClaimTypes.Name, ClaimTypes.Role));
        }

        /// <summary>
        /// Request principal: session id from the cookie (and <see cref="WebAuthOptions.StableIdentityClaimType"/>); id,
        /// name, email, roles, permissions and <see cref="CurrentUserInfo.ExtraClaims"/> CURRENT from the API.
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
            AddStableIdentity(claims, sessionId);
            if (!string.IsNullOrEmpty(user.Email))
                claims.Add(new Claim(ClaimTypes.Email, user.Email));
            claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
            claims.AddRange(user.Permissions.Select(permission => new Claim(_options.PermissionClaimType, permission)));
            foreach (var extra in user.ExtraClaims ?? [])
            {
                if (extra is null || string.IsNullOrEmpty(extra.Type) || extra.Value is null || IsReserved(extra.Type))
                    continue;
                claims.Add(new Claim(extra.Type, extra.Value));
            }
            return new ClaimsPrincipal(new ClaimsIdentity(claims, _options.Scheme, ClaimTypes.Name, ClaimTypes.Role));
        }

        /// <summary>Claim types owned by the factory: extra claims cannot forge identity, roles or permissions.</summary>
        private bool IsReserved(string type)
            => type == _options.SessionIdClaimType || type == _options.StableIdentityClaimType
               || type == ClaimTypes.Role || type == _options.PermissionClaimType
               || type == ClaimTypes.NameIdentifier || type == ClaimTypes.Name || type == ClaimTypes.Email;

        /// <summary>
        /// Same identity for antiforgery on every principal of the session: the session id under
        /// <see cref="WebAuthOptions.StableIdentityClaimType"/>.
        /// </summary>
        private void AddStableIdentity(List<Claim> claims, string sessionId)
        {
            var type = _options.StableIdentityClaimType;
            if (!string.IsNullOrEmpty(type) && type != _options.SessionIdClaimType)
                claims.Add(new Claim(type, sessionId));
        }
    }
}
