using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TqkLibrary.Asp.ToolKit.Auth.Api.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>
    /// Issues COMPACT access tokens (JWT, HMAC-SHA256): user id, session id, <c>jti</c> and lifetime only. Name, email,
    /// roles and permissions are NOT in the token; <see cref="SessionTokenValidator"/> loads them per request, so role,
    /// permission and revocation changes apply without waiting for the token to expire. Singleton.
    /// </summary>
    public sealed class AccessTokenFactory
    {
        private readonly ApiAuthOptions _options;
        private readonly SigningCredentials _credentials;
        private readonly JsonWebTokenHandler _handler = new();

        public AccessTokenFactory(IOptions<ApiAuthOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _options = options.Value;
            _options.Validate();
            _credentials = new SigningCredentials(_options.CreateSigningKey(), SecurityAlgorithms.HmacSha256);
        }

        /// <summary>Creates an access token for <paramref name="userId"/> in session <paramref name="sessionId"/>.</summary>
        public IssuedAccessToken Create(string userId, string sessionId, DateTime utcNow)
        {
            ArgumentException.ThrowIfNullOrEmpty(userId);
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            var now = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            var expires = now.Add(_options.AccessTokenLifetime);
            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = _options.Issuer,
                Audience = _options.EffectiveAudience,
                IssuedAt = now,
                NotBefore = now,
                Expires = expires,
                SigningCredentials = _credentials,
                Claims = new Dictionary<string, object>
                {
                    [_options.UserIdClaimType] = userId,
                    [_options.SessionIdClaimType] = sessionId,
                    [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
                },
            };
            return new IssuedAccessToken(_handler.CreateToken(descriptor), expires);
        }
    }
}
