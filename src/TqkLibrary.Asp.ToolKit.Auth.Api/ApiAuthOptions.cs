using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>Options of the session-bound JWT API authentication.</summary>
    public sealed class ApiAuthOptions
    {
        /// <summary>Token issuer.</summary>
        public string Issuer { get; set; } = string.Empty;

        /// <summary>Token audience; empty means the same as <see cref="Issuer"/>.</summary>
        public string? Audience { get; set; }

        /// <summary>HMAC-SHA256 signing key (UTF-8 text, at least 32 bytes).</summary>
        public string SigningKey { get; set; } = string.Empty;

        /// <summary>Claim type of the user id inside the token (JWT <c>sub</c>).</summary>
        public string UserIdClaimType { get; set; } = "sub";

        /// <summary>Claim type of the session id, both in the token and on the validated principal.</summary>
        public string SessionIdClaimType { get; set; } = "sid";

        /// <summary>Claim type used for permissions on the validated principal.</summary>
        public string PermissionClaimType { get; set; } = "permission";

        /// <summary>Lifetime of an access token.</summary>
        public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>
        /// Sliding lifetimes of a session / refresh token (normal and "remember me"): each refresh moves the expiry to
        /// now + the lifetime picked by <see cref="SlidingSessionPolicy"/>. Default: 30 days for both.
        /// </summary>
        public SessionLifetimes SessionLifetimes { get; set; } = new(TimeSpan.FromDays(30));

        /// <summary>
        /// Optional absolute cap counted from sign-in: sliding stops there and the user must sign in again.
        /// Null (default) = no cap.
        /// </summary>
        public TimeSpan? MaxSessionLifetime { get; set; }

        /// <summary>Allowed clock skew when validating token lifetime.</summary>
        public TimeSpan ClockSkew { get; set; } = TimeSpan.Zero;

        /// <summary>How long a session lookup is cached by <see cref="SessionAccessCache"/>.</summary>
        public TimeSpan SessionAccessCacheLifetime { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>The audience actually used.</summary>
        public string EffectiveAudience => string.IsNullOrEmpty(Audience) ? Issuer : Audience;

        /// <summary>Signing key built from <see cref="SigningKey"/>.</summary>
        public SymmetricSecurityKey CreateSigningKey() => new(Encoding.UTF8.GetBytes(SigningKey));

        private static readonly TimeSpan MaxLifetimeLimit = TimeSpan.FromDays(3650);

        /// <summary>Throws when the options cannot produce or validate tokens.</summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Issuer))
                throw new InvalidOperationException("ApiAuthOptions.Issuer is required.");
            if (Encoding.UTF8.GetByteCount(SigningKey ?? string.Empty) < 32)
                throw new InvalidOperationException("ApiAuthOptions.SigningKey must be at least 32 bytes.");
            if (AccessTokenLifetime <= TimeSpan.Zero)
                throw new InvalidOperationException("ApiAuthOptions.AccessTokenLifetime must be positive.");
            if (SessionLifetimes is null)
                throw new InvalidOperationException("ApiAuthOptions.SessionLifetimes is required.");
            if (SessionLifetimes.Normal <= TimeSpan.Zero)
                throw new InvalidOperationException("ApiAuthOptions.SessionLifetimes.Normal must be positive.");
            if (SessionLifetimes.Persistent is { } persistent && persistent <= TimeSpan.Zero)
                throw new InvalidOperationException("ApiAuthOptions.SessionLifetimes.Persistent must be positive when set.");
            if (MaxSessionLifetime is { } max && max <= TimeSpan.Zero)
                throw new InvalidOperationException("ApiAuthOptions.MaxSessionLifetime must be positive when set.");
            // Expiry arithmetic (now + lifetime) must not overflow DateTime.
            if (SessionLifetimes.Normal > MaxLifetimeLimit)
                throw new InvalidOperationException("ApiAuthOptions.SessionLifetimes.Normal must not exceed 3650 days.");
            if (SessionLifetimes.Persistent is { } persistentMax && persistentMax > MaxLifetimeLimit)
                throw new InvalidOperationException("ApiAuthOptions.SessionLifetimes.Persistent must not exceed 3650 days.");
            if (MaxSessionLifetime is { } maxLimit && maxLimit > MaxLifetimeLimit)
                throw new InvalidOperationException("ApiAuthOptions.MaxSessionLifetime must not exceed 3650 days.");
            if (string.IsNullOrEmpty(UserIdClaimType) || string.IsNullOrEmpty(SessionIdClaimType) || string.IsNullOrEmpty(PermissionClaimType))
                throw new InvalidOperationException("ApiAuthOptions claim types must not be empty.");
        }

        /// <summary>
        /// Validation parameters matching the tokens of <see cref="AccessTokenFactory"/>: issuer, audience, lifetime,
        /// signing key; name/role claim types are the standard <c>ClaimTypes</c>.
        /// </summary>
        public TokenValidationParameters CreateTokenValidationParameters() => new()
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = EffectiveAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = CreateSigningKey(),
            ClockSkew = ClockSkew,
            NameClaimType = System.Security.Claims.ClaimTypes.Name,
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
        };
    }
}
