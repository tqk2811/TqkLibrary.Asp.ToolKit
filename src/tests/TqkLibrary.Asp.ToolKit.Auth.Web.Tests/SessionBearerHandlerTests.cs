using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Claims;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Tests
{
    /// <summary>
    /// <see cref="SessionBearerHandler"/>: attaches the session token from the cookie, sends exactly once, and turns a
    /// 401 into <see cref="SessionExpiredException"/>. There is no refresh any more.
    /// </summary>
    public class SessionBearerHandlerTests
    {
        private const string Token = "sid.secret-value";
        private const string Challenge = "Bearer error=\"invalid_token\"";

        [Fact]
        public async Task AttachesSessionTokenAsBearer()
        {
            var h = new Harness();

            using var response = await h.Client.GetAsync("http://api/items");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal([Token], h.Inner.Bearers);
            Assert.Equal("Bearer", h.Inner.LastScheme);
        }

        [Fact]
        public async Task Unauthorized_ThrowsSessionExpired_AfterExactlyOneSend()
        {
            var h = new Harness();
            h.Inner.Respond = _ => HttpStatusCode.Unauthorized;
            h.Inner.WwwAuthenticate = Challenge;

            await Assert.ThrowsAsync<SessionExpiredException>(() => h.Client.PostAsync("http://api/items", new StringContent("{}")));

            Assert.Equal(1, h.Inner.Calls);
            Assert.True(WebSessionTicket.For(h.Context).DropPending);
        }

        /// <summary>
        /// Regression: a business 401 (e.g. a wrong 2FA code on an [Authorize] page) carries no Bearer challenge and
        /// must not end the session.
        /// </summary>
        [Fact]
        public async Task Unauthorized_WithoutChallenge_ReturnedAsIs_SessionNotExpired()
        {
            var h = new Harness();
            h.Inner.Respond = _ => HttpStatusCode.Unauthorized;

            using var response = await h.Client.PostAsync("http://api/items", new StringContent("{}"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(1, h.Inner.Calls);
            Assert.False(h.Context.Response.HasStarted);
            Assert.False(WebSessionTicket.For(h.Context).DropPending);
        }

        [Fact]
        public async Task Unauthorized_WithBearerChallenge_ThrowsAndMarksSessionExpired()
        {
            var h = new Harness();
            h.Inner.Respond = _ => HttpStatusCode.Unauthorized;
            h.Inner.WwwAuthenticate = Challenge;

            await Assert.ThrowsAsync<SessionExpiredException>(() => h.Client.GetAsync("http://api/items"));

            Assert.True(WebSessionTicket.For(h.Context).DropPending);
        }

        [Fact]
        public async Task Unauthorized_WithNonBearerChallenge_ReturnedAsIs()
        {
            var h = new Harness();
            h.Inner.Respond = _ => HttpStatusCode.Unauthorized;
            h.Inner.WwwAuthenticate = "Basic realm=\"x\"";

            using var response = await h.Client.GetAsync("http://api/items");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task TreatAnyUnauthorized_True_Unauthorized_WithoutChallenge_Throws()
        {
            var h = new Harness(configure: o => o.TreatAnyUnauthorizedAsSessionExpired = true);
            h.Inner.Respond = _ => HttpStatusCode.Unauthorized;

            await Assert.ThrowsAsync<SessionExpiredException>(() => h.Client.GetAsync("http://api/items"));

            Assert.True(WebSessionTicket.For(h.Context).DropPending);
        }

        [Fact]
        public async Task BearerChallenge_OnNon401_DoesNotExpire()
        {
            var h = new Harness();
            h.Inner.Respond = _ => HttpStatusCode.Forbidden;
            h.Inner.WwwAuthenticate = Challenge;

            using var response = await h.Client.GetAsync("http://api/items");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        /// <summary>
        /// Regression: the old handler refreshed proactively and waited for the auth API (up to its refresh timeout)
        /// before every call once the access token was about to expire. Now a call is one send, whatever the API does.
        /// </summary>
        [Fact]
        public async Task SlowApi_OneSendPerCall_NoOtherApiCall()
        {
            var h = new Harness();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Inner.RespondAsync = async _ =>
            {
                await release.Task;
                return HttpStatusCode.OK;
            };

            var calls = Enumerable.Range(0, 3).Select(i => h.Client.GetAsync("http://api/slow/" + i)).ToArray();
            while (h.Inner.Calls < 3)
                await Task.Delay(10);
            Assert.All(calls, c => Assert.False(c.IsCompleted));
            release.SetResult();
            foreach (var response in await Task.WhenAll(calls))
                response.Dispose();

            Assert.Equal(3, h.Inner.Calls);
            Assert.Equal(1, h.Auth.AuthenticateCount); // the cookie is read once per request
        }

        [Theory]
        [InlineData(HttpStatusCode.Forbidden)]
        [InlineData(HttpStatusCode.InternalServerError)]
        public async Task OtherErrorStatus_ReturnedAsIs(HttpStatusCode status)
        {
            var h = new Harness();
            h.Inner.Respond = _ => status;

            using var response = await h.Client.GetAsync("http://api/items");

            Assert.Equal(status, response.StatusCode);
            Assert.Equal(1, h.Inner.Calls);
            Assert.False(h.Context.Response.HasStarted);
        }

        [Fact]
        public async Task UploadBody_SentOnceUnbuffered_401IsSessionExpired()
        {
            var h = new Harness();
            h.Inner.Respond = _ => HttpStatusCode.Unauthorized;
            h.Inner.WwwAuthenticate = Challenge;
            using var content = new StreamContent(new MemoryStream([1, 2, 3]));

            await Assert.ThrowsAsync<SessionExpiredException>(() => h.Client.PostAsync("http://api/upload", content));

            Assert.Equal(1, h.Inner.Calls);
        }

        [Fact]
        public async Task CookieWithoutToken_ThrowsSessionExpired_NothingSent()
        {
            var h = new Harness(storeToken: false);

            await Assert.ThrowsAsync<SessionExpiredException>(() => h.Client.GetAsync("http://api/items"));

            Assert.Equal(0, h.Inner.Calls);
        }

        [Fact]
        public async Task Guest_PassesThrough_401NotSessionExpired()
        {
            var h = new Harness(guest: true);
            h.Inner.Respond = _ => HttpStatusCode.Unauthorized;

            using var response = await h.Client.GetAsync("http://api/items");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(new string?[] { null }, h.Inner.Bearers);
            Assert.Equal(0, h.Auth.AuthenticateCount);
        }

        [Fact]
        public async Task AnonymousPathSuffix_NoBearer_401ReturnedAsIs()
        {
            var h = new Harness(configure: o => o.AnonymousPathSuffixes.Add("/auth/login"));
            h.Inner.Respond = _ => HttpStatusCode.Unauthorized;

            using var response = await h.Client.PostAsync("http://api/auth/login?x=1", new StringContent("{}"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(new string?[] { null }, h.Inner.Bearers);
        }

        [Fact]
        public async Task InsideAnonymousApiScope_NoBearer()
        {
            var h = new Harness();
            h.Inner.Respond = _ => HttpStatusCode.Unauthorized;

            using var response = await AnonymousApiScope.RunAsync(() => h.Client.GetAsync("http://api/public"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(new string?[] { null }, h.Inner.Bearers);
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

        // ---------- Test infrastructure ----------

        private sealed class Harness
        {
            public WebAuthOptions Options { get; } = new();
            public RecordingHandler Inner { get; } = new();
            public FakeAuthenticationService Auth { get; }
            public DefaultHttpContext Context { get; }
            public HttpClient Client { get; }

            public Harness(bool storeToken = true, bool guest = false, Action<WebAuthOptions>? configure = null)
            {
                configure?.Invoke(Options);
                var properties = new AuthenticationProperties();
                if (storeToken)
                    properties.StoreTokens([new AuthenticationToken { Name = WebSessionTicket.SessionTokenName, Value = Token }]);

                Auth = new FakeAuthenticationService(properties, Options.Scheme, Options.SessionIdClaimType);
                var services = new ServiceCollection();
                services.AddSingleton<IOptions<WebAuthOptions>>(Microsoft.Extensions.Options.Options.Create(Options));
                services.AddSingleton<IAuthenticationService>(Auth);
                var provider = services.BuildServiceProvider();

                Context = new DefaultHttpContext { RequestServices = provider };
                Context.User = guest
                    ? new ClaimsPrincipal(new ClaimsIdentity())
                    : new ClaimsPrincipal(new ClaimsIdentity([new Claim(Options.SessionIdClaimType, "sid")], "test"));
                var accessor = new HttpContextAccessor { HttpContext = Context };

                var handler = new SessionBearerHandler(accessor, Microsoft.Extensions.Options.Options.Create(Options)) { InnerHandler = Inner };
                Client = new HttpClient(handler);
            }
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private int _calls;
            public int Calls => _calls;
            public List<string?> Bearers { get; } = new();
            public string? LastScheme { get; private set; }
            public string? WwwAuthenticate { get; set; }
            public Func<string?, HttpStatusCode> Respond { get; set; } = _ => HttpStatusCode.OK;
            public Func<string?, Task<HttpStatusCode>>? RespondAsync { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _calls);
                var bearer = request.Headers.Authorization?.Parameter;
                LastScheme = request.Headers.Authorization?.Scheme;
                lock (Bearers)
                    Bearers.Add(bearer);
                var status = RespondAsync is null ? Respond(bearer) : await RespondAsync(bearer);
                var response = new HttpResponseMessage(status);
                if (WwwAuthenticate is not null)
                    response.Headers.TryAddWithoutValidation("WWW-Authenticate", WwwAuthenticate);
                return response;
            }
        }

        private sealed class FakeAuthenticationService(AuthenticationProperties properties, string scheme, string sidClaim) : IAuthenticationService
        {
            private int _authenticateCount;
            public int AuthenticateCount => _authenticateCount;

            public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme1)
            {
                Interlocked.Increment(ref _authenticateCount);
                var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(sidClaim, "sid")], "test"));
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, properties, scheme)));
            }

            public Task ChallengeAsync(HttpContext context, string? scheme1, AuthenticationProperties? properties1) => Task.CompletedTask;
            public Task ForbidAsync(HttpContext context, string? scheme1, AuthenticationProperties? properties1) => Task.CompletedTask;
            public Task SignInAsync(HttpContext context, string? scheme1, ClaimsPrincipal principal, AuthenticationProperties? properties1) => Task.CompletedTask;
            public Task SignOutAsync(HttpContext context, string? scheme1, AuthenticationProperties? properties1) => Task.CompletedTask;
        }
    }
}
