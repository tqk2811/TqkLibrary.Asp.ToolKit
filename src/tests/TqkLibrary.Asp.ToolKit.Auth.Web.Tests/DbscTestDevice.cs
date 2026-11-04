using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Tests
{
    /// <summary>
    /// A fake DBSC browser key (ECDsa P-256 for ES256 or RSA for RS256): exports its public JWK and crafts proof JWTs
    /// the way a browser does (compact JWS, typ "dbsc+jwt", jti = challenge).
    /// </summary>
    internal sealed class DbscTestDevice : IDisposable
    {
        private readonly ECDsa? _ec;
        private readonly RSA? _rsa;

        private DbscTestDevice(ECDsa? ec, RSA? rsa)
        {
            _ec = ec;
            _rsa = rsa;
        }

        public static DbscTestDevice Es256() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256), null);

        public static DbscTestDevice Rs256(int bits = 2048) => new(null, RSA.Create(bits));

        public string Algorithm => _ec is not null ? "ES256" : "RS256";

        /// <summary>Public JWK of the key.</summary>
        public JsonObject Jwk()
        {
            if (_ec is not null)
            {
                var p = _ec.ExportParameters(false);
                return new JsonObject { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = B64(p.Q.X!), ["y"] = B64(p.Q.Y!) };
            }
            var r = _rsa!.ExportParameters(false);
            return new JsonObject { ["kty"] = "RSA", ["n"] = B64(r.Modulus!), ["e"] = B64(r.Exponent!) };
        }

        /// <summary>Registration proof: key in header.jwk.</summary>
        public string RegistrationJwt(string challenge) => Jwt(Header(withJwk: true), Payload(challenge));

        /// <summary>Refresh proof: no key.</summary>
        public string RefreshJwt(string challenge) => Jwt(Header(withJwk: false), Payload(challenge));

        public JsonObject Header(bool withJwk, string? alg = null, string typ = "dbsc+jwt")
        {
            var header = new JsonObject { ["alg"] = alg ?? Algorithm, ["typ"] = typ };
            if (withJwk)
                header["jwk"] = Jwk();
            return header;
        }

        public static JsonObject Payload(string challenge) => new() { ["jti"] = challenge };

        /// <summary>Signs header.payload with this key (IEEE P1363 for EC unless <paramref name="derSignature"/>).</summary>
        public string Jwt(JsonObject header, JsonObject payload, bool derSignature = false)
        {
            var signingInput = B64(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + B64(Encoding.UTF8.GetBytes(payload.ToJsonString()));
            var data = Encoding.ASCII.GetBytes(signingInput);
            var signature = _ec is not null
                ? _ec.SignData(data, HashAlgorithmName.SHA256, derSignature ? DSASignatureFormat.Rfc3279DerSequence : DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                : _rsa!.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return signingInput + "." + B64(signature);
        }

        public static string B64(byte[] bytes) => Base64Url.EncodeToString(bytes);

        public void Dispose()
        {
            _ec?.Dispose();
            _rsa?.Dispose();
        }
    }
}
