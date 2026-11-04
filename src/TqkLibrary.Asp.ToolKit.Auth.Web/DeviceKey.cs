using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// A validated device public key of a DBSC session: EC P-256 for <c>ES256</c> or RSA (modulus of at least the
    /// configured size, exponent 65537) for <c>RS256</c>. Private members are refused. <see cref="Jwk"/> is the canonical
    /// public JWK (only the required members, ordered), stored in the cookie ticket and sent to the API.
    /// </summary>
    internal sealed class DeviceKey
    {
        /// <summary>Largest accepted RSA modulus in bits (bounds the verification cost).</summary>
        internal const int MaxRsaKeySize = 8192;

        private static readonly byte[] Rsa65537 = [0x01, 0x00, 0x01];
        private static readonly string[] PrivateMembers = ["d", "p", "q", "dp", "dq", "qi", "oth", "k"];

        private readonly ECParameters? _ec;
        private readonly RSAParameters? _rsa;

        private DeviceKey(string algorithm, string jwk, ECParameters? ec, RSAParameters? rsa)
        {
            Algorithm = algorithm;
            Jwk = jwk;
            _ec = ec;
            _rsa = rsa;
        }

        /// <summary><c>ES256</c> or <c>RS256</c>.</summary>
        public string Algorithm { get; }

        /// <summary>Canonical public JWK JSON.</summary>
        public string Jwk { get; }

        /// <summary>Parses a JWK JSON text (as stored in the ticket).</summary>
        public static DeviceKey? TryParse(string? jwkJson, string? algorithm, int minRsaKeySize)
        {
            if (string.IsNullOrEmpty(jwkJson) || string.IsNullOrEmpty(algorithm) || jwkJson.Length > 4096)
                return null;
            try
            {
                using var document = JsonDocument.Parse(jwkJson, new JsonDocumentOptions { MaxDepth = 4 });
                return TryParse(document.RootElement, algorithm, minRsaKeySize);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Validates a JWK element for <paramref name="algorithm"/>: right key type for the algorithm, EC P-256 with
        /// 32-byte coordinates on the curve, or RSA with e = 65537 and a modulus between <paramref name="minRsaKeySize"/>
        /// and <see cref="MaxRsaKeySize"/> bits; no private member. Null when invalid.
        /// </summary>
        public static DeviceKey? TryParse(JsonElement jwk, string algorithm, int minRsaKeySize)
        {
            if (jwk.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var member in PrivateMembers)
            {
                if (jwk.TryGetProperty(member, out _))
                    return null;
            }
            var kty = GetString(jwk, "kty");
            return algorithm switch
            {
                "ES256" when kty == "EC" => TryParseEc(jwk),
                "RS256" when kty == "RSA" => TryParseRsa(jwk, minRsaKeySize),
                _ => null,
            };
        }

        private static DeviceKey? TryParseEc(JsonElement jwk)
        {
            if (GetString(jwk, "crv") != "P-256")
                return null;
            var x = DecodeMember(jwk, "x");
            var y = DecodeMember(jwk, "y");
            if (x is not { Length: 32 } || y is not { Length: 32 })
                return null;
            var parameters = new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } };
            try
            {
                // Import validates that the point is on the curve (Windows CNG reports an off-curve point as
                // PlatformNotSupportedException).
                using var ecdsa = ECDsa.Create(parameters);
            }
            catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
            {
                return null;
            }
            var canonical = new JsonObject
            {
                ["crv"] = "P-256",
                ["kty"] = "EC",
                ["x"] = Base64Url.EncodeToString(x),
                ["y"] = Base64Url.EncodeToString(y),
            }.ToJsonString();
            return new DeviceKey("ES256", canonical, parameters, null);
        }

        private static DeviceKey? TryParseRsa(JsonElement jwk, int minRsaKeySize)
        {
            var n = DecodeMember(jwk, "n");
            var e = DecodeMember(jwk, "e");
            if (n is null || e is null || !e.AsSpan().SequenceEqual(Rsa65537))
                return null;
            var start = 0;
            while (start < n.Length && n[start] == 0)
                start++;
            n = n[start..];
            if (n.Length == 0)
                return null;
            var bits = n.Length * 8 - System.Numerics.BitOperations.LeadingZeroCount((uint)n[0]) + 24;
            if (bits < minRsaKeySize || bits > MaxRsaKeySize)
                return null;
            var parameters = new RSAParameters { Modulus = n, Exponent = e };
            try
            {
                using var rsa = RSA.Create(parameters);
            }
            catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
            {
                return null;
            }
            var canonical = new JsonObject
            {
                ["e"] = Base64Url.EncodeToString(e),
                ["kty"] = "RSA",
                ["n"] = Base64Url.EncodeToString(n),
            }.ToJsonString();
            return new DeviceKey("RS256", canonical, null, parameters);
        }

        /// <summary>
        /// Verifies a JWS signature over <paramref name="data"/>: ES256 = 64-byte IEEE P1363 r||s (DER is refused),
        /// RS256 = PKCS#1 v1.5, both SHA-256.
        /// </summary>
        public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
        {
            try
            {
                if (_ec is { } ec)
                {
                    if (signature.Length != 64)
                        return false;
                    using var ecdsa = ECDsa.Create(ec);
                    return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                }
                if (_rsa is { } rsaParameters)
                {
                    using var rsa = RSA.Create(rsaParameters);
                    return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            }
            catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
            {
            }
            return false;
        }

        private static string? GetString(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static byte[]? DecodeMember(JsonElement element, string name)
        {
            var text = GetString(element, name);
            if (string.IsNullOrEmpty(text) || text.Length > 2048)
                return null;
            try
            {
                return Base64Url.DecodeFromChars(text);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
