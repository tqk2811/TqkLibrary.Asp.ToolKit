using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
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
    public class SessionBearerHandlerTests
    {
        private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // ---------- DefaultIsTransientRefreshFailure ----------

        [Fact]
        public void Default_HttpRequestExceptionWithoutStatus_IsTransient()
            => Assert.True(SessionBearerHandler.DefaultIsTransientRefreshFailure(new HttpRequestException("net"), default));

        [Fact]
        public void Default_Http503_IsTransient()
            => Assert.True(SessionBearerHandler.DefaultIsTransientRefreshFailure(
                new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable), default));

        [Theory]
        [InlineData(HttpStatusCode.BadRequest)]
        [InlineData(HttpStatusCode.NotFound)]
        public void Default_Http4xx_IsNotTransient(HttpStatusCode status)
            => Assert.False(SessionBearerHandler.DefaultIsTransientRefreshFailure(new HttpRequestException("x", null, status), default));

        [Fact]
        public void Default_CancellationNotRequestedByCaller_IsTransient()
            => Assert.True(SessionBearerHandler.DefaultIsTransientRefreshFailure(new OperationCanceledException(), CancellationToken.None));

        [Fact]
        public void Default_CancellationRequestedByCaller_IsNotTransient()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.False(SessionBearerHandler.DefaultIsTransientRefreshFailure(new OperationCanceledException(), cts.Token));
        }

        [Fact]
        public void Default_Timeout_IsTransient()
            => Assert.True(SessionBearerHandler.DefaultIsTransientRefreshFailure(new TimeoutException(), default));

        [Fact]
        public void Default_InvalidOperation_IsNotTransient()
            => Assert.False(SessionBearerHandler.DefaultIsTransientRefreshFailure(new InvalidOperationException(), default));

        // ---------- Handler behaviour ----------

        [Fact]
        public async Task ProactiveRefresh_Transient503_FallsBackToOldToken()
        {
            var h = new Harness(accessLifetime: TimeSpan.FromSeconds(20),
                refreshThrows: new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable));
            using var response = await h.Client.GetAsync("http://api/x");
            Assert.Equal(1, h.RefreshCalls);
            Assert.Equal("old-access", h.Inner.LastBearer);
            Assert.Equal(1, h.Inner.Calls);
        }

        [Fact]
        public async Task ProactiveRefresh_Http400_Propagates_NoRequestSent()
        {
            var h = new Harness(accessLifetime: TimeSpan.FromSeconds(20),
                refreshThrows: new HttpRequestException("x", null, HttpStatusCode.BadRequest));
            await Assert.ThrowsAsync<HttpRequestException>(() => h.Client.GetAsync("http://api/x"));
            Assert.Equal(0, h.Inner.Calls);
        }

        [Fact]
        public async Task ProactiveRefresh_TransientButOldTokenBelowMargin_Propagates()
        {
            var h = new Harness(accessLifetime: TimeSpan.FromSeconds(3),
                refreshThrows: new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable));
            await Assert.ThrowsAsync<HttpRequestException>(() => h.Client.GetAsync("http://api/x"));
            Assert.Equal(0, h.Inner.Calls);
        }

        [Fact]
        public async Task ProactiveRefresh_MarginMeasuredAfterRefreshAttempt()
        {
            // 20s left before the attempt; the refresh takes 18s, so only 2s remain (< 5s margin).
            var h = new Harness(accessLifetime: TimeSpan.FromSeconds(20),
                refreshThrows: new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable),
                refreshDuration: TimeSpan.FromSeconds(18));
            await Assert.ThrowsAsync<HttpRequestException>(() => h.Client.GetAsync("http://api/x"));
            Assert.Equal(0, h.Inner.Calls);
        }

        [Fact]
        public async Task CustomPredicate_ReplacesDefault()
        {
            var calls = 0;
            // A 400 is "transient" for the custom predicate, so it falls back; the default would have thrown.
            var h = new Harness(accessLifetime: TimeSpan.FromSeconds(20),
                refreshThrows: new HttpRequestException("x", null, HttpStatusCode.BadRequest),
                configure: o => o.IsTransientRefreshFailure = (_, _) => { calls++; return true; });
            using var response = await h.Client.GetAsync("http://api/x");
            Assert.Equal(1, calls);
            Assert.Equal("old-access", h.Inner.LastBearer);
        }

        [Fact]
        public async Task CustomPredicate_False_PropagatesEvenForTransientDefault()
        {
            var h = new Harness(accessLifetime: TimeSpan.FromSeconds(20),
                refreshThrows: new HttpRequestException("net"),
                configure: o => o.IsTransientRefreshFailure = (_, _) => false);
            await Assert.ThrowsAsync<HttpRequestException>(() => h.Client.GetAsync("http://api/x"));
            Assert.Equal(0, h.Inner.Calls);
        }

        // ---------- Test infrastructure ----------

        private sealed class Harness
        {
            public FakeTimeProvider Clock { get; } = new(Start);
            public RecordingHandler Inner { get; } = new();
            public int RefreshCalls => _auth.RefreshCalls;
            public HttpClient Client { get; }
            private readonly FakeAuthApiClient _auth;

            public Harness(TimeSpan accessLifetime, Exception refreshThrows, TimeSpan? refreshDuration = null,
                Action<WebAuthOptions>? configure = null)
            {
                var options = new WebAuthOptions();
                configure?.Invoke(options);
                var tokens = new WebSessionTokens("old-access", Start.UtcDateTime + accessLifetime, "refresh", Start.UtcDateTime.AddDays(1));
                var properties = new AuthenticationProperties();
                tokens.WriteTo(properties, options);

                _auth = new FakeAuthApiClient(refreshThrows, () => Clock.Advance(refreshDuration ?? TimeSpan.Zero));
                var services = new ServiceCollection();
                services.AddSingleton<IOptions<WebAuthOptions>>(Options.Create(options));
                services.AddSingleton<TimeProvider>(Clock);
                services.AddSingleton<IAuthApiClient>(_auth);
                services.AddSingleton<IAuthenticationService>(new FakeAuthenticationService(properties, options.Scheme, options.SessionIdClaimType));
                var provider = services.BuildServiceProvider();

                var context = new DefaultHttpContext { RequestServices = provider };
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(options.SessionIdClaimType, "sid")], "test"));
                var accessor = new HttpContextAccessor { HttpContext = context };

                var handler = new SessionBearerHandler(accessor, Clock, Options.Create(options)) { InnerHandler = Inner };
                Client = new HttpClient(handler);
            }
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            public int Calls { get; private set; }
            public string? LastBearer { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                LastBearer = request.Headers.Authorization?.Parameter;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }

        private sealed class FakeAuthApiClient(Exception refreshThrows, Action onRefresh) : IAuthApiClient
        {
            public int RefreshCalls { get; private set; }

            public Task<AuthTokenPair> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
            {
                RefreshCalls++;
                onRefresh();
                throw refreshThrows;
            }

            public Task<CurrentUserInfo> GetCurrentUserAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task LogoutAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        private sealed class FakeAuthenticationService(AuthenticationProperties properties, string scheme, string sidClaim) : IAuthenticationService
        {
            public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme1)
            {
                var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(sidClaim, "sid")], "test"));
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, properties, scheme)));
            }

            public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
            public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
            public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
            public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        }
    }
}
