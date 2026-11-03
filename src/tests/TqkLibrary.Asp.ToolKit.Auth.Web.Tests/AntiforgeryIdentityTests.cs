using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using TqkLibrary.Asp.ToolKit.Auth.Web.Models;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Tests
{
    /// <summary>
    /// Antiforgery binds its token to the principal identity (sub / NameIdentifier / upn). The session-only principal
    /// (API unreachable) and the full principal (after <c>me</c>) must give the same identity, otherwise a form issued
    /// in one state fails to post in the other.
    /// </summary>
    public class AntiforgeryIdentityTests
    {
        private const string HeaderName = "X-CSRF-TOKEN";

        private static Task<WebAuthTestApp> StartAsync()
            => WebAuthTestApp.StartAsync(
                configureServices: services => services.AddAntiforgery(o => o.HeaderName = HeaderName),
                mapEndpoints: endpoints =>
                {
                    endpoints.MapGet("/af/token", (HttpContext context, IAntiforgery antiforgery)
                        => Results.Text(antiforgery.GetAndStoreTokens(context).RequestToken!));
                    endpoints.MapPost("/af/post", async (HttpContext context, IAntiforgery antiforgery)
                        => await antiforgery.IsRequestValidAsync(context) ? Results.Ok() : Results.BadRequest());
                });

        private static async Task<string> GetTokenAsync(WebAuthTestApp app)
        {
            using var response = await app.SendAsync(HttpMethod.Get, "/af/token");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private static async Task<HttpStatusCode> PostAsync(WebAuthTestApp app, string token)
        {
            using var response = await app.SendAsync(HttpMethod.Post, "/af/post", r => r.Headers.Add(HeaderName, token));
            return response.StatusCode;
        }

        private static CurrentUserInfo User() => new() { Id = "u-1", UserName = "editor", Permissions = ["items.edit"] };

        [Fact]
        public async Task TokenIssuedWithSessionOnlyPrincipal_ValidatesWithFullPrincipal()
        {
            await using var app = await StartAsync();
            await app.SignInAsync("s-1");
            app.Api.Me = () => throw new HttpRequestException("down");
            var token = await GetTokenAsync(app);

            app.Api.Me = User;
            Assert.Equal(HttpStatusCode.OK, await PostAsync(app, token));
        }

        [Fact]
        public async Task TokenIssuedWithFullPrincipal_ValidatesWithSessionOnlyPrincipal()
        {
            await using var app = await StartAsync();
            await app.SignInAsync("s-1");
            app.Api.Me = User;
            var token = await GetTokenAsync(app);

            app.Services.GetRequiredService<CurrentUserCache>().Clear();
            app.Api.Me = () => throw new HttpRequestException("down");
            Assert.Equal(HttpStatusCode.OK, await PostAsync(app, token));
        }

        [Fact]
        public async Task TokenOfAnotherSession_IsRejected()
        {
            await using var app = await StartAsync();
            await app.SignInAsync("s-1");
            app.Api.Me = User;
            var token = await GetTokenAsync(app);

            await app.SignInAsync("s-2");
            Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(app, token));
        }
    }
}
