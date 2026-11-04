using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using TqkLibrary.Asp.ToolKit.Auth.Web.Models;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Tests
{
    /// <summary>
    /// <see cref="SessionExpiredMiddleware"/> and <see cref="CurrentUserMiddleware"/> through a real cookie pipeline.
    /// </summary>
    public class WebSessionPipelineTests
    {
        private const string Sid = "s-1";

        // ---------- SessionExpiredMiddleware ----------

        [Fact]
        public async Task SessionExpired_NormalRequest_RedirectsToLoginWithReturnUrl_AndDeletesCookie()
        {
            await using var app = await WebAuthTestApp.StartAsync();
            await app.SignInAsync(Sid);
            Assert.NotEmpty(app.Cookies);

            using var response = await app.SendAsync(HttpMethod.Get, "/expired?page=2");

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(WebAuthTestApp.LoginPath + "?ReturnUrl=%2Fexpired%3Fpage%3D2", response.Headers.Location?.OriginalString);
            AssertCookieDeleted(response, app);
        }

        [Fact]
        public async Task SessionExpired_ResponseClearedBeforeSignOut_PartialHeaderGone_DeleteCookieKept()
        {
            await using var app = await WebAuthTestApp.StartAsync();
            await app.SignInAsync(Sid);

            using var response = await app.SendAsync(HttpMethod.Get, "/expired");

            // Clear() ran FIRST (the header written by the endpoint is gone), the sign-out AFTER (its Set-Cookie survives).
            Assert.False(response.Headers.Contains(WebAuthTestApp.PartialHeader));
            AssertCookieDeleted(response, app);
        }

        [Theory]
        [InlineData("X-Requested-With", "XMLHttpRequest")]
        [InlineData("Accept", "application/json")]
        public async Task SessionExpired_AjaxRequest_Returns401Json_WithRedirect(string header, string value)
        {
            await using var app = await WebAuthTestApp.StartAsync();
            await app.SignInAsync(Sid);

            using var response = await app.SendAsync(HttpMethod.Get, "/expired?page=2", r => r.Headers.TryAddWithoutValidation(header, value));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(json.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("Session gone.", json.RootElement.GetProperty("message").GetString());
            Assert.Equal(WebAuthTestApp.LoginPath + "?ReturnUrl=%2Fexpired%3Fpage%3D2", json.RootElement.GetProperty("redirect").GetString());
            AssertCookieDeleted(response, app);
        }

        // ---------- CurrentUserMiddleware ----------

        [Fact]
        public async Task Me_LoadedIntoPrincipal_AndCachedAcrossRequests()
        {
            await using var app = await WebAuthTestApp.StartAsync();
            var permissions = new List<string> { "members.view" };
            app.Api.Me = () => new CurrentUserInfo
            {
                Id = "u-1", UserName = "editor", Email = "e@x.test", Roles = ["Editor"], Permissions = [.. permissions],
            };
            await app.SignInAsync(Sid);

            var first = await app.WhoAmIAsync();
            permissions.Add("users.view");
            var second = await app.WhoAmIAsync();

            Assert.Equal(1, app.Api.MeCalls);
            Assert.Contains("auth=true", first);
            Assert.Contains(ClaimTypes.NameIdentifier + "=u-1", first);
            Assert.Contains(ClaimTypes.Name + "=editor", first);
            Assert.Contains(ClaimTypes.Email + "=e@x.test", first);
            Assert.Contains(ClaimTypes.Role + "=Editor", first);
            Assert.Contains("permission=members.view", first);
            Assert.Contains("tqk_sid=" + Sid, first);
            Assert.DoesNotContain("permission=users.view", second);
        }

        [Fact]
        public async Task Me_CacheRemoved_AsksAgain_AndSeesNewPermissions()
        {
            await using var app = await WebAuthTestApp.StartAsync();
            var permissions = new List<string> { "members.view" };
            app.Api.Me = () => new CurrentUserInfo { Id = "u-1", UserName = "editor", Permissions = [.. permissions] };
            await app.SignInAsync(Sid);
            await app.WhoAmIAsync();

            permissions.Add("users.view");
            app.Services.GetRequiredService<CurrentUserCache>().Remove(Sid);
            var after = await app.WhoAmIAsync();

            Assert.Equal(2, app.Api.MeCalls);
            Assert.Contains("permission=users.view", after);
        }

        [Fact]
        public async Task Me_SignInAgainWithSameSession_DropsCachedUser()
        {
            await using var app = await WebAuthTestApp.StartAsync();
            var permission = "members.view";
            app.Api.Me = () => new CurrentUserInfo { Id = "u-1", UserName = "editor", Permissions = [permission] };
            await app.SignInAsync(Sid);
            await app.WhoAmIAsync();

            permission = "users.view";
            await app.SignInAsync(Sid);
            var after = await app.WhoAmIAsync();

            Assert.Contains("permission=users.view", after);
            Assert.DoesNotContain("permission=members.view", after);
        }

        [Fact]
        public async Task Me_SessionExpired_ClearsCookie_AndRunsAsAnonymous()
        {
            await using var app = await WebAuthTestApp.StartAsync();
            app.Api.Me = () => throw new SessionExpiredException();
            await app.SignInAsync(Sid);

            using var response = await app.SendAsync(HttpMethod.Get, "/whoami");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.StartsWith("auth=false", body);
            Assert.DoesNotContain("tqk_sid=", body);
            AssertCookieDeleted(response, app);
        }

        [Theory]
        [InlineData("http")]
        [InlineData("timeout")]
        public async Task Me_TransientFailure_RunsWithSessionOnlyPrincipal_AndIsNotCached(string kind)
        {
            await using var app = await WebAuthTestApp.StartAsync();
            app.Api.Me = () => throw (kind == "http" ? new HttpRequestException("down") : new OperationCanceledException("slow"));
            await app.SignInAsync(Sid);

            var first = await app.WhoAmIAsync();
            await app.WhoAmIAsync();

            // Session-only principal = exactly the cookie content: session id, user id, stable identity. No name/role.
            Assert.Equal("auth=true\ntqk_sid=" + Sid + "\n" + ClaimTypes.NameIdentifier + "=u-1\nsub=" + Sid, first);
            Assert.NotEmpty(app.Cookies); // the session is kept
            Assert.Equal(2, app.Api.MeCalls);
        }

        [Fact]
        public async Task ApiCall_CarriesSessionTokenFromCookie()
        {
            await using var app = await WebAuthTestApp.StartAsync();
            await app.SignInAsync(Sid);

            using var response = await app.SendAsync(HttpMethod.Get, "/api-call");

            Assert.Equal("Bearer " + Sid + "." + WebAuthTestApp.Secret, await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task ApiCall_InSignInRequest_UsesNewToken()
        {
            await using var app = await WebAuthTestApp.StartAsync(mapEndpoints: endpoints => endpoints.MapGet("/sign-in-and-call",
                async (HttpContext context, WebSessionManager sessions) =>
                {
                    await sessions.SignInAsync(context, new AuthSession
                    {
                        SessionToken = "s-9." + WebAuthTestApp.Secret, UserId = "u-9", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                    }, persistent: false);
                    // No manual context.User here: SignInAsync must make the new session visible to the bearer handler.
                    var handler = context.RequestServices.GetRequiredService<SessionBearerHandler>();
                    var inner = new BearerEcho();
                    handler.InnerHandler = inner;
                    using var client = new HttpClient(handler);
                    (await client.GetAsync("http://api/items")).Dispose();
                    return Results.Text(inner.LastAuthorization ?? "none");
                }));

            using var response = await app.SendAsync(HttpMethod.Get, "/sign-in-and-call");

            Assert.Equal("Bearer s-9." + WebAuthTestApp.Secret, await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task SignIn_PersistentCookie_ExpiresAtGivenAbsoluteExpiry()
        {
            await using var app = await WebAuthTestApp.StartAsync();

            using var response = await app.SignInAsync(Sid, persistent: true, expiresInDays: 90);

            var cookie = Assert.Single(WebAuthTestApp.SetCookies(response), c => c.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal));
            var expires = DateTimeOffset.Parse(cookie.Split("expires=", 2)[1].Split(';', 2)[0], System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(expires, DateTimeOffset.UtcNow.AddDays(89), DateTimeOffset.UtcNow.AddDays(91));
        }

        [Fact]
        public async Task SignIn_NotPersistent_SessionCookie()
        {
            await using var app = await WebAuthTestApp.StartAsync();

            using var response = await app.SignInAsync(Sid, persistent: false, expiresInDays: 90);

            var cookie = Assert.Single(WebAuthTestApp.SetCookies(response), c => c.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal));
            Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("nodot")]
        [InlineData(".secret")]
        [InlineData("s-1.")]
        [InlineData("s-1.secret")] // secret is not 43 chars
        [InlineData("s-1.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 42 chars
        [InlineData("s-1.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 44 chars
        [InlineData("s-1.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa+")] // not base64url
        [InlineData("s 1.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // id char not allowed
        [InlineData("s.1.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // '.' in the secret part is not base64url
        public async Task SignIn_MalformedToken_Throws(string token)
        {
            await using var app = await WebAuthTestApp.StartAsync(mapEndpoints: endpoints => endpoints.MapGet("/bad-sign-in",
                async (HttpContext context, WebSessionManager sessions) =>
                {
                    var ex = await Assert.ThrowsAsync<ArgumentException>(()
                        => sessions.SignInAsync(context, new AuthSession { SessionToken = token, UserId = "u-1", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) }, persistent: false));
                    return Results.Text(ex.ParamName ?? "");
                }));

            using var response = await app.SendAsync(HttpMethod.Get, "/bad-sign-in");

            Assert.Equal("session", await response.Content.ReadAsStringAsync());
            Assert.Empty(app.Cookies);
        }

        [Fact]
        public async Task SignIn_SessionIdLongerThan128_Throws_128IsAccepted()
        {
            await using var app = await WebAuthTestApp.StartAsync(mapEndpoints: endpoints => endpoints.MapGet("/long-sign-in",
                async (HttpContext context, WebSessionManager sessions) =>
                {
                    await Assert.ThrowsAsync<ArgumentException>(()
                        => sessions.SignInAsync(context, new AuthSession { SessionToken = new string('a', 129) + "." + WebAuthTestApp.Secret, UserId = "u-1", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) }, persistent: false));
                    await sessions.SignInAsync(context, new AuthSession { SessionToken = new string('a', 128) + "." + WebAuthTestApp.Secret, UserId = "u-1", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) }, persistent: false);
                    return Results.Ok();
                }));

            using var response = await app.SendAsync(HttpMethod.Get, "/long-sign-in");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task SignIn_AlreadyExpiredSession_Throws_NoCookie()
        {
            await using var app = await WebAuthTestApp.StartAsync(mapEndpoints: endpoints => endpoints.MapGet("/expired-sign-in",
                async (HttpContext context, WebSessionManager sessions) =>
                {
                    var ex = await Assert.ThrowsAsync<ArgumentException>(()
                        => sessions.SignInAsync(context, new AuthSession { SessionToken = "s-1." + WebAuthTestApp.Secret, UserId = "u-1", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, persistent: false));
                    return Results.Text(ex.ParamName ?? "");
                }));

            using var response = await app.SendAsync(HttpMethod.Get, "/expired-sign-in");

            Assert.Equal("session", await response.Content.ReadAsStringAsync());
            Assert.Empty(app.Cookies);
        }

        [Fact]
        public async Task Ticket_ReplaceDuringLoadOfTheOldCookie_KeepsTheReplacedToken()
        {
            var auth = new BlockingAuthenticationService();
            var services = new ServiceCollection()
                .AddSingleton<Microsoft.AspNetCore.Authentication.IAuthenticationService>(auth)
                .AddSingleton(Microsoft.Extensions.Options.Options.Create(new WebAuthOptions()))
                .BuildServiceProvider();
            var context = new DefaultHttpContext { RequestServices = services };
            var ticket = WebSessionTicket.For(context);

            var load = ticket.GetSessionTokenAsync(); // reads the OLD cookie, parked inside AuthenticateAsync
            await auth.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ticket.Replace("new-sid." + WebAuthTestApp.Secret);
            auth.Release.SetResult();

            var expected = "new-sid." + WebAuthTestApp.Secret;
            Assert.Equal(expected, await load.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(expected, await ticket.GetSessionTokenAsync());
        }

        private sealed class BlockingAuthenticationService : Microsoft.AspNetCore.Authentication.IAuthenticationService
        {
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
            {
                Entered.TrySetResult();
                await Release.Task;
                var identity = new ClaimsIdentity([new Claim("tqk_sid", "old-sid")], "test");
                var properties = new Microsoft.AspNetCore.Authentication.AuthenticationProperties();
                properties.StoreTokens([new Microsoft.AspNetCore.Authentication.AuthenticationToken { Name = WebSessionTicket.SessionTokenName, Value = "old-sid." + WebAuthTestApp.Secret }]);
                return Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(
                    new Microsoft.AspNetCore.Authentication.AuthenticationTicket(new ClaimsPrincipal(identity), properties, scheme ?? "x"));
            }

            public Task ChallengeAsync(HttpContext context, string? scheme, Microsoft.AspNetCore.Authentication.AuthenticationProperties? properties) => Task.CompletedTask;
            public Task ForbidAsync(HttpContext context, string? scheme, Microsoft.AspNetCore.Authentication.AuthenticationProperties? properties) => Task.CompletedTask;
            public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, Microsoft.AspNetCore.Authentication.AuthenticationProperties? properties) => Task.CompletedTask;
            public Task SignOutAsync(HttpContext context, string? scheme, Microsoft.AspNetCore.Authentication.AuthenticationProperties? properties) => Task.CompletedTask;
        }

        [Fact]
        public async Task CookieWithoutSessionToken_IsRejectedAtOnce_EvenWithoutApiCall()
        {
            await using var app = await WebAuthTestApp.StartAsync();
            using (await app.SendAsync(HttpMethod.Get, "/sign-in-legacy?sid=" + Sid))
                Assert.NotEmpty(app.Cookies);

            using var response = await app.SendAsync(HttpMethod.Get, "/whoami");

            Assert.StartsWith("auth=false", await response.Content.ReadAsStringAsync());
            Assert.Equal(0, app.Api.MeCalls);
            AssertCookieDeleted(response, app);
        }

        [Fact]
        public async Task UserOnValidatePrincipal_StillRuns_ForValidCookie()
        {
            var calls = 0;
            await using var app = await WebAuthTestApp.StartAsync(configureCookie: o =>
                o.Events.OnValidatePrincipal = _ =>
                {
                    calls++;
                    return Task.CompletedTask;
                });
            await app.SignInAsync(Sid);

            Assert.StartsWith("auth=true", await app.WhoAmIAsync());
            Assert.True(calls > 0);
        }

        [Fact]
        public async Task Anonymous_MeNeverCalled()
        {
            await using var app = await WebAuthTestApp.StartAsync();

            var body = await app.WhoAmIAsync();

            Assert.StartsWith("auth=false", body);
            Assert.Equal(0, app.Api.MeCalls);
        }

        private static void AssertCookieDeleted(HttpResponseMessage response, WebAuthTestApp app)
        {
            var setCookies = WebAuthTestApp.SetCookies(response);
            Assert.Contains(setCookies, c => c.StartsWith(".AspNetCore.Cookies=;", StringComparison.Ordinal)
                && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(app.Cookies);
        }
    }
}
