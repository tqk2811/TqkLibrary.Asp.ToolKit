using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Tests
{
    /// <summary>DBSC building blocks: device key, proof JWT verifier, stateless challenges, proof cookie, options.</summary>
    public class DeviceBoundPrimitivesTests
    {
        private const string Sid = "s-1";

        private static DeviceProofJwtVerifier Verifier(Action<DeviceBoundSessionOptions>? configure = null)
        {
            var options = new WebAuthOptions();
            configure?.Invoke(options.DeviceBound);
            return new DeviceProofJwtVerifier(Options.Create(options));
        }

        // ---------- verifier: registration ----------

        [Fact]
        public void Registration_ValidEs256_ReturnsJtiAndCanonicalKey()
        {
            using var device = DbscTestDevice.Es256();

            var result = Verifier().VerifyRegistration(device.RegistrationJwt("chal-1"));

            Assert.True(result.IsValid, result.Error.ToString());
            Assert.Equal("chal-1", result.Jti);
            Assert.Equal("ES256", result.Key!.Algorithm);
            var jwk = JsonNode.Parse(result.Key.Jwk)!.AsObject();
            Assert.Equal(["crv", "kty", "x", "y"], jwk.Select(p => p.Key));
            Assert.Equal(device.Jwk()["x"]!.GetValue<string>(), jwk["x"]!.GetValue<string>());
        }

        [Fact]
        public void Registration_ValidRs256_ReturnsKey()
        {
            using var device = DbscTestDevice.Rs256();

            var result = Verifier().VerifyRegistration(device.RegistrationJwt("chal-1"));

            Assert.True(result.IsValid, result.Error.ToString());
            Assert.Equal("RS256", result.Key!.Algorithm);
            Assert.Equal(["e", "kty", "n"], JsonNode.Parse(result.Key.Jwk)!.AsObject().Select(p => p.Key));
        }

        [Fact]
        public void Registration_SignedByAnotherKey_BadSignature()
        {
            using var device = DbscTestDevice.Es256();
            using var attacker = DbscTestDevice.Es256();

            var jwt = attacker.Jwt(device.Header(withJwk: true), DbscTestDevice.Payload("c"));

            Assert.Equal(DeviceProofJwtError.BadSignature, Verifier().VerifyRegistration(jwt).Error);
        }

        [Fact]
        public void Registration_TamperedPayload_BadSignature()
        {
            using var device = DbscTestDevice.Es256();
            var parts = device.RegistrationJwt("c").Split('.');
            parts[1] = DbscTestDevice.B64(System.Text.Encoding.UTF8.GetBytes("{\"jti\":\"other\"}"));

            Assert.Equal(DeviceProofJwtError.BadSignature, Verifier().VerifyRegistration(string.Join('.', parts)).Error);
        }

        [Theory]
        [InlineData("none")]
        [InlineData("HS256")]
        [InlineData("ES384")]
        [InlineData("es256")]
        public void Registration_AlgorithmOutsideEs256Rs256_Refused(string alg)
        {
            using var device = DbscTestDevice.Es256();

            var jwt = device.Jwt(device.Header(withJwk: true, alg: alg), DbscTestDevice.Payload("c"));

            Assert.Equal(DeviceProofJwtError.AlgorithmNotAllowed, Verifier().VerifyRegistration(jwt).Error);
        }

        [Fact]
        public void Registration_AlgorithmNotConfigured_Refused()
        {
            using var device = DbscTestDevice.Rs256();

            var result = Verifier(o => o.Algorithms = ["ES256"]).VerifyRegistration(device.RegistrationJwt("c"));

            Assert.Equal(DeviceProofJwtError.AlgorithmNotAllowed, result.Error);
        }

        [Theory]
        [InlineData("JWT")]
        [InlineData("DBSC+JWT")]
        [InlineData("")]
        public void Registration_WrongTyp_Refused(string typ)
        {
            using var device = DbscTestDevice.Es256();

            var jwt = device.Jwt(device.Header(withJwk: true, typ: typ), DbscTestDevice.Payload("c"));

            Assert.Equal(DeviceProofJwtError.WrongType, Verifier().VerifyRegistration(jwt).Error);
        }

        [Fact]
        public void Registration_DerEncodedEcSignature_Refused()
        {
            using var device = DbscTestDevice.Es256();

            var jwt = device.Jwt(device.Header(withJwk: true), DbscTestDevice.Payload("c"), derSignature: true);

            Assert.Equal(DeviceProofJwtError.BadSignature, Verifier().VerifyRegistration(jwt).Error);
        }

        public static TheoryData<string> BadEcJwks => new()
        {
            "d",            // private key member
            "crv",          // wrong curve
            "shortX",       // 31-byte coordinate
            "offCurve",     // point not on the curve
            "kty",          // RSA key type for ES256
        };

        [Theory]
        [MemberData(nameof(BadEcJwks))]
        public void Registration_InvalidEcJwk_Refused(string defect)
        {
            using var device = DbscTestDevice.Es256();
            var header = device.Header(withJwk: true);
            var jwk = header["jwk"]!.AsObject();
            switch (defect)
            {
                case "d": jwk["d"] = DbscTestDevice.B64(new byte[32]); break;
                case "crv": jwk["crv"] = "P-384"; break;
                case "shortX": jwk["x"] = DbscTestDevice.B64(new byte[31]); break;
                case "offCurve": jwk["x"] = DbscTestDevice.B64(Enumerable.Repeat((byte)1, 32).ToArray()); jwk["y"] = DbscTestDevice.B64(Enumerable.Repeat((byte)2, 32).ToArray()); break;
                case "kty": jwk["kty"] = "RSA"; break;
            }

            var result = Verifier().VerifyRegistration(device.Jwt(header, DbscTestDevice.Payload("c")));

            Assert.Equal(DeviceProofJwtError.InvalidKey, result.Error);
        }

        [Fact]
        public void Registration_Rsa1024_Refused()
        {
            using var device = DbscTestDevice.Rs256(1024);

            Assert.Equal(DeviceProofJwtError.InvalidKey, Verifier().VerifyRegistration(device.RegistrationJwt("c")).Error);
        }

        [Fact]
        public void Registration_RsaBelowConfiguredMinimum_Refused()
        {
            using var device = DbscTestDevice.Rs256(2048);

            var result = Verifier(o => o.MinRsaKeySize = 3072).VerifyRegistration(device.RegistrationJwt("c"));

            Assert.Equal(DeviceProofJwtError.InvalidKey, result.Error);
        }

        [Fact]
        public void Registration_RsaExponentNot65537_Refused()
        {
            using var device = DbscTestDevice.Rs256();
            var header = device.Header(withJwk: true);
            header["jwk"]!["e"] = DbscTestDevice.B64([3]);

            Assert.Equal(DeviceProofJwtError.InvalidKey, Verifier().VerifyRegistration(device.Jwt(header, DbscTestDevice.Payload("c"))).Error);
        }

        [Theory]
        [InlineData("jwk")]
        [InlineData("key")]
        public void Registration_KeyInPayload_Accepted(string member)
        {
            using var device = DbscTestDevice.Es256();
            var payload = DbscTestDevice.Payload("c");
            payload[member] = device.Jwk();

            var result = Verifier().VerifyRegistration(device.Jwt(device.Header(withJwk: false), payload));

            Assert.True(result.IsValid, result.Error.ToString());
        }

        [Fact]
        public void Registration_NoKey_Refused()
        {
            using var device = DbscTestDevice.Es256();

            Assert.Equal(DeviceProofJwtError.MissingKey, Verifier().VerifyRegistration(device.RefreshJwt("c")).Error);
        }

        [Fact]
        public void Registration_NoJti_Refused()
        {
            using var device = DbscTestDevice.Es256();

            var result = Verifier().VerifyRegistration(device.Jwt(device.Header(withJwk: true), new JsonObject { ["iat"] = 1 }));

            Assert.Equal(DeviceProofJwtError.MissingJti, result.Error);
        }

        [Fact]
        public void Registration_Oversize_Refused()
        {
            using var device = DbscTestDevice.Es256();
            var payload = DbscTestDevice.Payload("c");
            payload["pad"] = new string('a', DeviceProofJwtVerifier.MaxLength);

            Assert.Equal(DeviceProofJwtError.TooLarge, Verifier().VerifyRegistration(device.Jwt(device.Header(true), payload)).Error);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("a.b")]
        [InlineData("a.b.c.d")]
        [InlineData("!!.@@.##")]
        [InlineData("W10.W10.AA")] // header and payload are JSON arrays
        public void Registration_Malformed_Refused(string? jwt)
            => Assert.Equal(DeviceProofJwtError.Malformed, Verifier().VerifyRegistration(jwt).Error);

        // ---------- verifier: refresh ----------

        private static DeviceKey StoredKey(DbscTestDevice device)
            => Verifier().VerifyRegistration(device.RegistrationJwt("reg")).Key!;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Refresh_SignedByStoredKey_Valid(bool rsa)
        {
            using var device = rsa ? DbscTestDevice.Rs256() : DbscTestDevice.Es256();

            var result = Verifier().VerifyRefresh(device.RefreshJwt("chal-2"), StoredKey(device));

            Assert.True(result.IsValid, result.Error.ToString());
            Assert.Equal("chal-2", result.Jti);
        }

        [Fact]
        public void Refresh_SignedByOtherKey_BadSignature()
        {
            using var device = DbscTestDevice.Es256();
            using var other = DbscTestDevice.Es256();

            Assert.Equal(DeviceProofJwtError.BadSignature, Verifier().VerifyRefresh(other.RefreshJwt("c"), StoredKey(device)).Error);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Refresh_WithJwk_Refused(bool inHeader)
        {
            using var device = DbscTestDevice.Es256();
            var payload = DbscTestDevice.Payload("c");
            if (!inHeader)
                payload["jwk"] = device.Jwk();

            var result = Verifier().VerifyRefresh(device.Jwt(device.Header(withJwk: inHeader), payload), StoredKey(device));

            Assert.Equal(DeviceProofJwtError.KeyNotAllowed, result.Error);
        }

        [Fact]
        public void Refresh_AlgorithmDiffersFromStoredKey_Refused()
        {
            using var device = DbscTestDevice.Es256();
            using var rsa = DbscTestDevice.Rs256();

            Assert.Equal(DeviceProofJwtError.AlgorithmMismatch, Verifier().VerifyRefresh(rsa.RefreshJwt("c"), StoredKey(device)).Error);
        }

        [Fact]
        public void DeviceKey_StoredJwk_RoundTrips()
        {
            using var device = DbscTestDevice.Es256();
            var key = StoredKey(device);

            var parsed = DeviceKey.TryParse(key.Jwk, key.Algorithm, 2048);

            Assert.NotNull(parsed);
            Assert.True(Verifier().VerifyRefresh(device.RefreshJwt("c"), parsed!).IsValid);
            Assert.Null(DeviceKey.TryParse(key.Jwk, "RS256", 2048));
            Assert.Null(DeviceKey.TryParse("not json", "ES256", 2048));
        }

        // ---------- challenges ----------

        private static (DeviceChallengeService Service, FakeTimeProvider Clock) Challenges(IDataProtectionProvider? keys = null)
        {
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var service = new DeviceChallengeService(keys ?? new EphemeralDataProtectionProvider(),
                new MemoryCache(new MemoryCacheOptions()), clock, Options.Create(new WebAuthOptions()));
            return (service, clock);
        }

        [Fact]
        public void Challenge_Valid_ThenReplayed()
        {
            var (service, _) = Challenges();
            var challenge = service.Issue(DeviceChallengeKind.Refresh, Sid);

            Assert.Equal(DeviceChallengeStatus.Valid, service.Validate(challenge, DeviceChallengeKind.Refresh, Sid, consume: false));
            Assert.Equal(DeviceChallengeStatus.Valid, service.Validate(challenge, DeviceChallengeKind.Refresh, Sid, consume: true));
            Assert.Equal(DeviceChallengeStatus.Replayed, service.Validate(challenge, DeviceChallengeKind.Refresh, Sid, consume: true));
        }

        [Fact]
        public void Challenge_EachIssueIsUnique()
        {
            var (service, _) = Challenges();

            Assert.NotEqual(service.Issue(DeviceChallengeKind.Refresh, Sid), service.Issue(DeviceChallengeKind.Refresh, Sid));
        }

        [Theory]
        [InlineData(true, 5)]
        [InlineData(false, 2)]
        public void Challenge_ExpiresAfterItsLifetime(bool registration, int minutes)
        {
            var kind = registration ? DeviceChallengeKind.Registration : DeviceChallengeKind.Refresh;
            var (service, clock) = Challenges();
            var challenge = service.Issue(kind, Sid);

            clock.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1));
            Assert.Equal(DeviceChallengeStatus.Valid, service.Validate(challenge, kind, Sid, consume: false));
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(DeviceChallengeStatus.Expired, service.Validate(challenge, kind, Sid, consume: true));
        }

        [Fact]
        public void Challenge_WrongKindOrSession_Invalid()
        {
            var (service, _) = Challenges();
            var registration = service.Issue(DeviceChallengeKind.Registration, Sid);

            Assert.Equal(DeviceChallengeStatus.Invalid, service.Validate(registration, DeviceChallengeKind.Refresh, Sid, consume: true));
            Assert.Equal(DeviceChallengeStatus.Invalid, service.Validate(registration, DeviceChallengeKind.Registration, "s-2", consume: true));
            // Neither attempt consumed it.
            Assert.Equal(DeviceChallengeStatus.Valid, service.Validate(registration, DeviceChallengeKind.Registration, Sid, consume: true));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("garbage")]
        [InlineData("!!!")]
        public void Challenge_Garbage_Invalid(string? challenge)
        {
            var (service, _) = Challenges();

            Assert.Equal(DeviceChallengeStatus.Invalid, service.Validate(challenge, DeviceChallengeKind.Refresh, Sid, consume: true));
        }

        [Fact]
        public void Challenge_FromAnotherKeyRing_Invalid()
        {
            var (issuer, _) = Challenges();
            var (validator, _) = Challenges();

            var challenge = issuer.Issue(DeviceChallengeKind.Refresh, Sid);

            Assert.Equal(DeviceChallengeStatus.Invalid, validator.Validate(challenge, DeviceChallengeKind.Refresh, Sid, consume: true));
        }

        // ---------- proof cookie ----------

        private static (DeviceProofCookie Cookie, FakeTimeProvider Clock) ProofCookie(Action<DeviceBoundSessionOptions>? configure = null)
        {
            var options = new WebAuthOptions();
            configure?.Invoke(options.DeviceBound);
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var cookie = new DeviceProofCookie(new EphemeralDataProtectionProvider(), clock, Options.Create(options),
                new FixedOptionsMonitor<CookieAuthenticationOptions>(new CookieAuthenticationOptions { Cookie = { Name = "app" } }));
            return (cookie, clock);
        }

        [Fact]
        public void Proof_NameDerivedFromMainCookie_UnlessConfigured()
        {
            Assert.Equal("app.proof", ProofCookie().Cookie.Name);
            Assert.Equal("p", ProofCookie(o => o.ProofCookieName = "p").Cookie.Name);
        }

        [Fact]
        public void Proof_Valid_ForItsSession_Only()
        {
            var (cookie, _) = ProofCookie();
            var value = cookie.CreateValue(Sid);

            Assert.Null(cookie.Check(value, Sid));
            Assert.Equal(DeviceProofFailureReason.WrongSession, cookie.Check(value, "s-2"));
            Assert.Equal(DeviceProofFailureReason.Missing, cookie.Check(null, Sid));
            Assert.Equal(DeviceProofFailureReason.Invalid, cookie.Check("garbage", Sid));
            Assert.Equal(DeviceProofFailureReason.Invalid, cookie.Check(value[..^4] + "AAAA", Sid));
        }

        [Fact]
        public void Proof_ExpiredWithinGrace_StillValid_ThenExpired()
        {
            var (cookie, clock) = ProofCookie();
            var value = cookie.CreateValue(Sid);

            clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
            Assert.Null(cookie.Check(value, Sid));
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(DeviceProofFailureReason.Expired, cookie.Check(value, Sid));
        }

        [Fact]
        public void Proof_ZeroGrace_ExpiresExactlyAtLifetime()
        {
            var (cookie, clock) = ProofCookie(o => o.ProofExpiryGrace = TimeSpan.Zero);
            var value = cookie.CreateValue(Sid);

            clock.Advance(TimeSpan.FromMinutes(10));

            Assert.Equal(DeviceProofFailureReason.Expired, cookie.Check(value, Sid));
        }

        // ---------- options ----------

        [Fact]
        public void Options_Defaults_AreValid()
        {
            var options = new DeviceBoundSessionOptions();

            options.Validate();
            Assert.False(options.Enabled);
            Assert.False(options.AllowUserChoice);
            Assert.Equal(DeviceProofFailureMode.Reject, options.FailureMode);
            Assert.Equal(3, options.AllowedUserModes.Count);
        }

        public static TheoryData<string> InvalidOptions => new()
        {
            "lifetime", "alg", "noAlg", "rsa", "path", "samePaths", "grace", "cookieName", "excluded", "challenge",
        };

        [Theory]
        [MemberData(nameof(InvalidOptions))]
        public void Options_Invalid_Throws(string defect)
        {
            var options = new DeviceBoundSessionOptions();
            switch (defect)
            {
                case "lifetime": options.ProofLifetime = TimeSpan.FromSeconds(59); break;
                case "alg": options.Algorithms = ["ES256", "HS256"]; break;
                case "noAlg": options.Algorithms = []; break;
                case "rsa": options.MinRsaKeySize = 1024; break;
                case "path": options.RefreshPath = "/"; break;
                case "samePaths": options.RefreshPath = options.RegistrationPath; break;
                case "grace": options.ProofExpiryGrace = TimeSpan.FromSeconds(-1); break;
                case "cookieName": options.ProofCookieName = "a;b"; break;
                case "excluded": options.ExcludedPaths = [new Microsoft.AspNetCore.Http.PathString()]; break;
                case "challenge": options.RefreshChallengeLifetime = TimeSpan.Zero; break;
            }

            Assert.Throws<InvalidOperationException>(options.Validate);
        }
    }

    /// <summary>An <see cref="IOptionsMonitor{TOptions}"/> returning one instance for every name.</summary>
    internal sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
