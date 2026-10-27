using Microsoft.AspNetCore.WebUtilities;
using System.Security.Cryptography;
using System.Text;
using TqkLibrary.Asp.ToolKit.Auth.Api.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>
    /// Refresh token format <c>{sessionId}.{secret}</c>: the first part finds the session, the second is 32 random
    /// bytes. Only the SHA-256 of the secret is stored, so a leaked session table does not leak usable tokens.
    /// The token is NOT single-use: refresh returns the same token and only slides the session expiry, so concurrent
    /// requests that refresh with it all succeed.
    /// </summary>
    public static class RefreshTokenCodec
    {
        /// <summary>Creates a new token for <paramref name="sessionId"/> (which must not contain '.').</summary>
        public static IssuedRefreshToken Create(string sessionId)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            if (sessionId.Contains('.'))
                throw new ArgumentException("Session id must not contain '.'.", nameof(sessionId));
            var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            return new IssuedRefreshToken($"{sessionId}.{secret}", Hash(secret));
        }

        /// <summary>Splits a token; false when it is not in the expected format.</summary>
        public static bool TryParse(string? token, out string sessionId, out string secret)
        {
            sessionId = secret = string.Empty;
            if (string.IsNullOrWhiteSpace(token))
                return false;
            var dot = token.IndexOf('.');
            if (dot <= 0 || dot == token.Length - 1)
                return false;
            sessionId = token[..dot];
            secret = token[(dot + 1)..];
            return true;
        }

        /// <summary>Uppercase hex SHA-256 of <paramref name="secret"/>.</summary>
        public static string Hash(string secret)
        {
            ArgumentNullException.ThrowIfNull(secret);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        }

        /// <summary>Compares two hashes in time independent of the first differing character.</summary>
        public static bool HashEquals(string? left, string? right)
            => left is not null && right is not null
               && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

        /// <summary>True when <paramref name="secret"/> hashes to <paramref name="storedHash"/>.</summary>
        public static bool Verify(string secret, string? storedHash) => HashEquals(Hash(secret), storedHash);
    }
}
