using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using TqkLibrary.Asp.ToolKit.Auth.Web.Models;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Tests
{
    /// <summary>
    /// Options of <see cref="CurrentUserMiddleware"/> (extra claims, failure mode/cache, skip predicate), of
    /// <see cref="SessionExpiredMiddleware"/> (AJAX detection/writer) and the cookie drop when app code catches
    /// <see cref="SessionExpiredException"/> thrown by <see cref="SessionBearerHandler"/>.
    /// </summary>
    public class WebSessionOptionsTests
    {
        private const string Sid = "s-1";

        // ---------- CurrentUserInfo.ExtraClaims ----------

        [Fact]
        public async Task ExtraClaims_AddedToPrincipal_ReservedTypesIgnored_AndCached()
        {
            await using var app = await WebAuthTestApp.StartAsync();
            app.Api.Me = () => new CurrentUserInfo
            {
                Id = "u-1",
                UserName = "editor",
                ExtraClaims =
                [
                    new("tenant", "t-9"), new("tqk_sid", "forged"), new("sub", "forged"),
                    new(ClaimTypes.Role, "forged"), new("permission", "forged"), new(ClaimTypes.NameIdentifier, "forged"),
                    new(ClaimTypes.Name, "forged"), new(ClaimTypes.Email, "forged"),
                ],
            };
            await app.SignInAsync(Sid);

            var first = await app.WhoAmIAsync();
            var second = await app.WhoAmIAsync();

            Assert.Contains("\ntenant=t-9", first);
            Assert.DoesNotContain("forged", first);
            Assert.Contains("\nsub=" + Sid, first);
            Assert.Equal(first, second);
            Assert.Equal(1, app.Api.MeCalls);
            // Never in the cookie: the extra claim lives only in the memory cache.
            Assert.DoesNotContain(app.Cookies.Values, v => v.Contains("t-9", StringComparison.Ordinal));
        }

        // ---------- CurrentUserFailureMode / CurrentUserFailureCacheFor ----------

        [Fact]
        public async Task FailureMode_Throw_FailsRequestWithCurrentUserUnavailable()
        {
            Exception? seen = null;
            await using var app = await WebAuthTestApp.StartAsync(
                configure: o => o.CurrentUserFailureMode = CurrentUserFailureMode.Throw,
                beforeWebSession: a => a.Use(async (context, next) =>
                {
                    try
                    {
                        await next(context);
                    }
                    catch (Exception ex)
                    {
                        seen = ex;
                        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    }
                }));
            app.Api.Me = () => throw new HttpRequestException("down");
            await app.SignInAsync(Sid);

            using var response = await app.SendAsync(HttpMethod.Get, "/whoami");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var error = Assert.IsType<CurrentUserUnavailableException>(seen);
            Assert.IsType<HttpRequestException>(error.InnerException);
            Assert.NotEmpty(app.Cookies); // the session is kept
        }

        [Fact]
        public async Task FailureCache_RemembersFailurePerSession_ThenRetriesAfterClear()
        {
            await using var app = await WebAuthTestApp.StartAsync(o => o.CurrentUserFailureCacheFor = TimeSpan.FromMinutes(1));
            app.Api.Me = () => throw new HttpRequestException("down");
            await app.SignInAsync(Sid);

            var first = await app.WhoAmIAsync();
            var second = await app.WhoAmIAsync();

            Assert.Equal("auth=true\ntqk_sid=" + Sid + "\nsub=" + Sid, first);
            Assert.Equal(first, second);
            Assert.Equal(1, app.Api.MeCalls);

            app.Services.GetRequiredService<CurrentUserCache>().Clear();
            app.Api.Me = () => new CurrentUserInfo { Id = "u-1", UserName = "editor" };
            Assert.Contains("=editor", await app.WhoAmIAsync());
            Assert.Equal(2, app.Api.MeCalls);
        }

        [Fact]
        public async Task FailureCache_NonTransientException_IsNotCached_ButStillAppliesFailureMode()
        {
            await using var app = await WebAuthTestApp.StartAsync(o => o.CurrentUserFailureCacheFor = TimeSpan.FromMinutes(1));
            app.Api.Me = () => throw new InvalidOperationException("bug");
            await app.SignInAsync(Sid);

            var first = await app.WhoAmIAsync();
            var second = await app.WhoAmIAsync();

            Assert.Equal("auth=true\ntqk_sid=" + Sid + "\nsub=" + Sid, first);
            Assert.Equal(first, second);
            Assert.Equal(2, app.Api.MeCalls);
        }

        [Theory]
        [InlineData(HttpStatusCode.ServiceUnavailable, true)]
        [InlineData(HttpStatusCode.TooManyRequests, true)]
        [InlineData(HttpStatusCode.BadRequest, false)]
        public async Task FailureCache_HttpStatus_OnlyOutagesAreCached(HttpStatusCode status, bool cached)
        {
            await using var app = await WebAuthTestApp.StartAsync(o => o.CurrentUserFailureCacheFor = TimeSpan.FromMinutes(1));
            app.Api.Me = () => throw new HttpRequestException("x", null, status);
            await app.SignInAsync(Sid);

            await app.WhoAmIAsync();
            await app.WhoAmIAsync();

            Assert.Equal(cached ? 1 : 2, app.Api.MeCalls);
        }

        [Fact]
        public async Task FailureCache_NotSet_EveryRequestAsksAgain()
        {
            await using var app = await WebAuthTestApp.StartAsync();
            app.Api.Me = () => throw new HttpRequestException("down");
            await app.SignInAsync(Sid);

            await app.WhoAmIAsync();
            await app.WhoAmIAsync();

            Assert.Equal(2, app.Api.MeCalls);
        }

        // ---------- SkipCurrentUser ----------

        [Fact]
        public async Task SkipCurrentUser_NoMeCall_SessionOnlyPrincipal_OtherPathsStillLoad()
        {
            await using var app = await WebAuthTestApp.StartAsync(o => o.SkipCurrentUser = context => context.Request.Query.ContainsKey("skip"));
            await app.SignInAsync(Sid);

            using (var skipped = await app.SendAsync(HttpMethod.Get, "/whoami?skip=1"))
                Assert.Equal("auth=true\ntqk_sid=" + Sid + "\nsub=" + Sid, await skipped.Content.ReadAsStringAsync());
            Assert.Equal(0, app.Api.MeCalls);

            Assert.Contains("=editor", await app.WhoAmIAsync());
            Assert.Equal(1, app.Api.MeCalls);

            // A cached user still applies to skipped requests.
            using var cached = await app.SendAsync(HttpMethod.Get, "/whoami?skip=1");
            Assert.Contains("=editor", await cached.Content.ReadAsStringAsync());
            Assert.Equal(1, app.Api.MeCalls);
        }

        // ---------- SessionExpiredMiddleware customisation ----------

        [Fact]
        public async Task CustomAjaxDetectionAndWriter_AreUsed()
        {
            await using var app = await WebAuthTestApp.StartAsync(o =>
            {
                o.IsAjaxRequest = request => request.Headers.ContainsKey("X-Ajax");
                o.WriteSessionExpiredAjaxResponse = (context, ex, redirect) =>
                {
                    context.Response.StatusCode = StatusCodes.Status419AuthenticationTimeout;
                    return context.Response.WriteAsync(ex.Message + "|" + redirect);
                };
            });
            await app.SignInAsync(Sid);

            using var custom = await app.SendAsync(HttpMethod.Get, "/expired", r => r.Headers.Add("X-Ajax", "1"));
            Assert.Equal((HttpStatusCode)StatusCodes.Status419AuthenticationTimeout, custom.StatusCode);
            Assert.Equal("Session gone.|" + WebAuthTestApp.LoginPath + "?ReturnUrl=%2Fexpired", await custom.Content.ReadAsStringAsync());
            Assert.Empty(app.Cookies);

            // The default detection no longer applies: Accept json is a normal navigation for this app.
            await app.SignInAsync(Sid);
            using var navigation = await app.SendAsync(HttpMethod.Get, "/expired", r => r.Headers.Add("Accept", "application/json"));
            Assert.Equal(HttpStatusCode.Redirect, navigation.StatusCode);
        }

        // ---------- SessionExpiredException caught by app code ----------

        private sealed class AlwaysUnauthorized : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        private static Task<WebAuthTestApp> StartWithCatchingEndpointAsync()
            => WebAuthTestApp.StartAsync(mapEndpoints: endpoints => endpoints.MapGet("/caught", async (HttpContext context) =>
            {
                var handler = context.RequestServices.GetRequiredService<SessionBearerHandler>();
                handler.InnerHandler = new AlwaysUnauthorized();
                using var client = new HttpClient(handler);
                try
                {
                    // Stream body: not replayable, so the 401 is a SessionExpiredException without any refresh.
                    using var content = new StreamContent(new MemoryStream([1, 2, 3]));
                    await client.PostAsync("http://api/upload", content);
                    return Results.Text("sent");
                }
                catch (SessionExpiredException)
                {
                    return Results.Text("caught");
                }
            }));

        /// <summary>Makes the bearer handler throw SessionExpiredException (registers the OnStarting drop), swallowing it.</summary>
        private static async Task TriggerExpiredCaughtAsync(HttpContext context)
        {
            var handler = context.RequestServices.GetRequiredService<SessionBearerHandler>();
            handler.InnerHandler = new AlwaysUnauthorized();
            using var client = new HttpClient(handler);
            try
            {
                using var content = new StreamContent(new MemoryStream([1, 2, 3]));
                await client.PostAsync("http://api/upload", content);
            }
            catch (SessionExpiredException)
            {
            }
        }

        [Fact]
        public async Task SessionExpiredThenNewSignInInSameRequest_KeepsNewCookie()
        {
            await using var app = await WebAuthTestApp.StartAsync(mapEndpoints: endpoints => endpoints.MapGet("/relogin", async (HttpContext context, WebSessionManager sessions) =>
            {
                await TriggerExpiredCaughtAsync(context);
                var now = DateTime.UtcNow;
                await sessions.SignInAsync(context, new TqkLibrary.Asp.ToolKit.Auth.Web.Models.AuthTokenPair
                {
                    SessionId = "s-2",
                    AccessToken = "access-s-2",
                    AccessTokenExpiresAt = now.AddMinutes(15),
                    RefreshToken = "s-2.secret",
                    RefreshTokenExpiresAt = now.AddDays(7),
                }, persistent: false);
                return Results.Text("ok");
            }));
            await app.SignInAsync(Sid);

            using var response = await app.SendAsync(HttpMethod.Get, "/relogin");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(WebAuthTestApp.SetCookies(response), c => c.StartsWith(".AspNetCore.Cookies=;", StringComparison.Ordinal));
            Assert.NotEmpty(app.Cookies);
            Assert.Contains("\ntqk_sid=s-2", await app.WhoAmIAsync());
        }

        [Fact]
        public async Task SessionExpiredCaughtThenRethrown_MiddlewareAndOnStartingProduceOneDeleteCookie()
        {
            await using var app = await WebAuthTestApp.StartAsync(mapEndpoints: endpoints => endpoints.MapGet("/rethrow", async (HttpContext context) =>
            {
                await TriggerExpiredCaughtAsync(context);
                throw new SessionExpiredException("Session gone.");
            }));
            await app.SignInAsync(Sid);

            using var response = await app.SendAsync(HttpMethod.Get, "/rethrow");

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Single(WebAuthTestApp.SetCookies(response), c => c.StartsWith(".AspNetCore.Cookies=;", StringComparison.Ordinal));
            Assert.Empty(app.Cookies);
        }

        [Fact]
        public async Task SessionExpiredCaughtByApp_CookieStillDropped()
        {
            await using var app = await StartWithCatchingEndpointAsync();
            await app.SignInAsync(Sid);
            Assert.NotEmpty(app.Cookies);

            using var response = await app.SendAsync(HttpMethod.Get, "/caught");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("caught", await response.Content.ReadAsStringAsync());
            Assert.Contains(WebAuthTestApp.SetCookies(response), c => c.StartsWith(".AspNetCore.Cookies=;", StringComparison.Ordinal));
            Assert.Empty(app.Cookies);
        }
    }
}
