using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using TqkLibrary.Asp.ToolKit.Auth.Web.Interfaces;
using TqkLibrary.Asp.ToolKit.Auth.Web.Models;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Tests
{
    /// <summary>DBSC end to end through the real cookie pipeline (registration, refresh, enforcement, failure modes).</summary>
    public class DeviceBoundSessionPipelineTests
    {
        private const string Sid = "s-1";
        private const string MainCookie = ".AspNetCore.Cookies";
        private const string ProofCookie = MainCookie + ".proof";

        private sealed class FakeDeviceKeyApi : IDeviceKeyApiClient
        {
            public bool Result { get; set; } = true;
            public List<(string Jwk, string Algorithm)> Calls { get; } = [];

            public Task<bool> RegisterDeviceKeyAsync(string jwk, string algorithm, CancellationToken cancellationToken)
            {
                lock (Calls)
                    Calls.Add((jwk, algorithm));
                return Task.FromResult(Result);
            }
        }

        private sealed class Harness : IAsyncDisposable
        {
            public required WebAuthTestApp App { get; init; }
            public required FakeDeviceKeyApi KeyApi { get; init; }
            public required FakeTimeProvider Clock { get; init; }
            public DbscTestDevice Device { get; } = DbscTestDevice.Es256();

            public async ValueTask DisposeAsync()
            {
                Device.Dispose();
                await App.DisposeAsync();
            }
        }

        private static async Task<Harness> StartAsync(Action<DeviceBoundSessionOptions>? configure = null, bool https = true,
            bool enabled = true, Action<Microsoft.AspNetCore.Routing.IEndpointRouteBuilder>? mapEndpoints = null)
        {
            var keyApi = new FakeDeviceKeyApi();
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var app = await WebAuthTestApp.StartAsync(o =>
            {
                o.DeviceBound.Enabled = enabled;
                configure?.Invoke(o.DeviceBound);
            }, services =>
            {
                services.AddSingleton<IDeviceKeyApiClient>(keyApi);
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
            }, endpoints =>
            {
                endpoints.MapGet("/sign-out", async (HttpContext context, WebSessionManager sessions) =>
                {
                    await sessions.SignOutAsync(context);
                    return Results.Ok();
                });
                endpoints.MapGet("/public/whoami", (HttpContext context) => Results.Text(
                    "auth=" + (context.User.Identity?.IsAuthenticated == true).ToString().ToLowerInvariant()));
                mapEndpoints?.Invoke(endpoints);
            }, https: https);
            return new Harness { App = app, KeyApi = keyApi, Clock = clock };
        }

        private static string? RegistrationHeader(HttpResponseMessage response)
            => response.Headers.TryGetValues("Secure-Session-Registration", out var values) ? values.Single() : null;

        private static string ChallengeFrom(string header) => Regex.Match(header, "challenge=\"([^\"]*)\"").Groups[1].Value;

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
        public async Task Start_ValidProof_StoresKey_ReissuesCookie_SetsProof_ReturnsConfig()
        {
            await using var h = await StartAsync();
            using var signIn = await h.App.SignInAsync(Sid, persistent: true);
            var mainExpires = Regex.Match(WebAuthTestApp.SetCookies(signIn).Single(c => c.StartsWith(MainCookie + "=")), "expires=([^;]+)").Groups[1].Value;
            var challenge = ChallengeFrom(RegistrationHeader(signIn)!);

            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(challenge));

            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            var call = Assert.Single(h.KeyApi.Calls);
            Assert.Equal("ES256", call.Algorithm);
            Assert.Equal(h.Device.Jwk()["x"]!.GetValue<string>(), JsonDocument.Parse(call.Jwk).RootElement.GetProperty("x").GetString());

            var setCookies = WebAuthTestApp.SetCookies(start);
            var main = Assert.Single(setCookies, c => c.StartsWith(MainCookie + "="));
            // Reissued once, same absolute expiry and persistence as at sign-in.
            Assert.Equal(mainExpires, Regex.Match(main, "expires=([^;]+)").Groups[1].Value);
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
        public async Task Start_SecondTime_403()
        {
            await using var h = await StartAsync();
            await SignInAndBindAsync(h);

            using var again = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt("whatever"));

            Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
            Assert.Single(h.KeyApi.Calls);
        }

        [Fact]
        public async Task Start_ApiRefuses_403_NoProof_SessionStaysUnbound()
        {
            await using var h = await StartAsync();
            h.KeyApi.Result = false;
            using var signIn = await h.App.SignInAsync(Sid);

            using var start = await PostAsync(h.App, "/dbsc/start", h.Device.RegistrationJwt(ChallengeFrom(RegistrationHeader(signIn)!)));

            Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
            Assert.Empty(WebAuthTestApp.SetCookies(start));
            Assert.False(h.App.Cookies.ContainsKey(ProofCookie));
            // Unbound: no proof needed, and refresh ends the browser session.
            Assert.True(await IsAuthenticatedAsync(h.App));
            using var refresh = await PostAsync(h.App, "/dbsc/refresh", sessionId: DeviceBoundSessionMiddleware.SessionIdentifier(Sid));
            Assert.False(JsonDocument.Parse(await refresh.Content.ReadAsStringAsync()).RootElement.GetProperty("continue").GetBoolean());
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

            Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);
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
            var challenge = Regex.Match(first.Headers.GetValues("Secure-Session-Challenge").Single(), "^\"([^\"]+)\"").Groups[1].Value;
            h.Clock.Advance(TimeSpan.FromMinutes(2));

            using var late = await PostAsync(h.App, "/dbsc/refresh", h.Device.RefreshJwt(challenge), identifier);

            Assert.Equal(HttpStatusCode.Forbidden, late.StatusCode);
            Assert.NotEqual(challenge, Regex.Match(late.Headers.GetValues("Secure-Session-Challenge").Single(), "^\"([^\"]+)\"").Groups[1].Value);
        }

        [Fact]
        public async Task Refresh_BadSignatureOrJwkPresent_401()
        {
            await using var h = await StartAsync();
            var identifier = (await SignInAndBindAsync(h)).GetProperty("session_identifier").GetString()!;
            using var first = await PostAsync(h.App, "/dbsc/refresh", sessionId: identifier);
            var challenge = Regex.Match(first.Headers.GetValues("Secure-Session-Challenge").Single(), "^\"([^\"]+)\"").Groups[1].Value;
            using var thief = DbscTestDevice.Es256();

            using var foreignKey = await PostAsync(h.App, "/dbsc/refresh", thief.RefreshJwt(challenge), identifier);
            using var withJwk = await PostAsync(h.App, "/dbsc/refresh", h.Device.RegistrationJwt(challenge), identifier);

            Assert.Equal(HttpStatusCode.Unauthorized, foreignKey.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, withJwk.StatusCode);
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
            Assert.False(JsonDocument.Parse(await afterLogout.Content.ReadAsStringAsync()).RootElement.GetProperty("continue").GetBoolean());
        }

        // ---------- enforcement & failure modes ----------

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
        public async Task Reject_GuestRequest_DoesNotSendTheSessionToken()
        {
            string? authorization = null;
            await using var h = await StartAsync(mapEndpoints: endpoints => endpoints.MapGet("/call", async (HttpContext context) =>
            {
                var handler = context.RequestServices.GetRequiredService<SessionBearerHandler>();
                var echo = new BearerEcho();
                handler.InnerHandler = echo;
                using var client = new HttpClient(handler);
                using var _ = await client.GetAsync("http://api/items");
                authorization = echo.LastAuthorization;
                return Results.Ok();
            }));
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            using var response = await h.App.SendAsync(HttpMethod.Get, "/call");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(authorization);
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

        // ---------- policy: fixed vs user choice, hook ----------

        [Fact]
        public async Task FixedPolicy_IgnoresUserChoice()
        {
            await using var h = await StartAsync(o => o.FailureMode = DeviceProofFailureMode.Reject);
            h.App.Api.Me = () => new CurrentUserInfo { Id = "u-1", UserName = "editor", DeviceBindingMode = "ReportOnly" };
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            Assert.False(await IsAuthenticatedAsync(h.App));
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
            h.App.Api.Me = () => new CurrentUserInfo { Id = "u-1", UserName = "editor", DeviceBindingMode = userMode };
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            Assert.Equal(authenticated, await IsAuthenticatedAsync(h.App));
        }

        [Fact]
        public async Task UserChoice_SignOutChosenByUser_SignsOut()
        {
            await using var h = await StartAsync(o =>
            {
                o.AllowUserChoice = true;
                o.FailureMode = DeviceProofFailureMode.ReportOnly;
            });
            h.App.Api.Me = () => new CurrentUserInfo { Id = "u-1", UserName = "editor", DeviceBindingMode = "SignOut" };
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
        public async Task UserChoice_DisallowedOrUnknownValue_FallsBackToDefault(string userMode)
        {
            await using var h = await StartAsync(o =>
            {
                o.AllowUserChoice = true;
                o.FailureMode = DeviceProofFailureMode.Reject;
                o.AllowedUserModes = new HashSet<DeviceProofFailureMode> { DeviceProofFailureMode.Reject, DeviceProofFailureMode.SignOut };
            });
            h.App.Api.Me = () => new CurrentUserInfo { Id = "u-1", UserName = "editor", DeviceBindingMode = userMode };
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            Assert.False(await IsAuthenticatedAsync(h.App));
            Assert.Equal(0, h.App.Api.LogoutCalls);
        }

        [Fact]
        public async Task Hook_RunsLast_AndChangesTheDecision()
        {
            DeviceProofFailureContext? seen = null;
            await using var h = await StartAsync(o =>
            {
                o.AllowUserChoice = true;
                o.FailureMode = DeviceProofFailureMode.SignOut;
                o.OnProofFailure = ctx =>
                {
                    seen = ctx;
                    return ValueTask.FromResult(DeviceProofFailureMode.ReportOnly);
                };
            });
            h.App.Api.Me = () => new CurrentUserInfo { Id = "u-1", UserName = "editor", DeviceBindingMode = "Reject" };
            await SignInAndBindAsync(h);
            h.App.RemoveCookie(ProofCookie);

            Assert.True(await IsAuthenticatedAsync(h.App));
            Assert.NotNull(seen);
            Assert.Equal(DeviceProofFailureReason.Missing, seen!.Reason);
            Assert.Equal(DeviceProofFailureMode.Reject, seen.Mode); // user choice already applied
            Assert.Equal(Sid, seen.SessionId);
            Assert.Equal("Reject", seen.User!.DeviceBindingMode);
        }

        [Fact]
        public async Task Hook_FixedPolicy_GetsNoUser_AndCanReject()
        {
            DeviceProofFailureContext? seen = null;
            await using var h = await StartAsync(o =>
            {
                o.FailureMode = DeviceProofFailureMode.ReportOnly;
                o.OnProofFailure = ctx =>
                {
                    seen = ctx;
                    return ValueTask.FromResult(DeviceProofFailureMode.Reject);
                };
            });
            await SignInAndBindAsync(h);
            h.App.SetCookie(ProofCookie, "garbage");

            Assert.False(await IsAuthenticatedAsync(h.App));
            Assert.Equal(DeviceProofFailureReason.Invalid, seen!.Reason);
            Assert.Null(seen.User);
        }

        // ---------- downgrade, logout, disabled, startup ----------

        [Fact]
        public async Task Downgrade_ApiSaysBound_TicketWithoutKey_Guest_UserNotCached()
        {
            await using var h = await StartAsync(o => o.FailureMode = DeviceProofFailureMode.ReportOnly);
            h.App.Api.Me = () => new CurrentUserInfo { Id = "u-1", UserName = "editor", IsDeviceBound = true };
            await h.App.SignInAsync(Sid); // never registered: ticket has no key

            Assert.False(await IsAuthenticatedAsync(h.App));
            Assert.False(await IsAuthenticatedAsync(h.App));
            Assert.Equal(2, h.App.Api.MeCalls);
            Assert.True(h.App.Cookies.ContainsKey(MainCookie));
        }

        [Fact]
        public async Task Unbound_ApiSaysNotBound_NoProofNeeded()
        {
            await using var h = await StartAsync();
            await h.App.SignInAsync(Sid);

            Assert.True(await IsAuthenticatedAsync(h.App));
        }

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
            h.App.Api.Me = () => new CurrentUserInfo { Id = "u-1", UserName = "editor", IsDeviceBound = true };

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
        public async Task Disabled_IgnoresIsDeviceBound()
        {
            await using var h = await StartAsync(enabled: false);
            h.App.Api.Me = () => new CurrentUserInfo { Id = "u-1", UserName = "editor", IsDeviceBound = true };
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
        public async Task Enabled_InvalidOptions_FailsAtStartup()
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(o => o.ProofLifetime = TimeSpan.FromSeconds(10)));

            Assert.Contains("ProofLifetime", error.ToString());
        }
    }
}
