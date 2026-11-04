using Microsoft.Extensions.Options;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Verifies the DBSC proof JWT (<c>Secure-Session-Response</c>): compact JWS, header <c>typ = "dbsc+jwt"</c>,
    /// <c>alg</c> <c>ES256</c> or <c>RS256</c> (<c>none</c>, HMAC and the rest are refused), payload <c>jti</c> = the
    /// challenge. <c>iat</c>/<c>aud</c> are not required (the stateless challenge bounds the proof in time). At
    /// registration <c>alg</c> must be in <see cref="DeviceBoundSessionOptions.Algorithms"/> and the public key is REQUIRED,
    /// read from <c>header.jwk</c>, else <c>payload.jwk</c>, else <c>payload.key</c> (browser versions differ); at refresh
    /// <c>header.jwk</c>, <c>payload.jwk</c> and <c>payload.key</c> must be absent, the stored key is used and <c>alg</c>
    /// must be the stored key's algorithm (whatever the configured list says now).
    /// </summary>
    internal sealed class DeviceProofJwtVerifier
    {
        /// <summary>Largest accepted JWT (an RSA 8192 JWK plus a signature fits easily).</summary>
        internal const int MaxLength = 8192;
        private const int MaxJtiLength = 2048;
        private const string DbscType = "dbsc+jwt";

        private readonly IOptions<WebAuthOptions> _options;

        public DeviceProofJwtVerifier(IOptions<WebAuthOptions> options) => _options = options;

        /// <summary>Verifies a registration proof, signed by the key it carries.</summary>
        public DeviceProofJwt VerifyRegistration(string? jwt) => Verify(jwt, storedKey: null);

        /// <summary>Verifies a refresh proof, signed by <paramref name="storedKey"/>.</summary>
        public DeviceProofJwt VerifyRefresh(string? jwt, DeviceKey storedKey)
        {
            ArgumentNullException.ThrowIfNull(storedKey);
            return Verify(jwt, storedKey);
        }

        private DeviceProofJwt Verify(string? jwt, DeviceKey? storedKey)
        {
            if (string.IsNullOrEmpty(jwt))
                return new(DeviceProofJwtError.Malformed);
            if (jwt.Length > MaxLength)
                return new(DeviceProofJwtError.TooLarge);
            var parts = jwt.Split('.');
            if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
                return new(DeviceProofJwtError.Malformed);

            var signature = Decode(parts[2]);
            if (signature is null)
                return new(DeviceProofJwtError.Malformed);
            using var header = ParseJson(parts[0]);
            using var payload = ParseJson(parts[1]);
            if (header is null || payload is null)
                return new(DeviceProofJwtError.Malformed);
            var h = header.RootElement;
            var p = payload.RootElement;

            if (GetString(h, "typ") != DbscType)
                return new(DeviceProofJwtError.WrongType);
            var algorithm = GetString(h, "alg");
            var options = _options.Value.DeviceBound;
            if (algorithm is null || !DeviceBoundSessionOptions.SupportedAlgorithms.Contains(algorithm))
                return new(DeviceProofJwtError.AlgorithmNotAllowed);

            DeviceKey key;
            if (storedKey is null)
            {
                // Registration: only the algorithms the project currently accepts.
                if (!options.Algorithms.Contains(algorithm))
                    return new(DeviceProofJwtError.AlgorithmNotAllowed);
                JsonElement jwk;
                if (!h.TryGetProperty("jwk", out jwk) && !p.TryGetProperty("jwk", out jwk) && !p.TryGetProperty("key", out jwk))
                    return new(DeviceProofJwtError.MissingKey);
                var parsed = DeviceKey.TryParse(jwk, algorithm, options.MinRsaKeySize);
                if (parsed is null)
                    return new(DeviceProofJwtError.InvalidKey);
                key = parsed;
            }
            else
            {
                if (h.TryGetProperty("jwk", out _) || p.TryGetProperty("jwk", out _) || p.TryGetProperty("key", out _))
                    return new(DeviceProofJwtError.KeyNotAllowed);
                // Refresh: the algorithm of the STORED key, not the configured list, so removing an algorithm from
                // DeviceBoundSessionOptions.Algorithms only stops new registrations and never kills bound sessions.
                if (algorithm != storedKey.Algorithm)
                    return new(DeviceProofJwtError.AlgorithmMismatch);
                key = storedKey;
            }

            var jti = GetString(p, "jti");
            if (string.IsNullOrEmpty(jti) || jti.Length > MaxJtiLength)
                return new(DeviceProofJwtError.MissingJti);

            var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (!key.Verify(signingInput, signature))
                return new(DeviceProofJwtError.BadSignature);
            return new(DeviceProofJwtError.None, jti, key, GetString(p, "authorization"));
        }

        private static byte[]? Decode(string part)
        {
            try
            {
                return Base64Url.DecodeFromChars(part);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static JsonDocument? ParseJson(string part)
        {
            var bytes = Decode(part);
            if (bytes is null)
                return null;
            try
            {
                var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    return document;
                document.Dispose();
            }
            catch (JsonException)
            {
            }
            return null;
        }

        private static string? GetString(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
