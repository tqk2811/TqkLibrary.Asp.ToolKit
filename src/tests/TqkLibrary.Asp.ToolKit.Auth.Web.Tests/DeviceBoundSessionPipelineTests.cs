using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TqkLibrary.Asp.ToolKit.Auth.Web.Interfaces;
using TqkLibrary.Asp.ToolKit.Auth.Web.Models;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Tests
{
    /// <summary>DBSC end to end through the real cookie pipeline (registration, refresh, enforcement, failure handling).</summary>
    public class DeviceBoundSessionPipelineTests
    {
        private const string Sid = "s-1";
        private const string MainCookie = ".AspNetCore.Cookies";
        private const string ProofCookie = MainCookie + ".proof";

        /// <summary>The app's API for the key: stores it like the real API (so <c>me</c> reports it afterwards).</summary>
        private sealed class FakeDeviceKeyApi : IDeviceKeyApiClient
        {
            public bool Result { get; set; } = true;
            /// <summary>Stores the key, then fails: the registration response is lost although the API holds the key.</summary>
            public bool ThrowAfterStore { get; set; }
            public List<(string Jwk, string Algorithm)> Calls { get; } = [];
            public string? StoredJwk { get; set; }
            public string? StoredAlgorithm { get; set; }

            public Task<bool> RegisterDeviceKeyAsync(string jwk, string algorithm, CancellationToken cancellationToken)
            {
                lock (Calls)
                    Calls.Add((jwk, algorithm));
                if (Result && StoredJwk is null)
                {
                    StoredJwk = jwk;
                    StoredAlgorithm = algorithm;
                }
                if (ThrowAfterStore)
                    throw new HttpRequestException("connection reset after the API stored the key");
                return Task.FromResult(Result);
            }
        }

        /// <summary>Records every call and answers <see cref="Outcome"/> (or calls base when null).</summary>
        private sealed class RecordingHandler : DeviceProofFailureHandler
        {
            public Func<DeviceProofFailureContext, ValueTask<DeviceProofOutcome>>? Outcome { get; set; }
            public List<DeviceProofFailureContext> Calls { get; } = [];

            public override ValueTask<DeviceProofOutcome> HandleAsync(DeviceProofFailureContext context)
            {
                lock (Calls)
                    Calls.Add(context);
                return Outcome is { } outcome ? outcome(context) : base.HandleAsync(context);
            }
        }

        private sealed class Harness : IAsyncDisposable
        {
            public required WebAuthTestApp App { get; init; }
            public required FakeDeviceKeyApi KeyApi { get; init; }
            public required FakeTimeProvider Clock { get; init; }
            public required RecordingHandler Handler { get; init; }
            public DbscTestDevice Device { get; } = DbscTestDevice.Es256();

            /// <summary>Authorization headers seen by <c>/call</c>, <c>/explicit-scheme</c>, <c>/error</c> (one per call, null = none).</summary>
            public required List<string?> CallAuthorizations { get; init; }

            /// <summary>The user as the API reports it: bound when the key API stored a key.</summary>
            public CurrentUserInfo User(string? mode = null) => new()
            {
                Id = "u-1",
                UserName = "editor",
                DeviceKeyJwk = KeyApi.StoredJwk,
                DeviceKeyAlgorithm = KeyApi.StoredAlgorithm,
                DeviceBindingMode = mode,
            };

            public DeviceProofCookie ProofCookies => App.Services.GetRequiredService<DeviceProofCookie>();

            public async ValueTask DisposeAsync()
            {
                Device.Dispose();
                await App.DisposeAsync();
            }
        }

        private static async Task<Harness> StartAsync(Action<DeviceBoundSessionOptions>? configure = null, bool https = true,
            bool enabled = true, Action<Microsoft.AspNetCore.Routing.IEndpointRouteBuilder>? mapEndpoints = null,
            Action<WebAuthOptions>? configureWeb = null, Action<IApplicationBuilder>? beforeRouting = null,
            Action<IServiceCollection>? configureServices = null)
        {
            var keyApi = new FakeDeviceKeyApi();
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var handler = new RecordingHandler();
            var callAuthorizations = new List<string?>();
            var app = await WebAuthTestApp.StartAsync(o =>
            {
                o.DeviceBound.Enabled = enabled;
                configure?.Invoke(o.DeviceBound);
                configureWeb?.Invoke(o);
            }, services =>
            {
                services.AddSingleton<IDeviceKeyApiClient>(keyApi);
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
                services.AddSingleton<DeviceProofFailureHandler>(handler);
                configureServices?.Invoke(services);
            }, endpoints =>
            {
                endpoints.MapGet("/sign-out", async (HttpContext context, WebSessionManager sessions) =>
                {
                    await sessions.SignOutAsync(context);
                    return Results.Ok();
                });
                endpoints.MapGet("/public/whoami", (HttpContext context) => Results.Text(
                    "auth=" + (context.User.Identity?.IsAuthenticated == true).ToString().ToLowerInvariant()));
                // One API call through the real bearer handler (the extra parameter keeps the lambda off the RequestDelegate overload); the API answers 401 + Bearer challenge (session ended).
                endpoints.MapGet("/call", async (HttpContext context, CancellationToken _) => Results.Text(await CallApiAsync(context, callAuthorizations)));
                // Like an endpoint with explicit AuthenticationSchemes: PolicyEvaluator re-authenticates the cookie scheme
                // and puts its principal back on context.User before the endpoint runs.
                endpoints.MapGet("/explicit-scheme", async (HttpContext context, IOptions<WebAuthOptions> options) =>
                {
                    var result = await context.AuthenticateAsync(options.Value.Scheme);
                    if (result.Succeeded)
                        context.User = result.Principal!;
                    return Results.Text(await CallApiAsync(context, callAuthorizations));
                });
                endpoints.MapGet("/boom", (HttpContext context) => { throw new InvalidOperationException("boom"); });
                endpoints.Map("/error", async (HttpContext context, CancellationToken _) => Results.Text("error:" + await CallApiAsync(context, callAuthorizations)));
                mapEndpoints?.Invoke(endpoints);
            }, https: https, beforeRouting: beforeRouting);
            var h = new Harness { App = app, KeyApi = keyApi, Clock = clock, Handler = handler, CallAuthorizations = callAuthorizations };
            app.Api.Me = () => h.User();
            return h;
        }

        private static async Task<string> CallApiAsync(HttpContext context, List<string?> seen)
        {
            var handler = context.RequestServices.GetRequiredService<SessionBearerHandler>();
            var echo = new BearerEcho { Status = HttpStatusCode.Unauthorized };
            handler.InnerHandler = echo;
            using var client = new HttpClient(handler);
            using var response = await client.GetAsync("http://api/items");
            lock (seen)
                seen.Add(echo.LastAuthorization);
            return echo.LastAuthorization ?? "none";
        }

        private static string? RegistrationHeader(HttpResponseMessage response)
            => response.Headers.TryGetValues("Secure-Session-Registration", out var values) ? values.Single() : null;

        private static string ChallengeFrom(string header) => Regex.Match(header, "challenge=\"([^\"]*)\"").Groups[1].Value;

        private static string RefreshChallenge(HttpResponseMessage response)
            => Regex.Match(response.Headers.GetValues("Secure-Session-Challenge").Single(), "^\"([^\"]+)\"").Groups[1].Value;

        private static Task<HttpResponseMessage> PostAsync(WebAuthTestApp app, string path, string? proof = null, string? sessionId = null)
            => app.SendAsync(HttpMethod.Post, path, r =>
            {
                if (proof is not null)
                    r.Headers.TryAddWithoutValidation("Secure-Session-Response", proof);
                if (sessionId is not null)
                    r.Headers.TryAddWithoutValidation("Sec-Secure-Session-Id", "\"" + sessionId + "\"");
            });

        /// <summary>Signs in and completes the DBSC registration; returns the session config JSON.</summary>
        private static async Task<JsonElement> SignInAndBindAsync(Harness h, string sid = Sid, bool persistent = false)
        {
            using var signIn = await h.App.SignInAsync(sid, persistent);
            var header = RegistrationHeader(signIn) ?? throw new InvalidOperationException("no registration header");
            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(ChallengeFrom(header)));
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            return JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        private static async Task<bool> IsAuthenticatedAsync(WebAuthTestApp app, string path = "/whoami")
        {
            using var response = await app.SendAsync(HttpMethod.Get, path);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadAsStringAsync()).StartsWith("auth=true", StringComparison.Ordinal);
        }

        private static bool Continues(string json) => JsonDocument.Parse(json).RootElement.TryGetProperty("continue", out var c) && c.GetBoolean();

        // ---------- registration ----------

        [Fact]
        public async Task SignIn_Https_SendsRegistrationHeader()
        {
            await using var h = await StartAsync();

            using var response = await h.App.SignInAsync(Sid);

            var header = RegistrationHeader(response);
            Assert.NotNull(header);
            Assert.StartsWith("(ES256 RS256);path=\"/dbsc/start\";challenge=\"", header);
            Assert.NotEmpty(ChallengeFrom(header!));
        }

        [Fact]
        public async Task SignIn_Http_NoRegistrationHeader()
        {
            await using var h = await StartAsync(https: false);

            using var response = await h.App.SignInAsync(Sid);

            Assert.Null(RegistrationHeader(response));
        }

        [Fact]
        public async Task Start_ValidProof_StoresKey_DoesNotTouchMainCookie_SetsProof_ReturnsConfig()
        {
            await using var h = await StartAsync();
            using var signIn = await h.App.SignInAsync(Sid, persistent: true);
            var mainCookie = h.App.Cookies[MainCookie];
            var challenge = ChallengeFrom(RegistrationHeader(signIn)!);

            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(challenge));

            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            var call = Assert.Single(h.KeyApi.Calls);
            Assert.Equal("ES256", call.Algorithm);
            Assert.Equal(h.Device.Jwk()["x"]!.GetValue<string>(), JsonDocument.Parse(call.Jwk).RootElement.GetProperty("x").GetString());

            var setCookies = WebAuthTestApp.SetCookies(start);
            // The API is the single source of truth: the main cookie is NOT reissued.
            Assert.DoesNotContain(setCookies, c => c.StartsWith(MainCookie + "=", StringComparison.Ordinal));
            Assert.Equal(mainCookie, h.App.Cookies[MainCookie]);
            var proof = Assert.Single(setCookies, c => c.StartsWith(ProofCookie + "="));

            using var json = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
            var root = json.RootElement;
            Assert.Equal(DeviceBoundSessionMiddleware.SessionIdentifier(Sid), root.GetProperty("session_identifier").GetString());
            Assert.Equal("/dbsc/refresh", root.GetProperty("refresh_url").GetString());
            Assert.Equal("https://localhost", root.GetProperty("scope").GetProperty("origin").GetString());
            Assert.False(root.GetProperty("scope").GetProperty("include_site").GetBoolean());
            var credential = Assert.Single(root.GetProperty("credentials").EnumerateArray().ToList());
            Assert.Equal("cookie", credential.GetProperty("type").GetString());
            Assert.Equal(ProofCookie, credential.GetProperty("name").GetString());
            Assert.Equal(AttributeSet(credential.GetProperty("attributes").GetString()!), AttributeSet(proof.Split(';', 2)[1]));
            Assert.DoesNotContain("domain=", proof, StringComparison.OrdinalIgnoreCase);

            // The proof works on an ordinary request.
            Assert.True(await IsAuthenticatedAsync(h.App));
        }

        /// <summary>Cookie attributes without the expiry ones, normalised for comparison.</summary>
        private static string[] AttributeSet(string attributes)
            => attributes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(a => !a.StartsWith("max-age", StringComparison.OrdinalIgnoreCase) && !a.StartsWith("expires", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.ToLowerInvariant())
                .Order(StringComparer.Ordinal)
                .ToArray();

        [Fact]
        public async Task Start_SecondTime_400()
        {
            await using var h = await StartAsync();
            await SignInAndBindAsync(h);

            using var again = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt("whatever"));

            Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
            Assert.Single(h.KeyApi.Calls);
        }

        [Fact]
        public async Task Start_ApiRefuses_400_NoProof_SessionStaysUnbound()
        {
            await using var h = await StartAsync();
            h.KeyApi.Result = false;
            using var signIn = await h.App.SignInAsync(Sid);

            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(ChallengeFrom(RegistrationHeader(signIn)!)));

            Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
            Assert.Empty(WebAuthTestApp.SetCookies(start));
            Assert.False(h.App.Cookies.ContainsKey(ProofCookie));
            // Unbound: no proof needed, and refresh ends the browser session.
            Assert.True(await IsAuthenticatedAsync(h.App));
            using var refresh = await PostAsync(h.App, "/dbsc/refresh", sessionId: DeviceBoundSessionMiddleware.SessionIdentifier(Sid));
            Assert.False(Continues(await refresh.Content.ReadAsStringAsync()));
        }

        [Fact]
        public async Task Start_ReplayedOrForeignChallenge_400()
        {
            await using var h = await StartAsync();
            using var signIn = await h.App.SignInAsync(Sid);
            var challenge = ChallengeFrom(RegistrationHeader(signIn)!);
            h.KeyApi.Result = false; // first attempt consumes the challenge but does not bind

            using var first = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(challenge));
            using var replay = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(challenge));
            using var garbage = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt("not-a-challenge"));

            Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
            Assert.Single(h.KeyApi.Calls);
        }

        [Fact]
        public async Task Start_ChallengeOfAnotherSession_400()
        {
            await using var h = await StartAsync();
            using var other = await h.App.SignInAsync("s-other");
            var foreign = ChallengeFrom(RegistrationHeader(other)!);
            await h.App.SignInAsync(Sid);

            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(foreign));

            Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
            Assert.Empty(h.KeyApi.Calls);
        }

        [Fact]
        public async Task Start_ExpiredChallenge_400()
        {
            await using var h = await StartAsync();
            using var signIn = await h.App.SignInAsync(Sid);
            h.Clock.Advance(TimeSpan.FromMinutes(5));

            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(ChallengeFrom(RegistrationHeader(signIn)!)));

            Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
        }

        [Fact]
        public async Task Start_BadProofOrNoSession_400()
        {
            await using var h = await StartAsync();
            using var noSession = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt("c"));
            using var signIn = await h.App.SignInAsync(Sid);
            using var noProof = await PostAsync(h.App, "/dbsc/start");
            using var refreshShaped = await PostAsync(h.App, "/dbsc/start", h.Device.RefreshJwt(ChallengeFrom(RegistrationHeader(signIn)!)));
            using var get = await h.App.SendAsync(HttpMethod.Get, "/dbsc/start");

            Assert.Equal(HttpStatusCode.BadRequest, noSession.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, noProof.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, refreshShaped.StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
            Assert.Empty(h.KeyApi.Calls);
        }

        [Fact]
        public async Task Start_ApiFails_503()
        {
            await using var h = await StartAsync();
            h.KeyApi.ThrowAfterStore = true;
            using var signIn = await h.App.SignInAsync(Sid);

            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(ChallengeFrom(RegistrationHeader(signIn)!)));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
            Assert.False(h.App.Cookies.ContainsKey(ProofCookie));
        }

        // ---------- lost registration response ----------

        [Fact]
        public async Task LostRegistrationResponse_KeyStoredAtApi_RefreshGetsKeyFromMe_OldCookieWorksWithProof()
        {
            await using var h = await StartAsync();
            h.KeyApi.ThrowAfterStore = true;
            using var signIn = await h.App.SignInAsync(Sid);
            var mainCookie = h.App.Cookies[MainCookie];
            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(ChallengeFrom(RegistrationHeader(signIn)!)));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
            Assert.NotNull(h.KeyApi.StoredJwk);
            var identifier = DeviceBoundSessionMiddleware.SessionIdentifier(Sid);
            var meCalls = h.App.Api.MeCalls;

            // The browser refreshes: the web does not know the key yet and asks the API.
            using var first = await PostAsync(h.App, "/dbsc/refresh", sessionId: identifier);
            Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);
            Assert.Equal(meCalls + 1, h.App.Api.MeCalls);
            using var second = await PostAsync(h.App, "/dbsc/refresh", h.Device.RefreshJwt(RefreshChallenge(first)), identifier);

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.True(h.App.Cookies.ContainsKey(ProofCookie));
            // Same main cookie as at sign-in, never reissued, and it works with the proof.
            Assert.Equal(mainCookie, h.App.Cookies[MainCookie]);
            Assert.True(await IsAuthenticatedAsync(h.App));
        }

        [Fact]
        public async Task MeWithoutAlgorithm_AlgorithmDerivedFromJwk_RefreshAndEnforcementWork()
        {
            await using var h = await StartAsync();
            h.KeyApi.ThrowAfterStore = true; // the web learns the key from `me` only
            using var signIn = await h.App.SignInAsync(Sid);
            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(ChallengeFrom(RegistrationHeader(signIn)!)));
            h.KeyApi.StoredAlgorithm = null; // the app's `me` does not return deviceKeyAlgorithm
            var identifier = DeviceBoundSessionMiddleware.SessionIdentifier(Sid);

            using var first = await PostAsync(h.App, "/dbsc/refresh", sessionId: identifier);
            Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);
            using var second = await PostAsync(h.App, "/dbsc/refresh", h.Device.RefreshJwt(RefreshChallenge(first)), identifier);

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.True(await IsAuthenticatedAsync(h.App));
            h.App.RemoveCookie(ProofCookie);
            Assert.False(await IsAuthenticatedAsync(h.App));
            Assert.Equal(DeviceProofFailureReason.Missing, Assert.Single(h.Handler.Calls).Reason);
        }

        [Fact]
        public async Task LostRegistrationResponse_RequestWithoutProof_IsAProofFailure_NotALockout()
        {
            await using var h = await StartAsync();
            h.KeyApi.ThrowAfterStore = true;
            using var signIn = await h.App.SignInAsync(Sid);
            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(ChallengeFrom(RegistrationHeader(signIn)!)));

            Assert.False(await IsAuthenticatedAsync(h.App));

            var failure = Assert.Single(h.Handler.Calls);
            Assert.Equal(DeviceProofFailureReason.Missing, failure.Reason);
            Assert.True(h.App.Cookies.ContainsKey(MainCookie));
        }

        // ---------- refresh ----------

        [Fact]
        public async Task Refresh_WithoutProof_403WithChallengeAndId_ThenValidProof_200NewCookie()
        {
            await using var h = await StartAsync();
            var config = await SignInAndBindAsync(h);
            var identifier = config.GetProperty("session_identifier").GetString()!;
            var oldProof = h.App.Cookies[ProofCookie];

            using var first = await PostAsync(h.App, "/dbsc/refresh", sessionId: identifier);
            Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);
            var header = first.Headers.GetValues("Secure-Session-Challenge").Single();
            var match = Regex.Match(header, "^\"([^\"]+)\";id=\"([^\"]+)\"$");
            Assert.True(match.Success, header);
            Assert.Equal(identifier, match.Groups[2].Value);

            h.Clock.Advance(TimeSpan.FromSeconds(1));
            using var second = await PostAsync(h.App, "/dbsc/refresh", h.Device.RefreshJwt(match.Groups[1].Value), identifier);

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Contains(WebAuthTestApp.SetCookies(second), c => c.StartsWith(ProofCookie + "="));
            Assert.NotEqual(oldProof, h.App.Cookies[ProofCookie]);
            Assert.Equal(identifier, JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement.GetProperty("session_identifier").GetString());

            // The same challenge cannot be used twice: new challenge instead.
            using var replay = await PostAsync(h.App, "/dbsc/refresh", h.Device.RefreshJwt(match.Groups[1].Value), identifier);
            Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
            Assert.True(replay.Headers.Contains("Secure-Session-Challenge"));
        }

        [Fact]
        public async Task Refresh_ExpiredChallenge_403WithNewChallenge()
        {
            await using var h = await StartAsync();
            var identifier = (await SignInAndBindAsync(h)).GetProperty("session_identifier").GetString()!;
            using var first = await PostAsync(h.App, "/dbsc/refresh", sessionId: identifier);
            var challenge = RefreshChallenge(first);
            h.Clock.Advance(TimeSpan.FromMinutes(2));

            using var late = await PostAsync(h.App, "/dbsc/refresh", h.Device.RefreshJwt(challenge), identifier);

            Assert.Equal(HttpStatusCode.Forbidden, late.StatusCode);
            Assert.NotEqual(challenge, RefreshChallenge(late));
        }

        [Fact]
        public async Task Refresh_BadSignatureOrKeyPresent_401()
        {
            await using var h = await StartAsync();
            var identifier = (await SignInAndBindAsync(h)).GetProperty("session_identifier").GetString()!;
            using var first = await PostAsync(h.App, "/dbsc/refresh", sessionId: identifier);
            var challenge = RefreshChallenge(first);
            using var thief = DbscTestDevice.Es256();
            var keyInPayload = DbscTestDevice.Payload(challenge);
            keyInPayload["key"] = h.Device.Jwk();

            using var foreignKey = await PostAsync(h.App, "/dbsc/refresh", thief.RefreshJwt(challenge), identifier);
            using var withJwk = await PostAsync(h.App, "/dbsc/refresh", h.Device.RegistrationJwt(challenge), identifier);
            using var withKey = await PostAsync(h.App, "/dbsc/refresh", h.Device.Jwt(h.Device.Header(withJwk: false), keyInPayload), identifier);

            Assert.Equal(HttpStatusCode.Unauthorized, foreignKey.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, withJwk.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, withKey.StatusCode);
        }

        [Fact]
        public async Task Refresh_WrongSessionId_OrSignedOut_ContinueFalse()
        {
            await using var h = await StartAsync();
            await SignInAndBindAsync(h);

            using var wrongId = await PostAsync(h.App, "/dbsc/refresh", sessionId: "other-id");
            Assert.Equal(HttpStatusCode.OK, wrongId.StatusCode);
            var wrongJson = JsonDocument.Parse(await wrongId.Content.ReadAsStringAsync()).RootElement;
            Assert.False(wrongJson.GetProperty("continue").GetBoolean());
            Assert.Equal("other-id", wrongJson.GetProperty("session_identifier").GetString());

            using var signOut = await h.App.SendAsync(HttpMethod.Get, "/sign-out");
            Assert.False(h.App.Cookies.ContainsKey(ProofCookie));
            using var afterLogout = await PostAsync(h.App, "/dbsc/refresh", sessionId: DeviceBoundSessionMiddleware.SessionIdentifier(Sid));
            Assert.Equal(HttpStatusCode.OK, afterLogout.StatusCode);
            Assert.False(Continues(await afterLogout.Content.ReadAsStringAsync()));
        }

        [Fact]
        public async Task Refresh_OverHttp_AnsweredAsNoSession()
        {
            await using var h = await StartAsync();
            await SignInAndBindAsync(h);
            var meCalls = h.App.Api.MeCalls;

            using var refresh = await h.App.SendAsync(HttpMethod.Post, "http://localhost/dbsc/refresh",
                r => r.Headers.TryAddWithoutValidation("Sec-Secure-Session-Id", "\"" + DeviceBoundSessionMiddleware.SessionIdentifier(Sid) + "\""));

            Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
            Assert.False(Continues(await refresh.Content.ReadAsStringAsync()));
            Assert.False(refresh.Headers.Contains("Secure-Session-Challenge"));
            Assert.Equal(meCalls, h.App.Api.MeCalls);
        }

        [Fact]
        public async Task Refresh_KeyUnknown_MeFails_503_NotUnauthorized()
        {
            await using var h = await StartAsync();
            h.KeyApi.ThrowAfterStore = true;
            using var signIn = await h.App.SignInAsync(Sid);
            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(ChallengeFrom(RegistrationHeader(signIn)!)));
            h.App.Api.Me = () => throw new HttpRequestException("API down", null, HttpStatusCode.BadGateway);

            using var refresh = await PostAsync(h.App, "/dbsc/refresh", sessionId: DeviceBoundSessionMiddleware.SessionIdentifier(Sid));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, refresh.StatusCode);
            Assert.True(h.App.Cookies.ContainsKey(MainCookie));
        }

        [Fact]
        public async Task Refresh_KeyUnknown_PredicateThrows_KeepsSession_503()
        {
            await using var h = await StartAsync(configureWeb: o => o.IsSessionRejectedException = _ => throw new InvalidOperationException("bug"));
            h.KeyApi.ThrowAfterStore = true;
            using var signIn = await h.App.SignInAsync(Sid);
            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(ChallengeFrom(RegistrationHeader(signIn)!)));
            h.App.Api.Me = () => throw new HttpRequestException("rejected", null, HttpStatusCode.Unauthorized);

            using var refresh = await PostAsync(h.App, "/dbsc/refresh", sessionId: DeviceBoundSessionMiddleware.SessionIdentifier(Sid));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, refresh.StatusCode);
            Assert.True(h.App.Cookies.ContainsKey(MainCookie));
        }

        [Fact]
        public async Task Refresh_KeyUnknown_CustomPredicate_DropsSession()
        {
            await using var h = await StartAsync(configureWeb: o => o.IsSessionRejectedException = ex => ex is InvalidTimeZoneException);
            h.KeyApi.ThrowAfterStore = true;
            using var signIn = await h.App.SignInAsync(Sid);
            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(ChallengeFrom(RegistrationHeader(signIn)!)));
            h.App.Api.Me = () => throw new InvalidTimeZoneException("app-specific 401");

            using var refresh = await PostAsync(h.App, "/dbsc/refresh", sessionId: DeviceBoundSessionMiddleware.SessionIdentifier(Sid));

            Assert.NotEqual(HttpStatusCode.ServiceUnavailable, refresh.StatusCode);
            Assert.False(h.App.Cookies.ContainsKey(MainCookie));
        }

        [Fact]
        public async Task Refresh_KeyUnknown_MeTimeout_PredicateNotCalled_503()
        {
            var calls = 0;
            await using var h = await StartAsync(configureWeb: o => o.IsSessionRejectedException = _ =>
            {
                Interlocked.Increment(ref calls);
                return true;
            });
            h.KeyApi.ThrowAfterStore = true;
            using var signIn = await h.App.SignInAsync(Sid);
            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(ChallengeFrom(RegistrationHeader(signIn)!)));
            h.App.Api.Me = () => throw new TimeoutException("slow");

            using var refresh = await PostAsync(h.App, "/dbsc/refresh", sessionId: DeviceBoundSessionMiddleware.SessionIdentifier(Sid));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, refresh.StatusCode);
            Assert.True(h.App.Cookies.ContainsKey(MainCookie));
            Assert.Equal(0, Volatile.Read(ref calls));
        }

        /// <summary>Regression: a raw 401 from <c>me</c> during refresh ends the web session (same rule as CurrentUserMiddleware), not a 503.</summary>
        [Fact]
        public async Task Refresh_KeyUnknown_MeRaw401_DropsSession_Not503()
        {
            await using var h = await StartAsync();
            h.KeyApi.ThrowAfterStore = true;
            using var signIn = await h.App.SignInAsync(Sid);
            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(ChallengeFrom(RegistrationHeader(signIn)!)));
            h.App.Api.Me = () => throw new HttpRequestException("rejected", null, HttpStatusCode.Unauthorized);

            using var refresh = await PostAsync(h.App, "/dbsc/refresh", sessionId: DeviceBoundSessionMiddleware.SessionIdentifier(Sid));

            Assert.NotEqual(HttpStatusCode.ServiceUnavailable, refresh.StatusCode);
            Assert.False(h.App.Cookies.ContainsKey(MainCookie));
        }

        [Fact]
        public async Task Refresh_MatchingSession_MeReportsNoKey_ContinueFalse_LoggedAsError()
        {
            var logs = new ListLoggerProvider();
            await using var h = await StartAsync(configureServices: s => s.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs));
            await h.App.SignInAsync(Sid); // the app's `me` returns no key (contract bug)

            using var refresh = await PostAsync(h.App, "/dbsc/refresh", sessionId: DeviceBoundSessionMiddleware.SessionIdentifier(Sid));

            Assert.False(Continues(await refresh.Content.ReadAsStringAsync()));
            Assert.Contains(logs.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error
                && e.Category == typeof(DeviceBoundSessionMiddleware).FullName && e.Message.Contains("deviceKeyJwk", StringComparison.Ordinal));
        }

        // ---------- enforcement ----------

        [Fact]
        public async Task Reject_Default_MissingProof_Guest_CookieKept_NoMeCall()
        {
            await using var h = await StartAsync();
            await SignInAndBindAsync(h);
            var proof = h.App.Cookies[ProofCookie];
            h.App.RemoveCookie(ProofCookie);
            var meCalls = h.App.Api.MeCalls;

            using var response = await h.App.SendAsync(HttpMethod.Get, "/whoami");

            Assert.StartsWith("auth=false", await response.Content.ReadAsStringAsync());
            Assert.Empty(WebAuthTestApp.SetCookies(response));
            Assert.True(h.App.Cookies.ContainsKey(MainCookie));
            Assert.Equal(meCalls, h.App.Api.MeCalls);
            // Proof back (as after a browser refresh): the session works again.
            h.App.SetCookie(ProofCookie, proof);
            Assert.True(await IsAuthenticatedAsync(h.App));
        }

        [Fact]
        public async Task Reject_GuestRequest_DoesNotSendTheSessionToken_NorDropsTheCookie()
        {
            await using var h = await StartAsync();
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            using var response = await h.App.SendAsync(HttpMethod.Get, "/call");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("none", await response.Content.ReadAsStringAsync());
            Assert.True(h.App.Cookies.ContainsKey(MainCookie));
        }

        [Fact]
        public async Task Reject_ExplicitSchemeEndpoint_NoToken_CookieNotDropped()
        {
            await using var h = await StartAsync();
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            using var response = await h.App.SendAsync(HttpMethod.Get, "/explicit-scheme");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("none", await response.Content.ReadAsStringAsync());
            Assert.DoesNotContain(WebAuthTestApp.SetCookies(response), c => c.StartsWith(MainCookie + "=", StringComparison.Ordinal));
            Assert.True(h.App.Cookies.ContainsKey(MainCookie));
        }

        private static void MapLocalExplicitScheme(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
            => endpoints.MapGet("/local-explicit", () => Results.Text("local"))
                .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
                    Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser().Build());

        [Fact]
        public async Task Reject_LocalEndpointWithExplicitCookieScheme_IsChallenged_NotRun()
        {
            await using var h = await StartAsync(mapEndpoints: MapLocalExplicitScheme);
            await SignInAndBindAsync(h);
            using (var proven = await h.App.SendAsync(HttpMethod.Get, "/local-explicit"))
                Assert.Equal("local", await proven.Content.ReadAsStringAsync());
            h.App.RemoveCookie(ProofCookie);

            using var response = await h.App.SendAsync(HttpMethod.Get, "/local-explicit");

            // The policy re-authenticates the cookie scheme: under Reject that must not restore the principal.
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith(WebAuthTestApp.LoginPath, response.Headers.Location!.AbsolutePath);
            Assert.True(h.App.Cookies.ContainsKey(MainCookie));
        }

        [Fact]
        public async Task ReportOnly_LocalEndpointWithExplicitCookieScheme_Runs()
        {
            await using var h = await StartAsync(o => o.FailureMode = DeviceProofFailureMode.ReportOnly, mapEndpoints: MapLocalExplicitScheme);
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            using var response = await h.App.SendAsync(HttpMethod.Get, "/local-explicit");

            Assert.Equal("local", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Reject_ReExecutedByExceptionHandler_NoToken()
        {
            await using var h = await StartAsync(beforeRouting: app => app.UseExceptionHandler("/error"));
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            using var response = await h.App.SendAsync(HttpMethod.Get, "/boom");

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("error:none", await response.Content.ReadAsStringAsync());
            Assert.True(h.App.Cookies.ContainsKey(MainCookie));
        }

        [Theory]
        [InlineData("invalid")]
        [InlineData("wrong")]
        public async Task Reject_InvalidOrForeignProof_Guest(string kind)
        {
            await using var h = await StartAsync();
            await SignInAndBindAsync(h, "s-a");
            var foreignProof = h.App.Cookies[ProofCookie];
            await SignInAndBindAsync(h, Sid);

            h.App.SetCookie(ProofCookie, kind == "invalid" ? "garbage" : foreignProof);

            Assert.False(await IsAuthenticatedAsync(h.App));
            Assert.Equal(kind == "invalid" ? DeviceProofFailureReason.Invalid : DeviceProofFailureReason.WrongSession, h.Handler.Calls.Last().Reason);
        }

        [Fact]
        public async Task ProofCookieForAnotherKey_Invalid()
        {
            await using var h = await StartAsync();
            await SignInAndBindAsync(h);
            h.App.SetCookie(ProofCookie, h.ProofCookies.CreateValue(Sid, "thumbprint-of-another-key"));

            Assert.False(await IsAuthenticatedAsync(h.App));
            Assert.Equal(DeviceProofFailureReason.Invalid, Assert.Single(h.Handler.Calls).Reason);
        }

        [Fact]
        public async Task ReportOnly_MissingProof_StillAuthenticated_AndCounted()
        {
            var measurements = new List<(long Value, string Reason, string Mode)>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == DeviceBoundMetrics.ProofFailuresName)
                    l.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var tagList = tags.ToArray();
                lock (measurements)
                    measurements.Add((value, (string)tagList.Single(t => t.Key == "reason").Value!, (string)tagList.Single(t => t.Key == "mode").Value!));
            });
            listener.Start();
            await using var h = await StartAsync(o => o.FailureMode = DeviceProofFailureMode.ReportOnly);
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            Assert.True(await IsAuthenticatedAsync(h.App));
            lock (measurements)
                Assert.Contains((1L, "missing", "report_only"), measurements);
        }

        [Fact]
        public async Task SignOut_MissingProof_RevokesAndDeletesBothCookies()
        {
            await using var h = await StartAsync(o => o.FailureMode = DeviceProofFailureMode.SignOut);
            await SignInAndBindAsync(h);
            h.App.SetCookie(ProofCookie, "garbage");

            using var response = await h.App.SendAsync(HttpMethod.Get, "/whoami");

            Assert.StartsWith("auth=false", await response.Content.ReadAsStringAsync());
            Assert.Equal(1, h.App.Api.LogoutCalls);
            Assert.False(h.App.Cookies.ContainsKey(MainCookie));
            Assert.False(h.App.Cookies.ContainsKey(ProofCookie));
        }

        [Fact]
        public async Task Grace_ExpiredProofWithinGrace_Valid_ThenRejected()
        {
            await using var h = await StartAsync();
            await SignInAndBindAsync(h);

            h.Clock.Advance(TimeSpan.FromMinutes(11));
            Assert.True(await IsAuthenticatedAsync(h.App));
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.False(await IsAuthenticatedAsync(h.App));
        }

        [Fact]
        public async Task ExcludedPath_NotEnforced()
        {
            await using var h = await StartAsync(o => o.ExcludedPaths = ["/public"]);
            var config = await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            Assert.True(await IsAuthenticatedAsync(h.App, "/public/whoami"));
            Assert.False(await IsAuthenticatedAsync(h.App, "/whoami"));
            var rule = Assert.Single(config.GetProperty("scope").GetProperty("scope_specification").EnumerateArray().ToList());
            Assert.Equal("exclude", rule.GetProperty("type").GetString());
            Assert.Equal("/public", rule.GetProperty("path").GetString());
        }

        [Fact]
        public async Task BindingCacheHit_MeFailing_StillEnforced()
        {
            await using var h = await StartAsync(o => o.AllowUserChoice = true); // soft policy: `me` is called first
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);
            h.App.Api.Me = () => throw new HttpRequestException("API down", null, HttpStatusCode.BadGateway);
            var meCalls = h.App.Api.MeCalls;

            Assert.False(await IsAuthenticatedAsync(h.App));
            Assert.Equal(meCalls + 1, h.App.Api.MeCalls);
            var failure = Assert.Single(h.Handler.Calls);
            Assert.Null(failure.User);
            Assert.Equal(DeviceProofFailureMode.Reject, failure.DefaultMode);
        }

        [Fact]
        public async Task SkipCurrentUser_BoundSession_StillEnforced()
        {
            await using var h = await StartAsync(o => o.AllowUserChoice = true,
                configureWeb: o => o.SkipCurrentUser = context => context.Request.Path.StartsWithSegments("/whoami"));
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);
            var meCalls = h.App.Api.MeCalls;

            Assert.False(await IsAuthenticatedAsync(h.App));
            Assert.Equal(meCalls, h.App.Api.MeCalls);
            Assert.Single(h.Handler.Calls);
        }

        /// <summary>Signs in a session that is bound at the API but unknown to this instance (cold instance).</summary>
        private static async Task SignInBoundAtApiOnlyAsync(Harness h)
        {
            h.KeyApi.ThrowAfterStore = true; // the API stores the key, the web never learns it
            using var signIn = await h.App.SignInAsync(Sid);
            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(ChallengeFrom(RegistrationHeader(signIn)!)));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
            Assert.Null(h.App.Services.GetRequiredService<DeviceBindingCache>().Get(Sid));
            Assert.Null(h.App.Services.GetRequiredService<CurrentUserCache>().Get(Sid));
        }

        [Fact]
        public async Task SkipCurrentUser_ColdInstance_BoundAtApi_LearnsBindingFromMe_Enforced()
        {
            await using var h = await StartAsync(configureWeb: o => o.SkipCurrentUser = context => context.Request.Path.StartsWithSegments("/call"));
            await SignInBoundAtApiOnlyAsync(h);
            var meCalls = h.App.Api.MeCalls;

            using var response = await h.App.SendAsync(HttpMethod.Get, "/call");

            // The skipped path still asks `me` for the unknown binding: no token without proof.
            Assert.Equal("none", await response.Content.ReadAsStringAsync());
            Assert.Equal(meCalls + 1, h.App.Api.MeCalls);
            Assert.Equal(DeviceProofFailureReason.Missing, Assert.Single(h.Handler.Calls).Reason);
            // Now known: the next skipped request is enforced without `me`.
            using var again = await h.App.SendAsync(HttpMethod.Get, "/call");
            Assert.Equal("none", await again.Content.ReadAsStringAsync());
            Assert.Equal(meCalls + 1, h.App.Api.MeCalls);
        }

        [Fact]
        public async Task SkipCurrentUser_UnboundSession_MeAskedOncePerRecheckInterval()
        {
            await using var h = await StartAsync(configureWeb: o =>
            {
                o.SkipCurrentUser = context => context.Request.Path.StartsWithSegments("/whoami");
                o.CurrentUserCacheLifetime = TimeSpan.FromMilliseconds(1); // the user cache does not help
            });
            await h.App.SignInAsync(Sid);
            var meCalls = h.App.Api.MeCalls;

            Assert.True(await IsAuthenticatedAsync(h.App));
            Assert.Equal(meCalls + 1, h.App.Api.MeCalls);
            await Task.Delay(20);
            Assert.True(await IsAuthenticatedAsync(h.App));
            Assert.True(await IsAuthenticatedAsync(h.App));
            // "Not bound" is trusted for UnboundRecheckInterval (30 s): no `me` call just for the binding.
            Assert.Equal(meCalls + 1, h.App.Api.MeCalls);

            h.Clock.Advance(TimeSpan.FromSeconds(31));
            Assert.True(await IsAuthenticatedAsync(h.App));
            Assert.Equal(meCalls + 2, h.App.Api.MeCalls);
            Assert.Empty(h.Handler.Calls);
        }

        [Fact]
        public async Task CachedUserSaysUnbound_BoundElsewhere_RecheckedAfterInterval_Enforced()
        {
            await using var h = await StartAsync(configureWeb: o => o.CurrentUserCacheLifetime = TimeSpan.FromMinutes(10));
            await h.App.SignInAsync(Sid);
            Assert.True(await IsAuthenticatedAsync(h.App)); // `me`: not bound; cached for 10 minutes
            var meCalls = h.App.Api.MeCalls;

            // Another instance registers the key: the API now reports the session as bound.
            h.KeyApi.StoredJwk = h.Device.Jwk().ToJsonString();
            h.KeyApi.StoredAlgorithm = h.Device.Algorithm;

            // Residual window: "not bound" is still trusted within UnboundRecheckInterval.
            Assert.True(await IsAuthenticatedAsync(h.App));
            Assert.Equal(meCalls, h.App.Api.MeCalls);

            // After it the stale cached user no longer decides the binding: `me` is asked again and the proof enforced.
            h.Clock.Advance(TimeSpan.FromSeconds(31));
            Assert.False(await IsAuthenticatedAsync(h.App));
            Assert.Equal(meCalls + 1, h.App.Api.MeCalls);
            Assert.Equal(DeviceProofFailureReason.Missing, Assert.Single(h.Handler.Calls).Reason);
        }

        [Fact]
        public async Task CachedUserSaysUnbound_RecheckFails_CachedUserStillApplies()
        {
            await using var h = await StartAsync(configureWeb: o => o.CurrentUserCacheLifetime = TimeSpan.FromMinutes(10));
            await h.App.SignInAsync(Sid);
            Assert.True(await IsAuthenticatedAsync(h.App));
            h.App.Api.Me = () => throw new HttpRequestException("API down", null, HttpStatusCode.BadGateway);
            h.Clock.Advance(TimeSpan.FromSeconds(31));

            var whoami = await h.App.WhoAmIAsync();

            Assert.StartsWith("auth=true", whoami);
            Assert.Contains("editor", whoami); // the cached full user, not the session-only principal
        }

        [Fact]
        public async Task ThrowFailureMode_SoftPolicy_MeFailing_EnforcementRunsFirst_GuestWithoutToken()
        {
            await using var h = await StartAsync(o => o.AllowUserChoice = true,
                configureWeb: o => o.CurrentUserFailureMode = CurrentUserFailureMode.Throw);
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);
            h.App.Api.Me = () => throw new HttpRequestException("API down", null, HttpStatusCode.BadGateway);

            using var response = await h.App.SendAsync(HttpMethod.Get, "/call");

            // No CurrentUserUnavailableException: the proof failure made the request a guest first.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("none", await response.Content.ReadAsStringAsync());
            Assert.Single(h.Handler.Calls);
        }

        [Fact]
        public async Task BindingUnknown_MeSaysBound_LearnedFromMe_ThenDecidedBeforeMe()
        {
            await using var h = await StartAsync(o => o.FailureMode = DeviceProofFailureMode.ReportOnly);
            h.KeyApi.ThrowAfterStore = true; // bound at the API, unknown to the web
            using var signIn = await h.App.SignInAsync(Sid);
            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(ChallengeFrom(RegistrationHeader(signIn)!)));

            // Unknown binding: `me` decides (ReportOnly keeps the request authenticated, the failure is reported).
            Assert.True(await IsAuthenticatedAsync(h.App));
            var first = Assert.Single(h.Handler.Calls);
            Assert.NotNull(first.User);
            // Now known: the next failure is decided before `me` (no user).
            h.App.Services.GetRequiredService<CurrentUserCache>().Remove(Sid);
            Assert.True(await IsAuthenticatedAsync(h.App));
            Assert.Null(h.Handler.Calls[1].User);
        }

        [Fact]
        public async Task Unbound_ApiSaysNotBound_NoProofNeeded()
        {
            await using var h = await StartAsync();
            await h.App.SignInAsync(Sid);

            Assert.True(await IsAuthenticatedAsync(h.App));
        }

        // ---------- failure handler: default policy ----------

        [Fact]
        public async Task FixedPolicy_IgnoresUserChoice_HandlerGetsNoUser()
        {
            await using var h = await StartAsync(o => o.FailureMode = DeviceProofFailureMode.Reject);
            h.App.Api.Me = () => h.User("ReportOnly");
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            Assert.False(await IsAuthenticatedAsync(h.App));
            var failure = Assert.Single(h.Handler.Calls);
            Assert.Null(failure.User);
            Assert.Equal(DeviceProofFailureMode.Reject, failure.DefaultMode);
        }

        [Theory]
        [InlineData("ReportOnly", true)]
        [InlineData("reportonly", true)]
        [InlineData("Reject", false)]
        public async Task UserChoice_UserModeWins(string userMode, bool authenticated)
        {
            await using var h = await StartAsync(o =>
            {
                o.AllowUserChoice = true;
                o.FailureMode = userMode == "Reject" ? DeviceProofFailureMode.ReportOnly : DeviceProofFailureMode.Reject;
            });
            h.App.Api.Me = () => h.User(userMode);
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            Assert.Equal(authenticated, await IsAuthenticatedAsync(h.App));
            Assert.Equal(userMode, Assert.Single(h.Handler.Calls).User!.DeviceBindingMode);
        }

        [Fact]
        public async Task UserChoice_SignOutChosenByUser_SignsOut()
        {
            await using var h = await StartAsync(o =>
            {
                o.AllowUserChoice = true;
                o.FailureMode = DeviceProofFailureMode.ReportOnly;
            });
            h.App.Api.Me = () => h.User("SignOut");
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            Assert.False(await IsAuthenticatedAsync(h.App));
            Assert.Equal(1, h.App.Api.LogoutCalls);
            Assert.False(h.App.Cookies.ContainsKey(MainCookie));
        }

        [Theory]
        [InlineData("ReportOnly")] // valid name, but not allowed
        [InlineData("bogus")]
        [InlineData("0")]          // numeric value of ReportOnly
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task UserChoice_DisallowedUnknownOrEmptyValue_FallsBackToDefault(string? userMode)
        {
            await using var h = await StartAsync(o =>
            {
                o.AllowUserChoice = true;
                o.FailureMode = DeviceProofFailureMode.Reject;
                o.AllowedUserModes = new HashSet<DeviceProofFailureMode> { DeviceProofFailureMode.Reject, DeviceProofFailureMode.SignOut };
            });
            h.App.Api.Me = () => h.User(userMode);
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            Assert.False(await IsAuthenticatedAsync(h.App));
            Assert.Equal(0, h.App.Api.LogoutCalls);
            Assert.Equal(DeviceProofFailureMode.Reject, Assert.Single(h.Handler.Calls).DefaultMode);
        }

        // ---------- failure handler: overrides ----------

        [Theory]
        [InlineData(DeviceProofOutcome.Continue, true, 0)]
        [InlineData(DeviceProofOutcome.Reject, false, 0)]
        [InlineData(DeviceProofOutcome.SignOut, false, 1)]
        public async Task Handler_Outcome_IsApplied(DeviceProofOutcome outcome, bool authenticated, int logouts)
        {
            await using var h = await StartAsync(o => o.FailureMode = DeviceProofFailureMode.ReportOnly);
            h.Handler.Outcome = _ => ValueTask.FromResult(outcome);
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            Assert.Equal(authenticated, await IsAuthenticatedAsync(h.App));
            Assert.Equal(logouts, h.App.Api.LogoutCalls);
            Assert.Equal(logouts == 0, h.App.Cookies.ContainsKey(MainCookie));
        }

        [Fact]
        public async Task Handler_Handled_OwnResponse_ShortCircuits_NoApiCall_CookiesKept()
        {
            await using var h = await StartAsync();
            h.Handler.Outcome = async context =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.HttpContext.Response.WriteAsync("device proof required");
                return DeviceProofOutcome.Handled;
            };
            await SignInAndBindAsync(h);
            h.App.SetCookie(ProofCookie, "garbage");
            var meCalls = h.App.Api.MeCalls;

            using var response = await h.App.SendAsync(HttpMethod.Get, "/call");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("device proof required", await response.Content.ReadAsStringAsync());
            Assert.Empty(h.CallAuthorizations);
            Assert.Equal(meCalls, h.App.Api.MeCalls);
            Assert.Empty(WebAuthTestApp.SetCookies(response));
            Assert.Equal("garbage", h.App.Cookies[ProofCookie]);
            Assert.True(h.App.Cookies.ContainsKey(MainCookie));
        }

        [Fact]
        public async Task Handler_HandledWithoutResponse_Answers403WithoutBody()
        {
            await using var h = await StartAsync();
            h.Handler.Outcome = _ => ValueTask.FromResult(DeviceProofOutcome.Handled);
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            using var response = await h.App.SendAsync(HttpMethod.Get, "/call");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
            Assert.Empty(h.CallAuthorizations);
        }

        [Fact]
        public async Task Handler_SignOutAfterStartingTheResponse_TreatedAsHandled()
        {
            await using var h = await StartAsync();
            h.Handler.Outcome = async context =>
            {
                await context.HttpContext.Response.WriteAsync("custom");
                return DeviceProofOutcome.SignOut;
            };
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            using var response = await h.App.SendAsync(HttpMethod.Get, "/call");

            // No exception from deleting cookies on read-only headers: the handler's response stands, nothing else runs.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("custom", await response.Content.ReadAsStringAsync());
            Assert.Empty(h.CallAuthorizations);
            Assert.Equal(0, h.App.Api.LogoutCalls);
            Assert.True(h.App.Cookies.ContainsKey(MainCookie));
        }

        [Fact]
        public async Task Handler_CancelledByAbortedRequest_RequestIsRejectedBeforeRethrow()
        {
            await using var h = await StartAsync();
            WebSessionTicket? ticket = null;
            h.Handler.Outcome = context =>
            {
                ticket = WebSessionTicket.For(context.HttpContext);
                context.HttpContext.Abort();
                context.HttpContext.RequestAborted.ThrowIfCancellationRequested();
                return ValueTask.FromResult(DeviceProofOutcome.Continue);
            };
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                using var _ = await h.App.SendAsync(HttpMethod.Get, "/call");
            });

            Assert.NotNull(ticket);
            Assert.True(ticket!.Rejected);
            Assert.Empty(h.CallAuthorizations);
        }

        [Fact]
        public async Task Handler_Handled_SoftPolicy_UserNotCached()
        {
            await using var h = await StartAsync(o => o.AllowUserChoice = true);
            h.Handler.Outcome = context =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                return ValueTask.FromResult(DeviceProofOutcome.Handled);
            };
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            using var response = await h.App.SendAsync(HttpMethod.Get, "/whoami");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Null(h.App.Services.GetRequiredService<CurrentUserCache>().Get(Sid));
        }

        [Fact]
        public async Task Handler_Throws_DefaultOutcomeApplies()
        {
            await using var h = await StartAsync(o => o.FailureMode = DeviceProofFailureMode.Reject);
            h.Handler.Outcome = _ => throw new InvalidOperationException("handler bug");
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            Assert.False(await IsAuthenticatedAsync(h.App));
            Assert.True(h.App.Cookies.ContainsKey(MainCookie));
        }

        [Fact]
        public async Task Handler_ThrowsWithReportOnlyDefault_RequestContinues()
        {
            await using var h = await StartAsync(o => o.FailureMode = DeviceProofFailureMode.ReportOnly);
            h.Handler.Outcome = _ => throw new InvalidOperationException("handler bug");
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            Assert.True(await IsAuthenticatedAsync(h.App));
        }

        [Theory]
        [InlineData(99)]
        [InlineData(-1)]
        public async Task Handler_UndefinedOutcome_DefaultOutcomeApplies(int value)
        {
            await using var h = await StartAsync(o => o.FailureMode = DeviceProofFailureMode.ReportOnly);
            h.Handler.Outcome = _ => ValueTask.FromResult((DeviceProofOutcome)value);
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            Assert.True(await IsAuthenticatedAsync(h.App));
        }

        private sealed class StricterForInvalid : DeviceProofFailureHandler
        {
            public override async ValueTask<DeviceProofOutcome> HandleAsync(DeviceProofFailureContext context)
                => context.Reason == DeviceProofFailureReason.Invalid ? DeviceProofOutcome.SignOut : await base.HandleAsync(context);
        }

        [Theory]
        [InlineData(false, true, 0)]  // missing proof: base → ReportOnly
        [InlineData(true, false, 1)]  // invalid proof: overridden → SignOut
        public async Task Handler_SubclassCallsBase(bool invalid, bool authenticated, int logouts)
        {
            var keyApi = new FakeDeviceKeyApi();
            await using var app = await WebAuthTestApp.StartAsync(o =>
            {
                o.DeviceBound.Enabled = true;
                o.DeviceBound.FailureMode = DeviceProofFailureMode.ReportOnly;
            }, services =>
            {
                services.AddSingleton<IDeviceKeyApiClient>(keyApi);
                services.AddSingleton<DeviceProofFailureHandler, StricterForInvalid>();
            }, https: true);
            app.Api.Me = () => new CurrentUserInfo { Id = "u-1", UserName = "editor", DeviceKeyJwk = keyApi.StoredJwk, DeviceKeyAlgorithm = keyApi.StoredAlgorithm };
            using var device = DbscTestDevice.Es256();
            using var signIn = await app.SignInAsync(Sid);
            using var start = await PostAsync(app, "/dbsc/start", device.RegistrationJwt(ChallengeFrom(RegistrationHeader(signIn)!)));
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            if (invalid)
                app.SetCookie(ProofCookie, "garbage");
            else
                app.RemoveCookie(ProofCookie);

            Assert.Equal(authenticated, await IsAuthenticatedAsync(app));
            Assert.Equal(logouts, app.Api.LogoutCalls);
        }

        [Fact]
        public void DefaultHandler_MapsEveryModeToAnOutcome()
        {
            Assert.Equal(DeviceProofOutcome.Continue, DeviceProofFailureHandler.ToOutcome(DeviceProofFailureMode.ReportOnly));
            Assert.Equal(DeviceProofOutcome.Reject, DeviceProofFailureHandler.ToOutcome(DeviceProofFailureMode.Reject));
            Assert.Equal(DeviceProofOutcome.SignOut, DeviceProofFailureHandler.ToOutcome(DeviceProofFailureMode.SignOut));
            Assert.All(Enum.GetValues<DeviceProofFailureMode>(), m => Assert.NotEqual(DeviceProofOutcome.Handled, DeviceProofFailureHandler.ToOutcome(m)));
        }

        // ---------- logout, disabled, startup ----------

        [Fact]
        public async Task SignOut_DeletesProofCookie_AndClearSiteDataWhenAsked()
        {
            await using var h = await StartAsync(o => o.ClearSiteDataOnSignOut = true);
            await SignInAndBindAsync(h);

            using var response = await h.App.SendAsync(HttpMethod.Get, "/sign-out");

            Assert.Contains(WebAuthTestApp.SetCookies(response), c => c.StartsWith(ProofCookie + "=;", StringComparison.Ordinal));
            Assert.False(h.App.Cookies.ContainsKey(ProofCookie));
            Assert.Equal("\"cookies\"", response.Headers.GetValues("Clear-Site-Data").Single());
        }

        [Fact]
        public async Task Disabled_BehavesAsBefore()
        {
            await using var h = await StartAsync(enabled: false);
            h.KeyApi.StoredJwk = "{\"kty\":\"EC\"}";

            using var signIn = await h.App.SignInAsync(Sid);
            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt("c"));
            using var signOut = await h.App.SendAsync(HttpMethod.Get, "/sign-out");

            Assert.Null(RegistrationHeader(signIn));
            Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
            Assert.DoesNotContain(WebAuthTestApp.SetCookies(signOut), c => c.StartsWith(ProofCookie, StringComparison.Ordinal));
            Assert.False(signOut.Headers.Contains("Clear-Site-Data"));
            Assert.Empty(h.KeyApi.Calls);
        }

        [Fact]
        public async Task Disabled_IgnoresABoundSession()
        {
            await using var h = await StartAsync(enabled: false);
            h.KeyApi.StoredJwk = "{\"kty\":\"EC\"}";
            h.KeyApi.StoredAlgorithm = "ES256";
            await h.App.SignInAsync(Sid);

            Assert.True(await IsAuthenticatedAsync(h.App));
        }

        [Fact]
        public async Task Enabled_WithoutDeviceKeyClient_FailsAtStartup()
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => WebAuthTestApp.StartAsync(o => o.DeviceBound.Enabled = true));

            Assert.Contains("IDeviceKeyApiClient", error.ToString());
        }

        [Fact]
        public async Task EnabledOutsideAddTqkWebAuth_WithoutDeviceKeyClient_FailsAtStartup()
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => WebAuthTestApp.StartAsync(
                configureServices: services => services.Configure<WebAuthOptions>(o => o.DeviceBound.Enabled = true)));

            Assert.Contains("IDeviceKeyApiClient", error.ToString());
        }

        [Fact]
        public async Task EnabledOutsideAddTqkWebAuth_Works()
        {
            await using var app = await WebAuthTestApp.StartAsync(configureServices: services =>
            {
                services.Configure<WebAuthOptions>(o => o.DeviceBound.Enabled = true);
                services.AddSingleton<IDeviceKeyApiClient>(new FakeDeviceKeyApi());
            }, https: true);

            using var signIn = await app.SignInAsync(Sid);

            Assert.NotNull(RegistrationHeader(signIn));
        }

        [Fact]
        public async Task Enabled_InvalidOptions_FailsAtStartup()
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(o => o.ProofLifetime = TimeSpan.FromSeconds(10)));

            Assert.Contains("ProofLifetime", error.ToString());
        }
    }
}
