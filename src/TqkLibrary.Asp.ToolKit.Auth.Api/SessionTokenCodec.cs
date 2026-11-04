using Microsoft.AspNetCore.WebUtilities;
using System.Security.Cryptography;
using System.Text;
using TqkLibrary.Asp.ToolKit.Auth.Api.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>
    /// Opaque session token format <c>{sessionId}.{secret}</c>: the first part finds the session, the second is 32
    /// random bytes (base64url). Only the SHA-256 of the secret is stored, so a leaked session table does not leak usable
    /// tokens. The token is issued once at sign-in and never rotated: every request of the session carries the same
    /// token, so concurrent requests need no coordination. Never log it.
    /// </summary>
    public static class SessionTokenCodec
    {
        /// <summary>Longest token <see cref="TryParse"/> accepts; longer values are rejected before hashing.</summary>
        public const int MaxTokenLength = 1024;

        /// <summary>Longest session id <see cref="Create"/> and <see cref="TryParse"/> accept.</summary>
        public const int MaxSessionIdLength = 128;

        /// <summary>Length of a secret: 32 random bytes as unpadded base64url.</summary>
        public const int SecretLength = 43;

        private const int SecretByteLength = 32;

        /// <summary>
        /// Hash that no secret produces; the handler verifies against it when the session does not exist, so an unknown
        /// session id costs the same time as a wrong secret.
        /// </summary>
        internal static readonly string DummyHash = Hash("dummy-secret-for-timing-equalisation");

        /// <summary>
        /// Creates a new token for <paramref name="sessionId"/>, which must be 1 to <see cref="MaxSessionIdLength"/>
        /// characters of <c>A-Z a-z 0-9 - _</c> (a GUID or base64url id), otherwise <see cref="TryParse"/> would reject its own token.
        /// The id MUST be unguessable (random GUID or random bytes), never a sequential or predictable id.
        /// </summary>
        public static IssuedSessionToken Create(string sessionId)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            if (!IsValidSessionId(sessionId))
                throw new ArgumentException($"Session id must be 1 to {MaxSessionIdLength} characters of A-Z a-z 0-9 - _.", nameof(sessionId));
            var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(SecretByteLength));
            return new IssuedSessionToken($"{sessionId}.{secret}", Hash(secret));
        }

        /// <summary>
        /// Splits a token at its first '.'; false when it is not in the expected format: a session id of up to
        /// <see cref="MaxSessionIdLength"/> characters of <c>A-Z a-z 0-9 - _</c> and a secret of exactly
        /// <see cref="SecretLength"/> base64url characters (what <see cref="Create"/> issues).
        /// </summary>
        public static bool TryParse(string? token, out string sessionId, out string secret)
        {
            sessionId = secret = string.Empty;
            if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
                return false;
            var dot = token.IndexOf('.');
            if (dot <= 0 || dot == token.Length - 1)
                return false;
            sessionId = token[..dot];
            secret = token[(dot + 1)..];
            if (!IsValidSessionId(sessionId) || secret.Length != SecretLength || !secret.All(IsBase64UrlChar))
            {
                sessionId = secret = string.Empty;
                return false;
            }
            return true;
        }

        private static bool IsValidSessionId(string sessionId)
            => sessionId.Length is > 0 and <= MaxSessionIdLength && sessionId.All(IsBase64UrlChar);

        private static bool IsBase64UrlChar(char c)
            => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';

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

        /// <summary>True when <paramref name="secret"/> hashes to <paramref name="storedHash"/> (fixed-time compare).</summary>
        public static bool Verify(string secret, string? storedHash) => HashEquals(Hash(secret), storedHash);
    }
}
