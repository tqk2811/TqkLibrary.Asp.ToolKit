using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using TqkLibrary.Asp.ToolKit.Auth.Api.Extensions;
using TqkLibrary.Asp.ToolKit.Auth.Api.Interfaces;
using TqkLibrary.Asp.ToolKit.Auth.Api.Models;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Tests
{
    /// <summary><c>AddTqkApiAuth</c> + <c>AddTqkSessionToken</c> in a TestServer: the session check runs on every request.</summary>
    public class SessionTokenPipelineTests
    {
        private const string RejectedHeader = "X-Session-Rejected";
        private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private sealed class FakeSessionSource : ISessionAccessSource
        {
            private int _calls;
            private int _touches;
            public int Calls => _calls;
            public int Touches => _touches;
            public ConcurrentDictionary<string, SessionAccessRecord> Sessions { get; } = new();
            public List<(string SessionId, DateTime LastAccessAt, DateTime ExpiresAt)> TouchLog { get; } = new();

            /// <summary>When set, wins over <see cref="Sessions"/>.</summary>
            public Func<string, SessionAccessResult>? Result { get; set; }

            /// <summary>When set, <see cref="TouchAsync"/> throws it.</summary>
            public Exception? TouchError { get; set; }

            /// <summary>When set, <see cref="TouchAsync"/> waits for it.</summary>
            public TaskCompletionSource? TouchGate { get; set; }

            public Task<SessionAccessResult> GetAsync(string sessionId, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _calls);
                if (Result is not null)
                    return Task.FromResult(Result(sessionId));
                return Task.FromResult(Sessions.TryGetValue(sessionId, out var record) ? SessionAccessResult.Grant(record) : SessionAccessResult.Reject());
            }

            public async Task TouchAsync(string sessionId, DateTime lastAccessAt, DateTime expiresAt, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _touches);
                if (TouchGate is { } gate)
                    await gate.Task;
                if (TouchError is { } error)
                    throw error;
                lock (TouchLog)
                    TouchLog.Add((sessionId, lastAccessAt, expiresAt));
                Sessions.AddOrUpdate(sessionId, _ => throw new InvalidOperationException(),
                    (_, old) => old with { LastAccessAt = lastAccessAt, ExpiresAt = expiresAt });
            }

            /// <summary>Creates a session the way an app login endpoint would and returns its token.</summary>
            public string SignIn(string sessionId, DateTime now, DateTime expiresAt, string userId = "u-1", bool persistent = false)
            {
                var issued = SessionTokenCodec.Create(sessionId);
                Sessions[sessionId] = new SessionAccessRecord
                {
                    UserId = userId, SecretHash = issued.SecretHash, CreatedAt = now, ExpiresAt = expiresAt, IsPersistent = persistent,
                    UserName = "editor", Email = "e@x.test", Roles = ["Editor"], Permissions = ["items.edit"],
                };
                return issued.Token;
            }
        }

        private sealed class App : IAsyncDisposable
        {
            private readonly IHost _host;
            public FakeSessionSource Source { get; } = new();
            public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(Start));
            public HttpClient Client { get; private set; } = null!;
            public IServiceProvider Services => _host.Services;

            private App(Action<SessionTokenAuthenticationOptions>? configureScheme, Action<ApiAuthOptions>? configureApi)
            {
                _host = new HostBuilder()
                    .ConfigureWebHost(web => web
                        .UseTestServer()
                        .ConfigureServices(services =>
                        {
                            services.AddSingleton<TimeProvider>(Clock);
                            services.AddTqkApiAuth(o =>
                            {
                                o.SessionLifetimes = new SessionLifetimes(TimeSpan.FromDays(7), TimeSpan.FromDays(30));
                                configureApi?.Invoke(o);
                            });
                            services.AddSingleton<ISessionAccessSource>(Source);
                            services.AddAuthentication(SessionTokenDefaults.AuthenticationScheme).AddTqkSessionToken(configureScheme);
                            services.AddAuthorization();
                            services.AddRouting();
                        })
                        .Configure(app =>
                        {
                            app.Use(async (context, next) =>
                            {
                                context.Response.OnStarting(() =>
                                {
                                    context.Response.Headers[RejectedHeader] = context.IsSessionRejected() ? "1" : "0";
                                    return Task.CompletedTask;
                                });
                                await next(context);
                            });
                            app.UseRouting();
                            app.UseAuthentication();
                            app.UseAuthorization();
                            app.UseEndpoints(e =>
                            {
                                e.MapGet("/me", (ClaimsPrincipal user) => string.Join("\n", user.Claims.Select(c => c.Type + "=" + c.Value)))
                                    .RequireAuthorization();
                                e.MapGet("/public", (HttpContext context) => context.User.Identity?.IsAuthenticated == true ? "user" : "guest");
                            });
                        }))
                    .Build();
            }

            public static async Task<App> StartAsync(Action<SessionTokenAuthenticationOptions>? configureScheme = null,
                Action<ApiAuthOptions>? configureApi = null)
            {
                var app = new App(configureScheme, configureApi);
                await app._host.StartAsync();
                app.Client = app._host.GetTestClient();
                return app;
            }

            public SessionAccessCache Cache => Services.GetRequiredService<SessionAccessCache>();

            public string SignIn(string sessionId = "s-1", bool persistent = false, string userId = "u-1")
            {
                var now = Clock.GetUtcNow().UtcDateTime;
                var policy = Services.GetRequiredService<SlidingSessionPolicy>();
                return Source.SignIn(sessionId, now, policy.GetInitialExpiry(now, persistent), userId, persistent);
            }

            public Task<HttpResponseMessage> GetAsync(string? token, string path = "/me")
            {
                var request = new HttpRequestMessage(HttpMethod.Get, path);
                if (token is not null)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return Client.SendAsync(request);
            }

            public async ValueTask DisposeAsync()
            {
                Client.Dispose();
                await _host.StopAsync();
                _host.Dispose();
            }
        }

        private static string Rejected(HttpResponseMessage response)
            => response.Headers.TryGetValues(RejectedHeader, out var v) ? v.Single() : "missing";

        private static string WrongSecret(string token)
        {
            SessionTokenCodec.TryParse(token, out var sid, out var secret);
            return sid + "." + (secret[0] == 'A' ? 'B' : 'A') + secret[1..];
        }

        [Fact]
        public async Task ValidToken_Passes_PrincipalBuiltFromCurrentRecord()
        {
            await using var app = await App.StartAsync();

            using var response = await app.GetAsync(app.SignIn());
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("0", Rejected(response));
            Assert.Contains("sid=s-1", body);
            Assert.Contains("sub=u-1", body);
            Assert.Contains(ClaimTypes.NameIdentifier + "=u-1", body);
            Assert.Contains(ClaimTypes.Name + "=editor", body);
            Assert.Contains(ClaimTypes.Email + "=e@x.test", body);
            Assert.Contains(ClaimTypes.Role + "=Editor", body);
            Assert.Contains("permission=items.edit", body);
            Assert.Equal(1, app.Source.Calls);
            Assert.Equal(0, app.Source.Touches);
        }

        [Fact]
        public async Task WrongSecret_Returns401_InvalidToken()
        {
            await using var app = await App.StartAsync(o => o.OnChallenge = WriteReason);
            var token = app.SignIn();

            using var response = await app.GetAsync(WrongSecret(token));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("1", Rejected(response));
            Assert.Equal("reason=" + SessionRejectionReasons.InvalidToken, await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task WrongSecret_OnRevokedSession_StillReportsInvalidToken()
        {
            await using var app = await App.StartAsync(o => o.OnChallenge = WriteReason);
            var token = app.SignIn();
            app.Source.Sessions["s-1"] = app.Source.Sessions["s-1"] with { IsRevoked = true };

            using var response = await app.GetAsync(WrongSecret(token));

            Assert.Equal("reason=" + SessionRejectionReasons.InvalidToken, await response.Content.ReadAsStringAsync());
        }

        [Theory]
        [InlineData("nodot")]
        [InlineData(".secret")]
        [InlineData("s-1.")]
        public async Task MalformedToken_Returns401_SourceNotCalled(string token)
        {
            await using var app = await App.StartAsync(o => o.OnChallenge = WriteReason);
            app.SignIn();

            using var response = await app.GetAsync(token);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("reason=" + SessionRejectionReasons.InvalidToken, await response.Content.ReadAsStringAsync());
            Assert.Equal(0, app.Source.Calls);
        }

        [Fact]
        public async Task NoAuthorizationHeader_NoResult_AnonymousEndpointRunsAsGuest()
        {
            await using var app = await App.StartAsync();

            using var guest = await app.GetAsync(null, "/public");
            using var protectedResponse = await app.GetAsync(null);

            Assert.Equal("guest", await guest.Content.ReadAsStringAsync());
            Assert.Equal("0", Rejected(guest));
            Assert.Equal(HttpStatusCode.Unauthorized, protectedResponse.StatusCode);
            Assert.Equal("0", Rejected(protectedResponse));
            Assert.Contains("Bearer", protectedResponse.Headers.WwwAuthenticate.ToString());
            Assert.Equal(0, app.Source.Calls);
        }

        [Fact]
        public async Task UnknownSession_Returns401()
        {
            await using var app = await App.StartAsync();

            using var response = await app.GetAsync(SessionTokenCodec.Create("missing").Token);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("1", Rejected(response));
        }

        [Fact]
        public async Task RevokedSession_Returns401_Revoked()
        {
            await using var app = await App.StartAsync(o => o.OnChallenge = WriteReason);
            var token = app.SignIn();
            app.Source.Sessions["s-1"] = app.Source.Sessions["s-1"] with { IsRevoked = true };

            using var response = await app.GetAsync(token);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("reason=" + SessionRejectionReasons.Revoked, await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task ExpiredSession_Returns401_Expired_EvenWhenServedFromCache()
        {
            await using var app = await App.StartAsync(o => o.OnChallenge = WriteReason,
                api => api.SessionAccessCacheLifetime = TimeSpan.FromDays(30));
            var token = app.SignIn();
            (await app.GetAsync(token)).Dispose();

            app.Clock.Advance(TimeSpan.FromDays(7));
            using var response = await app.GetAsync(token);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("reason=" + SessionRejectionReasons.Expired, await response.Content.ReadAsStringAsync());
            Assert.Equal(1, app.Source.Calls); // the cached record still knows its expiry
        }

        [Fact]
        public async Task SourceRejectionReason_IsPassedToOnChallenge()
        {
            await using var app = await App.StartAsync(o => o.OnChallenge = WriteReason);
            app.Source.Result = _ => SessionAccessResult.Reject(SessionRejectionReasons.UserLocked);

            using var response = await app.GetAsync(SessionTokenCodec.Create("s-1").Token);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("reason=" + SessionRejectionReasons.UserLocked, await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task OnChallenge_NotHandled_DefaultResponseStillWritten()
        {
            SessionRejection? seen = null;
            await using var app = await App.StartAsync(o => o.OnChallenge = context =>
            {
                seen = context.Rejection;
                return Task.CompletedTask;
            });

            using var response = await app.GetAsync("nodot");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
            Assert.Equal(SessionRejectionReasons.InvalidToken, seen?.Reason);
        }

        [Fact]
        public async Task CachedRecord_SecondRequestDoesNotHitSource_UntilInvalidated()
        {
            await using var app = await App.StartAsync();
            var token = app.SignIn();

            (await app.GetAsync(token)).Dispose();
            (await app.GetAsync(token)).Dispose();
            Assert.Equal(1, app.Source.Calls);

            app.Source.Sessions["s-1"] = app.Source.Sessions["s-1"] with { IsRevoked = true };
            app.Cache.InvalidateSession("s-1");
            using var response = await app.GetAsync(token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(2, app.Source.Calls);
        }

        [Fact]
        public async Task InvalidateUser_NewNameAppliesOnNextRequest()
        {
            await using var app = await App.StartAsync();
            var token = app.SignIn();
            (await app.GetAsync(token)).Dispose();

            app.Source.Sessions["s-1"] = app.Source.Sessions["s-1"] with { UserName = "renamed" };
            using (var stale = await app.GetAsync(token))
                Assert.Contains(ClaimTypes.Name + "=editor", await stale.Content.ReadAsStringAsync());

            app.Cache.InvalidateUser("u-1");
            using var fresh = await app.GetAsync(token);
            Assert.Contains(ClaimTypes.Name + "=renamed", await fresh.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Sliding_TouchesAtMostOncePerInterval_AndExtendsExpiry()
        {
            await using var app = await App.StartAsync();
            var token = app.SignIn();

            (await app.GetAsync(token)).Dispose();
            app.Clock.Advance(TimeSpan.FromMinutes(59));
            (await app.GetAsync(token)).Dispose();
            Assert.Equal(0, app.Source.Touches);

            app.Clock.Advance(TimeSpan.FromMinutes(2)); // 61 min after sign-in
            (await app.GetAsync(token)).Dispose();
            (await app.GetAsync(token)).Dispose();
            app.Clock.Advance(TimeSpan.FromMinutes(30));
            (await app.GetAsync(token)).Dispose();

            Assert.Equal(1, app.Source.Touches);
            var touched = Assert.Single(app.Source.TouchLog);
            var touchedAt = Start.AddMinutes(61);
            Assert.Equal(touchedAt, touched.LastAccessAt);
            Assert.Equal(touchedAt.AddDays(7), touched.ExpiresAt);

            // Past the ORIGINAL expiry the session lives on thanks to the touch.
            app.Clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(61));
            using var afterOriginalExpiry = await app.GetAsync(token);
            Assert.Equal(HttpStatusCode.OK, afterOriginalExpiry.StatusCode);
        }

        [Fact]
        public async Task Sliding_ParallelRequests_SingleTouch()
        {
            await using var app = await App.StartAsync(configureApi: o => o.SessionAccessCacheLifetime = TimeSpan.FromDays(1));
            var token = app.SignIn();
            (await app.GetAsync(token)).Dispose();
            app.Clock.Advance(TimeSpan.FromHours(2));
            app.Source.TouchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var first = app.GetAsync(token);
            while (app.Source.Touches == 0)
                await Task.Delay(10);
            var others = Enumerable.Range(0, 5).Select(_ => app.GetAsync(token)).ToArray();
            foreach (var response in await Task.WhenAll(others))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode); // not blocked by the touch in flight
                response.Dispose();
            }
            app.Source.TouchGate.SetResult();
            (await first).Dispose();

            Assert.Equal(1, app.Source.Touches);
        }

        [Fact]
        public async Task Sliding_PersistentSession_UsesPersistentLifetime()
        {
            await using var app = await App.StartAsync();
            var token = app.SignIn(persistent: true);
            app.Clock.Advance(TimeSpan.FromHours(2));

            (await app.GetAsync(token)).Dispose();

            Assert.Equal(Start.AddHours(2).AddDays(30), Assert.Single(app.Source.TouchLog).ExpiresAt);
        }

        [Fact]
        public async Task AbsoluteCap_ClampsTouch_ThenRejects()
        {
            await using var app = await App.StartAsync(o => o.OnChallenge = WriteReason, api => api.MaxSessionLifetime = TimeSpan.FromDays(10));
            var token = app.SignIn();

            for (var day = 1; day <= 9; day++)
            {
                app.Clock.Advance(TimeSpan.FromDays(1));
                using var ok = await app.GetAsync(token);
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            }
            Assert.Equal(Start.AddDays(10), app.Source.TouchLog[^1].ExpiresAt); // clamped to the cap, not day 9 + 7

            app.Clock.Advance(TimeSpan.FromDays(1));
            app.Cache.InvalidateAll();
            using var capped = await app.GetAsync(token);
            Assert.Equal(HttpStatusCode.Unauthorized, capped.StatusCode);
            Assert.Equal("reason=" + SessionRejectionReasons.Expired, await capped.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task AbsoluteCap_StoredExpiryBeyondCap_StillRejected()
        {
            await using var app = await App.StartAsync(o => o.OnChallenge = WriteReason, api => api.MaxSessionLifetime = TimeSpan.FromDays(10));
            var token = app.SignIn();
            app.Source.Sessions["s-1"] = app.Source.Sessions["s-1"] with { ExpiresAt = Start.AddDays(100) };
            app.Clock.Advance(TimeSpan.FromDays(10));

            using var response = await app.GetAsync(token);

            Assert.Equal("reason=" + SessionRejectionReasons.Expired, await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task TouchFailure_RequestStillSucceeds_RetriedAfterBackoff_NotEveryRequest()
        {
            await using var app = await App.StartAsync();
            var token = app.SignIn();
            app.Clock.Advance(TimeSpan.FromHours(2));
            app.Source.TouchError = new InvalidOperationException("db down");

            using (var response = await app.GetAsync(token))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            (await app.GetAsync(token)).Dispose();
            Assert.Equal(1, app.Source.Touches);

            app.Source.TouchError = null;
            app.Clock.Advance(TimeSpan.FromMinutes(1));
            (await app.GetAsync(token)).Dispose();
            Assert.Equal(2, app.Source.Touches);
            Assert.Single(app.Source.TouchLog);
        }

        [Fact]
        public async Task TouchSuccess_UpdatesCachedRecord_NoReload()
        {
            await using var app = await App.StartAsync(configureApi: o => o.SessionAccessCacheLifetime = TimeSpan.FromDays(30));
            var token = app.SignIn();
            (await app.GetAsync(token)).Dispose();
            app.Clock.Advance(TimeSpan.FromHours(2));
            (await app.GetAsync(token)).Dispose();

            // Same process, cache still holds the touched record: past the original expiry without asking the source.
            app.Clock.Advance(TimeSpan.FromDays(6));
            using var response = await app.GetAsync(token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, app.Source.Calls);
        }

        [Fact]
        public async Task ConfiguredClaimTypes_AreUsed()
        {
            await using var app = await App.StartAsync(configureApi: o =>
            {
                o.UserIdClaimType = "uid";
                o.SessionIdClaimType = "app_sid";
                o.PermissionClaimType = "perm";
            });

            using var response = await app.GetAsync(app.SignIn("s-9", userId: "u-9"));
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("uid=u-9", body);
            Assert.Contains("app_sid=s-9", body);
            Assert.Contains("perm=items.edit", body);
        }

        private static async Task WriteReason(SessionTokenChallengeContext context)
        {
            if (context.Rejection is not { } rejection)
                return;
            context.HandleResponse();
            context.HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.HttpContext.Response.WriteAsync("reason=" + rejection.Reason);
        }
    }
}
