using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TqkLibrary.Asp.ToolKit.Auth.Web.Interfaces;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Extensions
{
    /// <summary>Service registration of the web auth package.</summary>
    public static class WebAuthServiceCollectionExtensions
    {
        /// <summary>
        /// Cookie authentication whose encrypted ticket carries the API tokens (see <see cref="WebSessionManager"/>),
        /// plus the services of this package. The app must also register its <see cref="IAuthApiClient"/>
        /// implementation, add <see cref="AddTqkSessionBearer"/> to its API client, and call
        /// <c>UseAuthentication()</c> → <see cref="WebAuthApplicationBuilderExtensions.UseTqkWebSession"/> →
        /// <c>UseAuthorization()</c>.
        /// <para>Nothing is stored server side per session; with several instances only the Data Protection key ring
        /// must be shared (persist it with the app's own <c>AddDataProtection()</c> setup).</para>
        /// </summary>
        /// <param name="services">Services.</param>
        /// <param name="configureCookie">
        /// Cookie options of the app (name, login path, expiry...). The library sets <c>HttpOnly</c>, <c>SameSite=Lax</c>
        /// and <c>SlidingExpiration = false</c> first, so this delegate can override them. Sliding is off because the
        /// ticket already slides with the API session when tokens are refreshed (never beyond the refresh token
        /// expiry); the cookie handler would otherwise extend the ticket past the refresh token.
        /// </param>
        /// <param name="configure">
        /// Optional <see cref="WebAuthOptions"/> overrides. <see cref="WebAuthOptions.Scheme"/> can ONLY be set here
        /// (it is needed at registration time). This delegate is invoked twice (once on a snapshot at registration,
        /// once by the options system), so it must have no side effects outside the options object.
        /// </param>
        public static AuthenticationBuilder AddTqkWebAuth(this IServiceCollection services,
            Action<CookieAuthenticationOptions> configureCookie, Action<WebAuthOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configureCookie);

            // The scheme is needed now (registration time), the rest is read through IOptions at run time.
            var snapshot = new WebAuthOptions();
            configure?.Invoke(snapshot);
            var optionsBuilder = services.AddOptions<WebAuthOptions>();
            if (configure is not null)
                optionsBuilder.Configure(configure);

            services.AddHttpContextAccessor();
            services.AddMemoryCache();
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<WebSessionPrincipalFactory>();
            services.TryAddSingleton<CurrentUserCache>();
            services.TryAddTransient<SessionBearerHandler>();
            services.TryAddScoped<WebSessionManager>();

            // Outside Development the cookie only travels over HTTPS. Registered BEFORE AddCookie so configureCookie wins.
            services.AddOptions<CookieAuthenticationOptions>(snapshot.Scheme)
                .Configure<IHostEnvironment>((options, env) => options.Cookie.SecurePolicy = env.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always);

            return services
                .AddAuthentication(snapshot.Scheme)
                .AddCookie(snapshot.Scheme, options =>
                {
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.SlidingExpiration = false;
                    configureCookie(options);
                });
        }

        /// <summary>Adds <see cref="SessionBearerHandler"/> to an API client.</summary>
        public static IHttpClientBuilder AddTqkSessionBearer(this IHttpClientBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.TryAddTransient<SessionBearerHandler>();
            return builder.AddHttpMessageHandler<SessionBearerHandler>();
        }
    }
}
