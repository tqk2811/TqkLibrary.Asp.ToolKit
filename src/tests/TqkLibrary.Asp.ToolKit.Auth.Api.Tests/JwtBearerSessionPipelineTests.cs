using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using TqkLibrary.Asp.ToolKit.Auth.Api.Extensions;
using TqkLibrary.Asp.ToolKit.Auth.Api.Interfaces;
using TqkLibrary.Asp.ToolKit.Auth.Api.Models;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Tests
{
    /// <summary><c>AddTqkApiAuth</c> + <c>AddTqkJwtBearer</c> in a TestServer: the session check runs on every token.</summary>
    public class JwtBearerSessionPipelineTests
    {
        private const string RejectedHeader = "X-Session-Rejected";

        private sealed class FakeSessionSource : ISessionAccessSource
        {
            private int _calls;
            public int Calls => _calls;
            public Func<string, string, SessionAccessRecord?> Get { get; set; } = (_, userId) => new SessionAccessRecord
            {
                UserId = userId, UserName = "editor", Email = "e@x.test", Roles = ["Editor"], Permissions = ["items.edit"],
            };

            public Task<SessionAccessRecord?> GetAsync(string sessionId, string userId, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _calls);
                return Task.FromResult(Get(sessionId, userId));
            }
        }

        private sealed class App : IAsyncDisposable
        {
            private readonly IHost _host;
            public FakeSessionSource Source { get; } = new();
            public HttpClient Client { get; private set; } = null!;
            public IServiceProvider Services => _host.Services;

            private App(Action<JwtBearerOptions>? configureJwt, Action<ApiAuthOptions>? configureApi)
            {
                _host = new HostBuilder()
                    .ConfigureWebHost(web => web
                        .UseTestServer()
                        .ConfigureServices(services =>
                        {
                            services.AddTqkApiAuth(o =>
                            {
                                o.Issuer = "test-issuer";
                                o.SigningKey = TokenIssuingTests.SigningKey;
                                configureApi?.Invoke(o);
                            });
                            services.AddSingleton<ISessionAccessSource>(Source);
                            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddTqkJwtBearer(configureJwt);
                            services.AddAuthorization();
                            services.AddRouting();
                        })
                        .Configure(app =>
                        {
                            app.Use(async (context, next) =>
                            {
                                context.Response.OnStarting(() =>
                                {
                                    context.Response.Headers[RejectedHeader] = SessionTokenValidator.IsSessionRejected(context) ? "1" : "0";
                                    return Task.CompletedTask;
                                });
                                await next(context);
                            });
                            app.UseRouting();
                            app.UseAuthentication();
                            app.UseAuthorization();
                            app.UseEndpoints(e => e.MapGet("/me", (ClaimsPrincipal user)
                                    => string.Join("\n", user.Claims.Select(c => c.Type + "=" + c.Value)))
                                .RequireAuthorization());
                        }))
                    .Build();
            }

            public static async Task<App> StartAsync(Action<JwtBearerOptions>? configureJwt = null, Action<ApiAuthOptions>? configureApi = null)
            {
                var app = new App(configureJwt, configureApi);
                await app._host.StartAsync();
                app.Client = app._host.GetTestClient();
                return app;
            }

            public string IssueToken(string userId = "u-1", string sessionId = "s-1")
                => Services.GetRequiredService<AccessTokenFactory>().Create(userId, sessionId, DateTime.UtcNow).Token;

            public Task<HttpResponseMessage> GetMeAsync(string token)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/me");
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

        [Fact]
        public async Task ValidSession_Passes_ClaimsReplacedWithCurrentAccess()
        {
            await using var app = await App.StartAsync();

            using var response = await app.GetMeAsync(app.IssueToken());
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
        }

        [Fact]
        public async Task RejectedSession_Returns401_AndMarksSessionRejected()
        {
            await using var app = await App.StartAsync();
            app.Source.Get = (_, _) => null;

            using var response = await app.GetMeAsync(app.IssueToken());

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("1", Rejected(response));
        }

        [Fact]
        public async Task SessionOfAnotherUser_Returns401()
        {
            await using var app = await App.StartAsync();
            app.Source.Get = (_, _) => new SessionAccessRecord { UserId = "u-2", UserName = "other" };

            using var response = await app.GetMeAsync(app.IssueToken());

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task SignedTokenWithoutSessionClaim_Returns401_SourceNotCalled()
        {
            await using var app = await App.StartAsync();
            var options = app.Services.GetRequiredService<IOptions<ApiAuthOptions>>().Value;
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = options.Issuer,
                Audience = options.EffectiveAudience,
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(options.CreateSigningKey(), SecurityAlgorithms.HmacSha256),
                Claims = new Dictionary<string, object> { ["sub"] = "u-1" },
            });

            using var response = await app.GetMeAsync(token);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("1", Rejected(response));
            Assert.Equal(0, app.Source.Calls);
        }

        [Fact]
        public async Task BadSignature_Returns401_NotMarkedAsSessionRejected()
        {
            await using var app = await App.StartAsync();
            var token = app.IssueToken();

            using var response = await app.GetMeAsync(token[..^2] + (token[^2] == 'A' ? "BB" : "AA"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("0", Rejected(response));
            Assert.Equal(0, app.Source.Calls);
        }

        [Fact]
        public async Task CachedAccess_SecondRequestDoesNotHitSource_UntilInvalidated()
        {
            await using var app = await App.StartAsync();
            var token = app.IssueToken();

            (await app.GetMeAsync(token)).Dispose();
            (await app.GetMeAsync(token)).Dispose();
            Assert.Equal(1, app.Source.Calls);

            app.Source.Get = (_, _) => null;
            app.Services.GetRequiredService<SessionAccessCache>().InvalidateSession("s-1");
            using var response = await app.GetMeAsync(token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(2, app.Source.Calls);
        }

        [Fact]
        public async Task ConfiguredClaimTypes_UsedByFactoryAndValidator()
        {
            await using var app = await App.StartAsync(configureApi: o =>
            {
                o.UserIdClaimType = "uid";
                o.SessionIdClaimType = "app_sid";
            });
            string? seenSession = null;
            app.Source.Get = (sid, uid) => { seenSession = sid; return new SessionAccessRecord { UserId = uid, UserName = "n" }; };

            using var response = await app.GetMeAsync(app.IssueToken("u-9", "s-9"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("s-9", seenSession);
        }

        [Fact]
        public async Task AppEvents_OwnOnTokenValidatedRunsFirst_SessionCheckStillRuns()
        {
            var appHandlerCalls = 0;
            var sourceCallsSeenByAppHandler = -1;
            App? app = null;
            await using var started = await App.StartAsync(jwt => jwt.Events = new JwtBearerEvents
            {
                OnTokenValidated = _ =>
                {
                    appHandlerCalls++;
                    sourceCallsSeenByAppHandler = app!.Source.Calls;
                    return Task.CompletedTask;
                },
            });
            app = started;

            using var allowed = await app.GetMeAsync(app.IssueToken());
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            Assert.Equal(1, appHandlerCalls);
            Assert.Equal(0, sourceCallsSeenByAppHandler);
            Assert.Equal(1, app.Source.Calls);

            // The session check is not bypassed by the app's own handler.
            app.Source.Get = (_, _) => null;
            app.Services.GetRequiredService<SessionAccessCache>().InvalidateAll();
            using var rejected = await app.GetMeAsync(app.IssueToken());
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }

        [Fact]
        public async Task AppEvents_OwnHandlerFails_SessionCheckSkipped_401()
        {
            await using var app = await App.StartAsync(jwt => jwt.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    context.Fail("app says no");
                    return Task.CompletedTask;
                },
            });

            using var response = await app.GetMeAsync(app.IssueToken());

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(0, app.Source.Calls);
            Assert.Equal("0", Rejected(response));
        }

        [Fact]
        public async Task EventsType_IsRefused()
        {
            await using var app = await App.StartAsync(jwt => jwt.EventsType = typeof(JwtBearerEvents));

            var ex = Assert.Throws<InvalidOperationException>(()
                => app.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme));
            Assert.Contains("EventsType", ex.Message);
        }
    }
}
