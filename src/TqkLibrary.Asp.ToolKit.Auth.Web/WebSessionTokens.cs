using Microsoft.AspNetCore.Authentication;
using System.Globalization;
using TqkLibrary.Asp.ToolKit.Auth.Web.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// The API tokens of one web session, as stored in the cookie ticket (<see cref="AuthenticationProperties"/>,
    /// encrypted by Data Protection so the browser cannot read them). Nothing is stored server side. Times are UTC.
    /// </summary>
    public sealed record WebSessionTokens(
        string AccessToken,
        DateTime AccessTokenExpiresAt,
        string RefreshToken,
        DateTime RefreshTokenExpiresAt)
    {
        /// <summary>Copies the token part of <paramref name="tokens"/>.</summary>
        public static WebSessionTokens From(AuthTokenPair tokens)
        {
            ArgumentNullException.ThrowIfNull(tokens);
            return new(tokens.AccessToken, tokens.AccessTokenExpiresAt, tokens.RefreshToken, tokens.RefreshTokenExpiresAt);
        }

        /// <summary>Reads the tokens from a ticket; null when any part is missing or malformed (session considered lost).</summary>
        public static WebSessionTokens? Read(AuthenticationProperties properties, WebAuthOptions options)
        {
            ArgumentNullException.ThrowIfNull(properties);
            ArgumentNullException.ThrowIfNull(options);
            var access = properties.GetTokenValue(options.AccessTokenName);
            var refresh = properties.GetTokenValue(options.RefreshTokenName);
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh)
                || !TryParseUtc(properties.GetTokenValue(options.AccessTokenExpiresName), out var accessExpires)
                || !TryParseUtc(properties.GetTokenValue(options.RefreshTokenExpiresName), out var refreshExpires))
                return null;
            return new WebSessionTokens(access, accessExpires, refresh, refreshExpires);
        }

        /// <summary>Writes (replaces) the tokens in a ticket; used on sign-in and after every refresh.</summary>
        public void WriteTo(AuthenticationProperties properties, WebAuthOptions options)
        {
            ArgumentNullException.ThrowIfNull(properties);
            ArgumentNullException.ThrowIfNull(options);
            properties.StoreTokens(
            [
                new AuthenticationToken { Name = options.AccessTokenName, Value = AccessToken },
                new AuthenticationToken { Name = options.AccessTokenExpiresName, Value = FormatUtc(AccessTokenExpiresAt) },
                new AuthenticationToken { Name = options.RefreshTokenName, Value = RefreshToken },
                new AuthenticationToken { Name = options.RefreshTokenExpiresName, Value = FormatUtc(RefreshTokenExpiresAt) },
            ]);
        }

        /// <summary>True when the access token expires within <paramref name="skew"/>: refresh first instead of paying a 401.</summary>
        public bool IsAccessTokenExpiring(DateTime utcNow, TimeSpan skew) => AccessTokenExpiresAt - utcNow <= skew;

        /// <summary>Refresh token expiry as an offset (UTC).</summary>
        public DateTimeOffset RefreshTokenExpiresAtOffset
            => new(DateTime.SpecifyKind(RefreshTokenExpiresAt, DateTimeKind.Utc));

        // Round-trip "o", always UTC: parsing does not depend on the machine time zone or culture.
        private static string FormatUtc(DateTime value)
            => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);

        private static bool TryParseUtc(string? value, out DateTime result)
        {
            var ok = DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out result);
            result = DateTime.SpecifyKind(result, DateTimeKind.Utc);
            return ok;
        }
    }
}
