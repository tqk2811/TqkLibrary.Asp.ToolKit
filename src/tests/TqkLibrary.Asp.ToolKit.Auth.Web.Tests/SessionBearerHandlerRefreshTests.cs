using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using System.Net;
using System.Security.Claims;
using TqkLibrary.Asp.ToolKit.Auth.Web.Interfaces;
using TqkLibrary.Asp.ToolKit.Auth.Web.Models;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Tests
{
    /// <summary>
    /// <see cref="SessionBearerHandler"/> with a working refresh: 401 refreshes once and resends, the new tokens are
    /// written back to the cookie while the response has not started, and bodies that cannot be replayed are never resent.
    /// The transient-fallback rules are covered by <see cref="SessionBearerHandlerTests"/>.
    /// </summary>
    public class SessionBearerHandlerRefreshTests
    {
        private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        [Fact]
        public async Task Unauthorized_RefreshesOnce_ResendsWithNewToken_AndRewritesCookie()
        {
            var h = new Harness();
            h.Inner.Respond = bearer => bearer == "old-access" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;

            using var response = await h.Client.GetAsync("http://api/items");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, h.Api.RefreshCalls);
            Assert.Equal("refresh", h.Api.LastRefreshToken);
            Assert.Equal(["old-access", "new-access"], h.Inner.Bearers);
            Assert.Equal(1, h.Auth.SignInCount);
            Assert.Equal("new-access", h.Auth.LastSignInProperties!.GetTokenValue(h.Options.AccessTokenName));
            // The rewritten cookie still carries only the session id (and the stable identity claim with the same value).
            Assert.Equal("sid", h.Auth.LastSignInPrincipal!.FindFirst(h.Options.SessionIdClaimType)?.Value);
            Assert.Equal([h.Options.SessionIdClaimType + "=sid", "sub=sid"],
                h.Auth.LastSignInPrincipal.Claims.Select(c => c.Type + "=" + c.Value));
        }

        [Fact]
        public async Task Unauthorized_StringBody_IsResentIntact()
        {
            var h = new Harness();
            h.Inner.Respond = bearer => bearer == "old-access" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;

            using var response = await h.Client.PostAsync("http://api/items", new StringContent("payload"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(["payload", "payload"], h.Inner.Bodies);
        }

        [Fact]
        public async Task ResponseAlreadyStarted_RefreshStillWorks_ButCookieIsNotRewritten()
        {
            var h = new Harness(responseStarted: true);
            h.Inner.Respond = bearer => bearer == "old-access" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;

            using var response = await h.Client.GetAsync("http://api/items");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("new-access", h.Inner.Bearers[^1]);
            Assert.Equal(0, h.Auth.SignInCount);
        }

        [Fact]
        public async Task RefreshRejected_ThrowsSessionExpired_CookieNotRewritten()
        {
            var h = new Harness(refreshThrows: new SessionExpiredException("dead"));
            h.Inner.Respond = _ => HttpStatusCode.Unauthorized;

            await Assert.ThrowsAsync<SessionExpiredException>(() => h.Client.GetAsync("http://api/items"));

            Assert.Equal(1, h.Inner.Calls);
            Assert.Equal(0, h.Auth.SignInCount);
        }

        [Fact]
        public async Task RefreshTransientFailureOn401_PropagatesAsIs_NotSessionExpired()
        {
            var h = new Harness(refreshThrows: new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable));
            h.Inner.Respond = _ => HttpStatusCode.Unauthorized;

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => h.Client.GetAsync("http://api/items"));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
            Assert.Equal(0, h.Auth.SignInCount);
        }

        [Fact]
        public async Task FreshTokenStillUnauthorized_NoSecondRefresh_ThrowsSessionExpired()
        {
            var h = new Harness();
            h.Inner.Respond = _ => HttpStatusCode.Unauthorized;

            await Assert.ThrowsAsync<SessionExpiredException>(() => h.Client.GetAsync("http://api/items"));

            Assert.Equal(1, h.Api.RefreshCalls);
            Assert.Equal(2, h.Inner.Calls);
        }

        [Fact]
        public async Task CookieWithoutTokens_ThrowsSessionExpired_WithoutCallingApi()
        {
            var h = new Harness(storeTokens: false);

            await Assert.ThrowsAsync<SessionExpiredException>(() => h.Client.GetAsync("http://api/items"));

            Assert.Equal(0, h.Inner.Calls);
            Assert.Equal(0, h.Api.RefreshCalls);
        }

        [Fact]
        public async Task ExpiringToken_RefreshedBeforeSending()
        {
            var h = new Harness(accessLifetime: TimeSpan.FromSeconds(20));

            using var response = await h.Client.GetAsync("http://api/items");

            Assert.Equal(1, h.Api.RefreshCalls);
            Assert.Equal(["new-access"], h.Inner.Bearers);
            Assert.Equal(1, h.Auth.SignInCount);
        }

        [Fact]
        public async Task ConfiguredSkew_RefreshesEarlierThanDefault()
        {
            var h = new Harness(accessLifetime: TimeSpan.FromMinutes(2), configure: o => o.RefreshSkew = TimeSpan.FromMinutes(5));

            using var response = await h.Client.GetAsync("http://api/items");

            Assert.Equal(1, h.Api.RefreshCalls);
            Assert.Equal(["new-access"], h.Inner.Bearers);
        }

        [Fact]
        public async Task DefaultSkew_TokenValidForTwoMinutes_NotRefreshed()
        {
            var h = new Harness(accessLifetime: TimeSpan.FromMinutes(2));

            using var response = await h.Client.GetAsync("http://api/items");

            Assert.Equal(0, h.Api.RefreshCalls);
            Assert.Equal(["old-access"], h.Inner.Bearers);
        }

        [Fact]
        public async Task MultipartUpload_Unauthorized_NotResent_ThrowsSessionExpired()
        {
            var h = new Harness();
            h.Inner.Respond = _ => HttpStatusCode.Unauthorized;
            using var content = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "a.bin" } };

            await Assert.ThrowsAsync<SessionExpiredException>(() => h.Client.PostAsync("http://api/upload", content));

            Assert.Equal(1, h.Inner.Calls);
            Assert.Equal(0, h.Api.RefreshCalls);
        }

        [Theory]
        [InlineData("stream")]
        [InlineData("multipart")]
        public async Task UploadWithinReplayLimit_Unauthorized_RefreshesAndResendsSameBody(string kind)
        {
            var h = new Harness();
            h.Inner.Respond = bearer => bearer == "old-access" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;
            h.Inner.RecordBytes = true;
            using HttpContent content = kind == "stream"
                ? new StreamContent(new MemoryStream([1, 2, 3]))
                : new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "a.bin" } };
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://api/upload") { Content = content };
            request.Options.Set(SessionBearerHandler.ReplayBufferLimitOption, 1024L);

            using var response = await h.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(["old-access", "new-access"], h.Inner.Bearers);
            Assert.Equal(2, h.Inner.ByteBodies.Count);
            Assert.NotEmpty(h.Inner.ByteBodies[0]);
            Assert.Equal(h.Inner.ByteBodies[0], h.Inner.ByteBodies[1]);
        }

        [Fact]
        public async Task PerRequestReplayLimit_AllowsReplay()
        {
            var h = new Harness();
            h.Inner.Respond = bearer => bearer == "old-access" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://api/upload")
            {
                Content = new StreamContent(new MemoryStream([1, 2, 3])),
            };
            request.Options.Set(SessionBearerHandler.ReplayBufferLimitOption, 10L);

            using var response = await h.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, h.Api.RefreshCalls);
        }

        [Fact]
        public async Task PerRequestReplayLimitZero_DoesNotBuffer()
        {
            var h = new Harness();
            h.Inner.Respond = _ => HttpStatusCode.Unauthorized;
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://api/upload")
            {
                Content = new StreamContent(new MemoryStream([1, 2, 3])),
            };
            request.Options.Set(SessionBearerHandler.ReplayBufferLimitOption, 0L);

            await Assert.ThrowsAsync<SessionExpiredException>(() => h.Client.SendAsync(request));
            Assert.Equal(0, h.Api.RefreshCalls);
        }

        [Fact]
        public async Task UploadOverReplayLimit_NotResent_ThrowsSessionExpired()
        {
            var h = new Harness();
            h.Inner.Respond = _ => HttpStatusCode.Unauthorized;
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://api/upload")
            {
                Content = new StreamContent(new MemoryStream([1, 2, 3])),
            };
            request.Options.Set(SessionBearerHandler.ReplayBufferLimitOption, 2L);

            await Assert.ThrowsAsync<SessionExpiredException>(() => h.Client.SendAsync(request));

            Assert.Equal(1, h.Inner.Calls);
            Assert.Equal(0, h.Api.RefreshCalls);
        }

        [Fact]
        public async Task UploadOfUnknownLength_NotBuffered_NotResent()
        {
            var h = new Harness();
            h.Inner.Respond = _ => HttpStatusCode.Unauthorized;
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://api/upload")
            {
                Content = new StreamContent(new NonSeekableStream([1, 2, 3])),
            };
            request.Options.Set(SessionBearerHandler.ReplayBufferLimitOption, 1024L);

            await Assert.ThrowsAsync<SessionExpiredException>(() => h.Client.SendAsync(request));

            Assert.Equal(1, h.Inner.Calls);
            Assert.Equal(0, h.Api.RefreshCalls);
        }

        [Fact]
        public async Task UploadLongerThanDeclaredLength_BufferingFails_NothingIsSent()
        {
            var h = new Harness();
            h.Inner.Respond = bearer => bearer == "old-access" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;
            // ContentLength header lies (5) while the stream holds 100 bytes: the limit (10) must still be enforced.
            var content = new StreamContent(new MemoryStream(new byte[100]));
            content.Headers.ContentLength = 5;
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://api/upload") { Content = content };
            request.Options.Set(SessionBearerHandler.ReplayBufferLimitOption, 10L);

            await Assert.ThrowsAsync<HttpRequestException>(() => h.Client.SendAsync(request));

            // A half-read stream is never sent.
            Assert.Equal(0, h.Inner.Calls);
            Assert.Equal(0, h.Api.RefreshCalls);
        }

        private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
        {
            public override bool CanSeek => false;
        }

        [Fact]
        public async Task StreamUpload_Unauthorized_NotResent_ThrowsSessionExpired()
        {
            var h = new Harness();
            h.Inner.Respond = _ => HttpStatusCode.Unauthorized;
            using var content = new StreamContent(new MemoryStream([1, 2, 3]));

            await Assert.ThrowsAsync<SessionExpiredException>(() => h.Client.PostAsync("http://api/upload", content));

            Assert.Equal(1, h.Inner.Calls);
            Assert.Equal(0, h.Api.RefreshCalls);
        }

        [Fact]
        public async Task MultipartUpload_ExpiringToken_StillRefreshedBeforeSending()
        {
            var h = new Harness(accessLifetime: TimeSpan.FromSeconds(10));
            using var content = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "a.bin" } };

            using var response = await h.Client.PostAsync("http://api/upload", content);

            Assert.Equal(1, h.Api.RefreshCalls);
            Assert.Equal(["new-access"], h.Inner.Bearers);
        }

        [Fact]
        public async Task AnonymousPathSuffix_NoBearer_NoRefreshOn401()
        {
            var h = new Harness(configure: o => o.AnonymousPathSuffixes.Add("/auth/login"));
            h.Inner.Respond = _ => HttpStatusCode.Unauthorized;

            using var response = await h.Client.PostAsync("http://api/auth/login?x=1", new StringContent("{}"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(new string?[] { null }, h.Inner.Bearers);
            Assert.Equal(0, h.Api.RefreshCalls);
        }

        [Fact]
        public async Task InsideAnonymousApiScope_NoBearer_NoRefresh()
        {
            var h = new Harness(accessLifetime: TimeSpan.FromSeconds(5));

            using var response = await AnonymousApiScope.RunAsync(() => h.Client.GetAsync("http://api/public"));

            Assert.Equal(new string?[] { null }, h.Inner.Bearers);
            Assert.Equal(0, h.Api.RefreshCalls);
        }

        [Fact]
        public async Task ExplicitAuthorizationHeader_IsLeftUntouched()
        {
            var h = new Harness();
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://api/items");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "explicit");

            using var response = await h.Client.SendAsync(request);

            Assert.Equal(["explicit"], h.Inner.Bearers);
        }

        [Fact]
        public async Task ConcurrentCallsInSameRequest_BothUnauthorized_RefreshOnlyOnce()
        {
            var h = new Harness();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var arrived = 0;
            h.Inner.RespondAsync = async bearer =>
            {
                if (bearer != "old-access")
                    return HttpStatusCode.OK;
                if (Interlocked.Increment(ref arrived) == 2)
                    gate.SetResult();
                await gate.Task;
                return HttpStatusCode.Unauthorized;
            };

            var first = h.Client.GetAsync("http://api/a");
            var second = h.Client.GetAsync("http://api/b");
            using var r1 = await first;
            using var r2 = await second;

            Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
            Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
            Assert.Equal(1, h.Api.RefreshCalls);
            Assert.Equal(1, h.Auth.AuthenticateCount);
        }

        // ---------- Test infrastructure ----------

        private sealed class Harness
        {
            public FakeTimeProvider Clock { get; } = new(Start);
            public WebAuthOptions Options { get; } = new();
            public RecordingHandler Inner { get; } = new();
            public FakeAuthApiClient Api { get; }
            public FakeAuthenticationService Auth { get; }
            public HttpClient Client { get; }

            public Harness(TimeSpan? accessLifetime = null, Exception? refreshThrows = null, bool storeTokens = true,
                bool responseStarted = false, Action<WebAuthOptions>? configure = null)
            {
                configure?.Invoke(Options);
                var properties = new AuthenticationProperties();
                if (storeTokens)
                {
                    new WebSessionTokens("old-access", Start.UtcDateTime + (accessLifetime ?? TimeSpan.FromMinutes(10)),
                        "refresh", Start.UtcDateTime.AddDays(1)).WriteTo(properties, Options);
                }

                Api = new FakeAuthApiClient(refreshThrows, Start);
                Auth = new FakeAuthenticationService(properties, Options.Scheme, Options.SessionIdClaimType);
                var services = new ServiceCollection();
                services.AddOptions();
                services.AddSingleton<IOptions<WebAuthOptions>>(Microsoft.Extensions.Options.Options.Create(Options));
                services.AddSingleton<TimeProvider>(Clock);
                services.AddSingleton<IAuthApiClient>(Api);
                services.AddSingleton<WebSessionPrincipalFactory>();
                services.AddSingleton<IAuthenticationService>(Auth);
                var provider = services.BuildServiceProvider();

                var context = new DefaultHttpContext { RequestServices = provider };
                if (responseStarted)
                    context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(Options.SessionIdClaimType, "sid")], "test"));
                var accessor = new HttpContextAccessor { HttpContext = context };

                var handler = new SessionBearerHandler(accessor, Clock, Microsoft.Extensions.Options.Options.Create(Options)) { InnerHandler = Inner };
                Client = new HttpClient(handler);
            }
        }

        private sealed class StartedResponseFeature : HttpResponseFeature
        {
            public override bool HasStarted => true;
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private int _calls;
            public int Calls => _calls;
            public List<string?> Bearers { get; } = new();
            public List<string> Bodies { get; } = new();
            public bool RecordBytes { get; set; }
            public List<byte[]> ByteBodies { get; } = new();
            public Func<string?, HttpStatusCode> Respond { get; set; } = _ => HttpStatusCode.OK;
            public Func<string?, Task<HttpStatusCode>>? RespondAsync { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _calls);
                var bearer = request.Headers.Authorization?.Parameter;
                lock (Bearers)
                    Bearers.Add(bearer);
                if (RecordBytes && request.Content is not null)
                {
                    var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                    lock (ByteBodies)
                        ByteBodies.Add(bytes);
                }
                else if (request.Content is StringContent)
                {
                    var body = await request.Content.ReadAsStringAsync(cancellationToken);
                    lock (Bodies)
                        Bodies.Add(body);
                }
                var status = RespondAsync is null ? Respond(bearer) : await RespondAsync(bearer);
                return new HttpResponseMessage(status);
            }
        }

        private sealed class FakeAuthApiClient(Exception? refreshThrows, DateTimeOffset now) : IAuthApiClient
        {
            private int _refreshCalls;
            public int RefreshCalls => _refreshCalls;
            public string? LastRefreshToken { get; private set; }

            public Task<AuthTokenPair> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _refreshCalls);
                LastRefreshToken = refreshToken;
                if (refreshThrows is not null)
                    throw refreshThrows;
                return Task.FromResult(new AuthTokenPair
                {
                    SessionId = "sid",
                    AccessToken = "new-access",
                    AccessTokenExpiresAt = now.UtcDateTime.AddMinutes(15),
                    RefreshToken = refreshToken,
                    RefreshTokenExpiresAt = now.UtcDateTime.AddDays(1),
                });
            }

            public Task<CurrentUserInfo> GetCurrentUserAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task LogoutAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        private sealed class FakeAuthenticationService(AuthenticationProperties properties, string scheme, string sidClaim) : IAuthenticationService
        {
            private int _authenticateCount;
            public int AuthenticateCount => _authenticateCount;
            public int SignInCount { get; private set; }
            public AuthenticationProperties? LastSignInProperties { get; private set; }
            public ClaimsPrincipal? LastSignInPrincipal { get; private set; }

            public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme1)
            {
                Interlocked.Increment(ref _authenticateCount);
                var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(sidClaim, "sid")], "test"));
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, properties, scheme)));
            }

            public Task SignInAsync(HttpContext context, string? scheme1, ClaimsPrincipal principal, AuthenticationProperties? properties1)
            {
                SignInCount++;
                LastSignInPrincipal = principal;
                LastSignInProperties = properties1;
                return Task.CompletedTask;
            }

            public Task ChallengeAsync(HttpContext context, string? scheme1, AuthenticationProperties? properties1) => Task.CompletedTask;
            public Task ForbidAsync(HttpContext context, string? scheme1, AuthenticationProperties? properties1) => Task.CompletedTask;
            public Task SignOutAsync(HttpContext context, string? scheme1, AuthenticationProperties? properties1) => Task.CompletedTask;
        }
    }
}
