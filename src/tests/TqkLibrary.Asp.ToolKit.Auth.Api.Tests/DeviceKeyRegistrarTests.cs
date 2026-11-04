using Microsoft.Extensions.DependencyInjection;
using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TqkLibrary.Asp.ToolKit.Auth.Api.Enums;
using TqkLibrary.Asp.ToolKit.Auth.Api.Extensions;
using TqkLibrary.Asp.ToolKit.Auth.Api.Interfaces;
using TqkLibrary.Asp.ToolKit.Auth.Api.Models;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Tests
{
    public class DeviceKeyRegistrarTests
    {
        private sealed class FakeDeviceKeyStore : IDeviceKeyStore
        {
            public bool Result { get; set; } = true;
            public List<(string SessionId, string Jwk, string Algorithm)> Calls { get; } = new();
            public List<(DateTimeOffset Now, DateTimeOffset? CreatedNotBefore)> Windows { get; } = new();

            public Task<bool> TrySetDeviceKeyAsync(string sessionId, string publicKeyJwk, string algorithm,
                DateTimeOffset now, DateTimeOffset? createdNotBefore, CancellationToken cancellationToken)
            {
                Calls.Add((sessionId, publicKeyJwk, algorithm));
                Windows.Add((now, createdNotBefore));
                return Task.FromResult(Result);
            }
        }

        private sealed class Fixture : IDisposable
        {
            private readonly ServiceProvider _root;
            private readonly IServiceScope _scope;
            public FakeDeviceKeyStore Store { get; } = new();
            public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));

            public Fixture(bool withStore = true, Action<ApiAuthOptions>? configure = null)
            {
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton<TimeProvider>(Clock);
                services.AddTqkApiAuth(o => configure?.Invoke(o));
                services.AddAuthentication(SessionTokenDefaults.AuthenticationScheme).AddTqkSessionToken();
                if (withStore)
                    services.AddSingleton<IDeviceKeyStore>(Store);
                _root = services.BuildServiceProvider();
                _scope = _root.CreateScope();
            }

            public DeviceKeyRegistrar Registrar => _scope.ServiceProvider.GetRequiredService<DeviceKeyRegistrar>();
            public SessionAccessCache Cache => _root.GetRequiredService<SessionAccessCache>();

            public void Dispose()
            {
                _scope.Dispose();
                _root.Dispose();
            }
        }

        private static ClaimsPrincipal SessionUser(string sid = "s-1", string scheme = SessionTokenDefaults.AuthenticationScheme, bool bound = false)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "u-1"), new("sid", sid) };
            if (bound)
                claims.Add(new Claim("device_bound", "true"));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
        }

        private static string B64(byte[] bytes) => Base64Url.EncodeToString(bytes);

        private static (byte[] X, byte[] Y) NewEcKey()
        {
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var p = ec.ExportParameters(false);
            return (p.Q.X!, p.Q.Y!);
        }

        private static string EcJwk(byte[] x, byte[] y, string crv = "P-256", string? extra = null)
            => "{\"kty\":\"EC\",\"crv\":\"" + crv + "\",\"x\":\"" + B64(x) + "\",\"y\":\"" + B64(y) + "\"" + (extra ?? "") + "}";

        private static string RsaJwk(int bits, string? extra = null)
        {
            using var rsa = RSA.Create(bits);
            var p = rsa.ExportParameters(false);
            return "{\"kty\":\"RSA\",\"n\":\"" + B64(p.Modulus!) + "\",\"e\":\"" + B64(p.Exponent!) + "\"" + (extra ?? "") + "}";
        }

        [Fact]
        public async Task ValidEcKey_Registered_StoresCanonicalJwk()
        {
            using var f = new Fixture();
            var (x, y) = NewEcKey();
            // WebCrypto exportKey("jwk") shape: extra members ext/key_ops, any member order.
            var jwk = "{\"y\":\"" + B64(y) + "\",\"key_ops\":[\"verify\"],\"ext\":true,\"x\":\"" + B64(x) + "\",\"kty\":\"EC\",\"crv\":\"P-256\"}";

            var result = await f.Registrar.RegisterAsync(SessionUser(), jwk, "ES256");

            Assert.Equal(DeviceKeyRegistrationResult.Registered, result);
            var call = Assert.Single(f.Store.Calls);
            Assert.Equal("s-1", call.SessionId);
            Assert.Equal("ES256", call.Algorithm);
            Assert.Equal("{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" + B64(x) + "\",\"y\":\"" + B64(y) + "\"}", call.Jwk);
        }

        [Fact]
        public async Task ValidRsa2048Key_Registered_StoresCanonicalJwk()
        {
            using var f = new Fixture();
            var jwk = RsaJwk(2048, ",\"alg\":\"RS256\",\"use\":\"sig\",\"kid\":\"k1\"");

            var result = await f.Registrar.RegisterAsync(SessionUser(), jwk, "RS256");

            Assert.Equal(DeviceKeyRegistrationResult.Registered, result);
            var stored = JsonDocument.Parse(Assert.Single(f.Store.Calls).Jwk).RootElement;
            Assert.Equal(["e", "kty", "n"], stored.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal("AQAB", stored.GetProperty("e").GetString());
        }

        [Fact]
        public async Task RsaModulusWithLeadingZeroByte_Registered_StoredInMinimalForm()
        {
            using var f = new Fixture();
            using var rsa = RSA.Create(2048);
            var p = rsa.ExportParameters(false);
            var padded = new byte[] { 0 }.Concat(p.Modulus!).ToArray();
            var jwk = "{\"kty\":\"RSA\",\"n\":\"" + B64(padded) + "\",\"e\":\"AQAB\"}";

            var result = await f.Registrar.RegisterAsync(SessionUser(), jwk, "RS256");

            Assert.Equal(DeviceKeyRegistrationResult.Registered, result);
            var storedN = JsonDocument.Parse(Assert.Single(f.Store.Calls).Jwk).RootElement.GetProperty("n").GetString()!;
            Assert.NotEqual(B64(padded), storedN); // the zero byte was dropped
            Assert.Equal(B64(p.Modulus!), storedN);
        }

        [Fact]
        public async Task DefaultWindow_PassesNowAndTenMinutesBack_ToStore()
        {
            using var f = new Fixture();
            var (x, y) = NewEcKey();

            Assert.Equal(DeviceKeyRegistrationResult.Registered, await f.Registrar.RegisterAsync(SessionUser(), EcJwk(x, y), "ES256"));

            var window = Assert.Single(f.Store.Windows);
            Assert.Equal(f.Clock.GetUtcNow(), window.Now);
            Assert.Equal(f.Clock.GetUtcNow() - TimeSpan.FromMinutes(10), window.CreatedNotBefore);
        }

        [Fact]
        public async Task NullWindow_PassesNullCreatedNotBefore()
        {
            using var f = new Fixture(configure: o => o.DeviceKeyRegistrationWindow = null);
            var (x, y) = NewEcKey();

            await f.Registrar.RegisterAsync(SessionUser(), EcJwk(x, y), "ES256");

            var window = Assert.Single(f.Store.Windows);
            Assert.Equal(f.Clock.GetUtcNow(), window.Now);
            Assert.Null(window.CreatedNotBefore);
        }

        [Fact]
        public async Task CustomWindow_UsesConfiguredSpan()
        {
            using var f = new Fixture(configure: o => o.DeviceKeyRegistrationWindow = TimeSpan.FromSeconds(30));
            var (x, y) = NewEcKey();

            await f.Registrar.RegisterAsync(SessionUser(), EcJwk(x, y), "ES256");

            Assert.Equal(f.Clock.GetUtcNow() - TimeSpan.FromSeconds(30), Assert.Single(f.Store.Windows).CreatedNotBefore);
        }

        [Fact]
        public async Task WindowClosed_StoreFalse_AlreadyBound()
        {
            using var f = new Fixture();
            f.Store.Result = false; // the store rejects: CreatedAt older than createdNotBefore
            var (x, y) = NewEcKey();

            var result = await f.Registrar.RegisterAsync(SessionUser(), EcJwk(x, y), "ES256");

            Assert.Equal(DeviceKeyRegistrationResult.AlreadyBound, result);
            Assert.NotNull(Assert.Single(f.Store.Windows).CreatedNotBefore);
        }

        [Fact]
        public async Task Registered_InvalidatesCachedSession()
        {
            using var f = new Fixture();
            var loads = 0;
            var record = new SessionAccessRecord
            {
                UserId = "u-1", SecretHash = "h", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1), UserName = "n",
            };
            Task<SessionAccessRecord?> Load(CancellationToken _) { loads++; return Task.FromResult<SessionAccessRecord?>(record); }
            await f.Cache.GetAsync("s-1", Load);
            await f.Cache.GetAsync("s-1", Load);
            Assert.Equal(1, loads);

            var (x, y) = NewEcKey();
            Assert.Equal(DeviceKeyRegistrationResult.Registered, await f.Registrar.RegisterAsync(SessionUser(), EcJwk(x, y), "ES256"));

            await f.Cache.GetAsync("s-1", Load);
            Assert.Equal(2, loads);
        }

        [Fact]
        public async Task StoreReturnsFalse_AlreadyBound_CacheKept()
        {
            using var f = new Fixture();
            f.Store.Result = false;
            var loads = 0;
            var record = new SessionAccessRecord
            {
                UserId = "u-1", SecretHash = "h", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1), UserName = "n",
            };
            Task<SessionAccessRecord?> Load(CancellationToken _) { loads++; return Task.FromResult<SessionAccessRecord?>(record); }
            await f.Cache.GetAsync("s-1", Load);
            var (x, y) = NewEcKey();

            var result = await f.Registrar.RegisterAsync(SessionUser(), EcJwk(x, y), "ES256");

            Assert.Equal(DeviceKeyRegistrationResult.AlreadyBound, result);
            await f.Cache.GetAsync("s-1", Load);
            Assert.Equal(1, loads);
        }

        [Fact]
        public async Task BoundClaim_AlreadyBound_StoreNotCalled()
        {
            using var f = new Fixture();
            var (x, y) = NewEcKey();

            var result = await f.Registrar.RegisterAsync(SessionUser(bound: true), EcJwk(x, y), "ES256");

            Assert.Equal(DeviceKeyRegistrationResult.AlreadyBound, result);
            Assert.Empty(f.Store.Calls);
        }

        [Fact]
        public async Task Anonymous_NoSession()
        {
            using var f = new Fixture();
            var (x, y) = NewEcKey();

            var result = await f.Registrar.RegisterAsync(new ClaimsPrincipal(new ClaimsIdentity()), EcJwk(x, y), "ES256");

            Assert.Equal(DeviceKeyRegistrationResult.NoSession, result);
            Assert.Empty(f.Store.Calls);
        }

        [Fact]
        public async Task OtherScheme_NoSession()
        {
            using var f = new Fixture();
            var (x, y) = NewEcKey();

            var result = await f.Registrar.RegisterAsync(SessionUser(scheme: "Cookies"), EcJwk(x, y), "ES256");

            Assert.Equal(DeviceKeyRegistrationResult.NoSession, result);
        }

        [Fact]
        public async Task MissingSidClaim_NoSession()
        {
            using var f = new Fixture();
            var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "u-1")], SessionTokenDefaults.AuthenticationScheme));
            var (x, y) = NewEcKey();

            Assert.Equal(DeviceKeyRegistrationResult.NoSession, await f.Registrar.RegisterAsync(user, EcJwk(x, y), "ES256"));
        }

        /// <summary>Same 43-char text but the last char sets an unused trailing bit: decodes to the same bytes.</summary>
        private static string NonCanonicalTail(string text)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
            var last = alphabet.IndexOf(text[^1]);
            Assert.Equal(0, last & 3); // canonical encoders leave the 2 unused bits zero
            return text[..^1] + alphabet[last + 1];
        }

        private static string RsaEvenModulusJwk()
        {
            using var rsa = RSA.Create(2048);
            var n = rsa.ExportParameters(false).Modulus!;
            n[^1] &= 0xFE;
            return "{\"kty\":\"RSA\",\"n\":\"" + B64(n) + "\",\"e\":\"AQAB\"}";
        }

        private static string RsaOversizedJwk()
        {
            var n = new byte[1025]; // 8200 bits
            n[0] = 0x80;
            n[^1] = 0x01;
            return "{\"kty\":\"RSA\",\"n\":\"" + B64(n) + "\",\"e\":\"AQAB\"}";
        }

        public static TheoryData<string, string> InvalidCases()
        {
            var (x, y) = NewEcKey();
            var offCurveY = (byte[])y.Clone();
            offCurveY[^1] ^= 1;
            var d = B64(new byte[32]);
            return new TheoryData<string, string>
            {
                { EcJwk(x, y, extra: ",\"d\":\"" + d + "\""), "ES256" },                // private key
                { EcJwk(x, y, crv: "P-384"), "ES256" },                                  // wrong curve
                { EcJwk(x[..31], y), "ES256" },                                          // short x
                { EcJwk(x, offCurveY), "ES256" },                                        // point not on the curve
                { EcJwk(x, y), "none" },                                                 // alg none
                { EcJwk(x, y), "HS256" },                                                // symmetric alg
                { EcJwk(x, y), "es256" },                                                // case matters
                { EcJwk(x, y), "RS256" },                                                // alg / kty mismatch
                { EcJwk(x, y, extra: ",\"alg\":\"ES384\""), "ES256" },                   // JWK alg disagrees
                { EcJwk(x, y, extra: ",\"use\":\"enc\""), "ES256" },                     // not a signing key
                { EcJwk(x, y, extra: ",\"x\":\"" + B64(x) + "\""), "ES256" },            // duplicate member
                { EcJwk(x, y).Replace(B64(x), B64(x) + "="), "ES256" },                  // padded base64url
                { EcJwk(x, y).Replace(B64(x), "+" + B64(x)[1..]), "ES256" },             // 43 chars, '+' is not base64url
                { EcJwk(x, y).Replace(B64(x), "/" + B64(x)[1..]), "ES256" },             // 43 chars, '/' is not base64url
                { EcJwk(x, y).Replace(B64(x), NonCanonicalTail(B64(x))), "ES256" },      // non-zero trailing bits
                { RsaEvenModulusJwk(), "RS256" },                                        // even modulus
                { RsaOversizedJwk(), "RS256" },                                          // > 8192 bit modulus
                { EcJwk(x, y, extra: ",\"alg\":1"), "ES256" },                          // alg not a string
                { EcJwk(x, y, extra: ",\"use\":1"), "ES256" },                          // use not a string
                { EcJwk(x, y, extra: ",\"z\":[[[[1]]]]"), "ES256" },                    // nested deeper than max depth
                { EcJwk(x, y, extra: ",\"\\u0078\":\"" + B64(x) + "\""), "ES256" },    // duplicate member via escape
                { "{\"kty\":\"oct\",\"k\":\"" + d + "\"}", "HS256" },                     // symmetric key
                { RsaJwk(1024), "RS256" },                                               // RSA too short
                { RsaJwk(2048).Replace("\"AQAB\"", "\"Aw\""), "RS256" },                  // e = 3
                { RsaJwk(2048, ",\"d\":\"" + d + "\""), "RS256" },                       // RSA private part
                { RsaJwk(2048, ",\"p\":\"" + d + "\""), "RS256" },
                { "[]", "ES256" },                                                       // not an object
                { "not json", "ES256" },
                { "", "ES256" },
                { EcJwk(x, y, extra: ",\"pad\":\"" + new string('a', DeviceKeyRegistrar.MaxJwkLength) + "\""), "ES256" }, // too long
            };
        }

        [Theory]
        [MemberData(nameof(InvalidCases))]
        public async Task InvalidKeyOrAlgorithm_Invalid_StoreNotCalled(string jwk, string algorithm)
        {
            using var f = new Fixture();

            var result = await f.Registrar.RegisterAsync(SessionUser(), jwk, algorithm);

            Assert.Equal(DeviceKeyRegistrationResult.Invalid, result);
            Assert.Empty(f.Store.Calls);
        }

        [Fact]
        public async Task NoStoreRegistered_Throws()
        {
            using var f = new Fixture(withStore: false);
            var (x, y) = NewEcKey();

            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Registrar.RegisterAsync(SessionUser(), EcJwk(x, y), "ES256"));
        }

        [Fact]
        public void NoStoreRegistered_RegistrarStillResolves()
        {
            using var f = new Fixture(withStore: false);
            Assert.NotNull(f.Registrar);
        }
    }
}
