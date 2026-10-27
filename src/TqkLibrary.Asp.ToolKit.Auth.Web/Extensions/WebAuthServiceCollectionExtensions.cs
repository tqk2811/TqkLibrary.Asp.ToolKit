using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TqkLibrary.Asp.ToolKit.Auth.Web.Interfaces;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Extensions
{
    public static class WebAuthServiceCollectionExtensions
    {
        /// <summary>
        /// Cookie authentication whose encrypted ticket carries the API tokens (see <see cref="WebSessionManager"/>),
        /// plus the services of this package. The app must also register its <see cref="IAuthApiClient"/>
        /// implementation, add <see cref="AddTqkSessionBearer"/> to its API client, and call
        /// <c>UseAuthentication()</c> → <see cref="WebAuthApplicationBuilderExtensions.UseTqkWebSession"/> →
        /// <c>UseAuthorization()</c>.
        /// <para>Nothing is stored server side per session; with several instances only the Data Protection key ring
        /// must be shared (<see cref="AddTqkDataProtectionKeyRing"/>).</para>
        /// </summary>
        /// <param name="services">Services.</param>
        /// <param name="configureCookie">Cookie options of the app (name, login path, expiry...).</param>
        /// <param name="configure">Optional <see cref="WebAuthOptions"/> overrides.</param>
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
                    configureCookie(options);
                });
        }

        /// <summary>
        /// Persists the Data Protection key ring (it encrypts the cookie and therefore the API tokens) so sessions
        /// survive a restart. Default directory: <c>&lt;ContentRoot&gt;/../Data/&lt;ApplicationName&gt;/keys</c>, outside the
        /// publish folder so a redeploy does not wipe it. Losing the key ring invalidates every cookie (users sign in
        /// again). Several instances must point to the same directory and application name.
        /// </summary>
        /// <param name="services">Services.</param>
        /// <param name="environment">Host environment (content root, application name).</param>
        /// <param name="keysDirectory">Absolute or content-root relative directory; null for the default.</param>
        /// <param name="applicationName">Fixed application name; null for <see cref="IHostEnvironment.ApplicationName"/>.</param>
        public static IDataProtectionBuilder AddTqkDataProtectionKeyRing(this IServiceCollection services, IHostEnvironment environment,
            string? keysDirectory = null, string? applicationName = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(environment);
            var name = string.IsNullOrWhiteSpace(applicationName) ? environment.ApplicationName : applicationName;
            var directory = string.IsNullOrWhiteSpace(keysDirectory)
                ? Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "Data", name, "keys"))
                : Path.GetFullPath(keysDirectory, environment.ContentRootPath);

            return services.AddDataProtection()
                .SetApplicationName(name)
                .PersistKeysToFileSystem(new DirectoryInfo(directory));
        }

        /// <summary>Adds <see cref="SessionBearerHandler"/> to an API client.</summary>
        public static IHttpClientBuilder AddTqkSessionBearer(this IHttpClientBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.TryAddTransient<SessionBearerHandler>();
            return builder.AddHttpMessageHandler<SessionBearerHandler>();
        }

        /// <summary>Adds <see cref="ClientIpForwardingHandler"/> (viewer IP → <c>X-Forwarded-For</c>) to an API client.</summary>
        public static IHttpClientBuilder AddTqkClientIpForwarding(this IHttpClientBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.AddHttpContextAccessor();
            builder.Services.TryAddTransient<ClientIpForwardingHandler>();
            return builder.AddHttpMessageHandler<ClientIpForwardingHandler>();
        }
    }
}
