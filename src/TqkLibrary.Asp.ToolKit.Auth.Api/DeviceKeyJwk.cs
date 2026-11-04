using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>
    /// Validation and canonical form of a device PUBLIC key in JWK form (RFC 7517/7518) for DBSC. Accepts only
    /// <c>ES256</c> (EC P-256, the point must be on the curve) and <c>RS256</c> (RSA, modulus 2048..8192 bits, exponent
    /// 65537). Any private member is rejected. The canonical form keeps only the required public members in lexicographic
    /// order (the RFC 7638 thumbprint input), so equal keys always store the same text.
    /// </summary>
    internal static class DeviceKeyJwk
    {
        public const string Es256 = "ES256";
        public const string Rs256 = "RS256";

        private const int MinRsaBits = 2048;
        private const int MaxRsaBits = 8192;

        private static readonly string[] PrivateMembers = ["d", "p", "q", "dp", "dq", "qi", "oth", "k"];

        private static readonly BigInteger P256Prime = BigInteger.Parse(
            "00FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF", NumberStyles.HexNumber);

        private static readonly BigInteger P256B = BigInteger.Parse(
            "005AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B", NumberStyles.HexNumber);

        /// <summary>
        /// True when <paramref name="jwk"/> is an acceptable public key for <paramref name="algorithm"/>;
        /// <paramref name="canonical"/> is then its canonical JSON.
        /// </summary>
        public static bool TryCanonicalize(string? jwk, string? algorithm, int maxLength, [NotNullWhen(true)] out string? canonical)
        {
            canonical = null;
            if (string.IsNullOrEmpty(jwk) || jwk.Length > maxLength)
                return false;
            if (algorithm is not (Es256 or Rs256))
                return false;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(jwk, new JsonDocumentOptions { MaxDepth = 4 });
            }
            catch (JsonException)
            {
                return false;
            }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return false;

                var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in root.EnumerateObject())
                {
                    // A duplicate member makes the key ambiguous (parsers disagree on which value wins).
                    if (!members.TryAdd(property.Name, property.Value))
                        return false;
                }
                if (PrivateMembers.Any(members.ContainsKey))
                    return false;
                if (members.TryGetValue("alg", out var alg) && !IsString(alg, algorithm))
                    return false;
                if (members.TryGetValue("use", out var use) && !IsString(use, "sig"))
                    return false;

                canonical = algorithm == Es256 ? CanonicalizeEc(members) : CanonicalizeRsa(members);
                return canonical is not null;
            }
        }

        private static string? CanonicalizeEc(Dictionary<string, JsonElement> members)
        {
            if (!TryGetString(members, "kty", out var kty) || kty != "EC"
                || !TryGetString(members, "crv", out var crv) || crv != "P-256"
                || !TryGetBase64Url(members, "x", out var x) || x.Length != 32
                || !TryGetBase64Url(members, "y", out var y) || y.Length != 32
                || !IsOnP256(x, y))
                return null;
            return "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" + Base64Url.EncodeToString(x)
                + "\",\"y\":\"" + Base64Url.EncodeToString(y) + "\"}";
        }

        private static string? CanonicalizeRsa(Dictionary<string, JsonElement> members)
        {
            if (!TryGetString(members, "kty", out var kty) || kty != "RSA"
                || !TryGetString(members, "e", out var e) || e != "AQAB"
                || !TryGetBase64Url(members, "n", out var n))
                return null;
            // Minimal octets (RFC 7518 6.3.1.1): drop leading zero bytes some encoders add.
            var start = 0;
            while (start < n.Length && n[start] == 0)
                start++;
            var modulus = n.AsSpan(start);
            if (modulus.IsEmpty || (modulus[^1] & 1) == 0)
                return null;
            var bits = (modulus.Length - 1) * 8 + (32 - BitOperations.LeadingZeroCount((uint)modulus[0]));
            if (bits < MinRsaBits || bits > MaxRsaBits)
                return null;
            return "{\"e\":\"AQAB\",\"kty\":\"RSA\",\"n\":\"" + Base64Url.EncodeToString(modulus) + "\"}";
        }

        /// <summary>y² = x³ − 3x + b (mod p) with 0 ≤ x, y &lt; p: rejects points an attacker crafted off the curve.</summary>
        private static bool IsOnP256(byte[] x, byte[] y)
        {
            var bx = new BigInteger(x, isUnsigned: true, isBigEndian: true);
            var by = new BigInteger(y, isUnsigned: true, isBigEndian: true);
            if (bx >= P256Prime || by >= P256Prime)
                return false;
            var left = BigInteger.ModPow(by, 2, P256Prime);
            var right = (BigInteger.ModPow(bx, 3, P256Prime) - 3 * bx + P256B) % P256Prime;
            if (right.Sign < 0)
                right += P256Prime;
            return left == right;
        }

        private static bool IsString(JsonElement element, string expected)
            => element.ValueKind == JsonValueKind.String && element.ValueEquals(expected);

        private static bool TryGetString(Dictionary<string, JsonElement> members, string name, [NotNullWhen(true)] out string? value)
        {
            value = members.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
            return value is not null;
        }

        /// <summary>Strict base64url: URL-safe alphabet only, no padding, canonical (re-encodes to the same text).</summary>
        private static bool TryGetBase64Url(Dictionary<string, JsonElement> members, string name, [NotNullWhen(true)] out byte[]? value)
        {
            value = null;
            if (!TryGetString(members, name, out var text) || text.Length == 0)
                return false;
            foreach (var c in text)
            {
                if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
                    return false;
            }
            byte[] bytes;
            try
            {
                bytes = Base64Url.DecodeFromChars(text);
            }
            catch (FormatException)
            {
                return false;
            }
            if (Base64Url.EncodeToString(bytes) != text)
                return false;
            value = bytes;
            return true;
        }
    }
}
