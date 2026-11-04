using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Security.Claims;
using TqkLibrary.Asp.ToolKit.Auth.Web.Extensions;
using TqkLibrary.Asp.ToolKit.Auth.Web.Interfaces;
using TqkLibrary.Asp.ToolKit.Auth.Web.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Tests
{
    /// <summary>
    /// TestServer host with the real cookie scheme from <c>AddTqkWebAuth</c> and the <c>UseTqkWebSession</c> middlewares.
    /// <c>/sign-in?sid=...</c> signs in through <see cref="WebSessionManager"/>, <c>/whoami</c> prints the principal,
    /// <c>/expired</c> sets a header then throws <see cref="SessionExpiredException"/>. Cookies are carried by hand.
    /// </summary>
    internal sealed class WebAuthTestApp : IAsyncDisposable
    {
        public const string LoginPath = "/account/login";
        public const string PartialHeader = "X-Partial";

        private readonly IHost _host;
        private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

        private WebAuthTestApp(IHost host, FakeAuthApi api)
        {
            _host = host;
            Api = api;
            Client = host.GetTestClient();
        }

        public FakeAuthApi Api { get; }
        public HttpClient Client { get; }
        public IServiceProvider Services => _host.Services;
        public IReadOnlyDictionary<string, string> Cookies => _cookies;

        public static async Task<WebAuthTestApp> StartAsync(Action<WebAuthOptions>? configure = null,
            Action<IServiceCollection>? configureServices = null, Action<IEndpointRouteBuilder>? mapEndpoints = null,
            Action<IApplicationBuilder>? beforeWebSession = null, Action<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>? configureCookie = null)
        {
            var api = new FakeAuthApi();
            var host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddDataProtection().UseEphemeralDataProtectionProvider();
                        services.AddTqkWebAuth(cookie =>
                        {
                            cookie.LoginPath = LoginPath;
                            configureCookie?.Invoke(cookie);
                        }, configure);
                        services.Replace(ServiceDescriptor.Singleton<IAuthApiClient>(api));
                        services.AddRouting();
                        configureServices?.Invoke(services);
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseAuthentication();
                        beforeWebSession?.Invoke(app);
                        app.UseTqkWebSession();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapGet("/sign-in", async (HttpContext context, WebSessionManager sessions, string sid,
                                bool? persistent, int? expiresInDays) =>
                            {
                                await sessions.SignInAsync(context, new AuthSession
                                {
                                    SessionToken = sid + ".secret",
                                    UserId = "u-1",
                                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(expiresInDays ?? 14),
                                }, persistent: persistent == true);
                                return Results.Ok();
                            });
                            endpoints.MapGet("/api-call", async (HttpContext context) =>
                            {
                                var authorization = await CallApiAsync(context);
                                return Results.Text(authorization);
                            });
                            // A cookie the way 1.x issued it: principal only, no session token in the ticket.
                            endpoints.MapGet("/sign-in-legacy", async (HttpContext context, WebSessionPrincipalFactory principals, string sid) =>
                            {
                                await context.SignInAsync(TqkScheme(context), principals.CreateSessionOnly(sid, "u-1"));
                                return Results.Ok();
                            });
                            endpoints.MapGet("/whoami", (HttpContext context) => Results.Text(Describe(context.User)));
                            endpoints.MapGet("/expired", (HttpContext context) =>
                            {
                                context.Response.Headers[PartialHeader] = "1";
                                throw new SessionExpiredException("Session gone.");
                            });
                            mapEndpoints?.Invoke(endpoints);
                        });
                    }))
                .Build();
            await host.StartAsync();
            return new WebAuthTestApp(host, api);
        }

        private static string TqkScheme(HttpContext context)
            => context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebAuthOptions>>().Value.Scheme;

        /// <summary>One API call through <see cref="SessionBearerHandler"/>; returns the Authorization header it sent.</summary>
        private static async Task<string> CallApiAsync(HttpContext context)
        {
            var handler = context.RequestServices.GetRequiredService<SessionBearerHandler>();
            var inner = new BearerEcho();
            handler.InnerHandler = inner;
            using var client = new HttpClient(handler);
            using var response = await client.GetAsync("http://api/items");
            return inner.LastAuthorization ?? "none";
        }

        /// <summary>One line per claim "type=value", first line "auth=true|false".</summary>
        private static string Describe(ClaimsPrincipal user)
            => "auth=" + (user.Identity?.IsAuthenticated == true).ToString().ToLowerInvariant() + "\n"
               + string.Join("\n", user.Claims.Select(c => c.Type + "=" + c.Value));

        public async Task<HttpResponseMessage> SignInAsync(string sessionId, bool persistent = false, int? expiresInDays = null)
        {
            var url = "/sign-in?sid=" + sessionId + "&persistent=" + (persistent ? "true" : "false")
                + (expiresInDays is { } days ? "&expiresInDays=" + days : string.Empty);
            var response = await SendAsync(HttpMethod.Get, url);
            response.EnsureSuccessStatusCode();
            return response;
        }

        public async Task<string> WhoAmIAsync()
        {
            using var response = await SendAsync(HttpMethod.Get, "/whoami");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        /// <summary>Sends the stored cookies and applies every Set-Cookie of the response (deletions included).</summary>
        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Action<HttpRequestMessage>? configure = null)
        {
            using var request = new HttpRequestMessage(method, url);
            if (_cookies.Count > 0)
                request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => c.Key + "=" + c.Value)));
            configure?.Invoke(request);
            var response = await Client.SendAsync(request);
            foreach (var setCookie in SetCookies(response))
            {
                var pair = setCookie.Split(';', 2)[0];
                var eq = pair.IndexOf('=');
                var name = pair[..eq];
                var value = pair[(eq + 1)..];
                if (value.Length == 0 || setCookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
                    _cookies.Remove(name);
                else
                    _cookies[name] = value;
            }
            return response;
        }

        public static IReadOnlyList<string> SetCookies(HttpResponseMessage response)
            => response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    /// <summary>Records the Authorization header the bearer handler attached.</summary>
    internal sealed class BearerEcho : HttpMessageHandler
    {
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    /// <summary>Scripted <see cref="IAuthApiClient"/>: <see cref="Me"/> decides each <c>me</c> call.</summary>
    internal sealed class FakeAuthApi : IAuthApiClient
    {
        private int _meCalls;
        public int MeCalls => _meCalls;
        public Func<CurrentUserInfo> Me { get; set; } = () => new CurrentUserInfo { Id = "u-1", UserName = "editor" };

        public Task<CurrentUserInfo> GetCurrentUserAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _meCalls);
            return Task.FromResult(Me());
        }

        public Task LogoutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
