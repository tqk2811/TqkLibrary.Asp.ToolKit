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
        /// Cookie authentication whose encrypted ticket carries the API session token (see <see cref="WebSessionManager"/>),
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
        /// API slides the session itself; the ticket expires at the <see cref="Models.AuthSession.ExpiresAt"/> given at
        /// sign-in (the session's absolute cap) and is never reissued. A cookie without a session token (issued by an
        /// older version) is rejected and signed out on its first request, BEFORE the app's <c>Events.OnValidatePrincipal</c>:
        /// that handler only runs for cookies that carry a token. The check is chained onto the <c>Events</c> object
        /// present while this delegate runs, so it is NOT applied when the app uses <c>options.EventsType</c> or replaces
        /// <c>Events</c> (or its <c>OnValidatePrincipal</c>) after <c>AddTqkWebAuth</c> (for example in a later
        /// <c>Configure&lt;CookieAuthenticationOptions&gt;</c>); such a cookie is then only rejected when the API is called.
        /// </param>
        /// <param name="configure">
        /// Optional <see cref="WebAuthOptions"/> overrides. <see cref="WebAuthOptions.Scheme"/> can ONLY be set here
        /// (it is needed at registration time). <c>DeviceBound</c> may be set here or in a later
        /// <c>Configure&lt;WebAuthOptions&gt;</c>: it is validated at startup either way. This delegate is invoked twice (once on a snapshot at registration,
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
            // DBSC: always registered, a no-op while disabled, so enabling it anywhere (also in a later
            // Configure<WebAuthOptions>) is validated at startup and requires its API client.
            optionsBuilder
                .Validate<IServiceProviderIsService>((options, isService) =>
                {
                    if (!options.DeviceBound.Enabled)
                        return true;
                    options.DeviceBound.Validate();
                    if (!isService.IsService(typeof(IDeviceKeyApiClient)))
                        throw new InvalidOperationException(
                            "WebAuthOptions.DeviceBound.Enabled requires an IDeviceKeyApiClient registration.");
                    return true;
                })
                .ValidateOnStart();

            services.AddHttpContextAccessor();
            services.AddMemoryCache();
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<WebSessionPrincipalFactory>();
            services.TryAddSingleton<CurrentUserCache>();
            services.TryAddTransient<SessionBearerHandler>();
            services.TryAddScoped<WebSessionManager>();
            services.AddMetrics();
            services.TryAddSingleton<DeviceBoundMetrics>();
            services.TryAddSingleton<DeviceChallengeService>();
            services.TryAddSingleton<DeviceProofCookie>();
            services.TryAddSingleton<DeviceProofJwtVerifier>();
            services.TryAddSingleton<DeviceProofEnforcer>();
            services.TryAddSingleton<DeviceBindingCache>();
            services.TryAddSingleton<DeviceProofFailureHandler>();

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
                    ChainTokenCheck(options);
                });
        }

        /// <summary>
        /// Rejects (and signs out) a ticket without the API session token, e.g. a cookie issued by 1.x, right when the
        /// cookie is validated, whether or not the request calls the API. The app's own
        /// <c>OnValidatePrincipal</c> (if any) runs only for cookies that have a token.
        /// </summary>
        private static void ChainTokenCheck(CookieAuthenticationOptions options)
        {
            var events = options.Events ??= new CookieAuthenticationEvents();
            var inner = events.OnValidatePrincipal;
            events.OnValidatePrincipal = async context =>
            {
                if (string.IsNullOrEmpty(context.Properties.GetTokenValue(WebSessionTicket.SessionTokenName)))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(context.Scheme.Name).ConfigureAwait(false);
                    return;
                }
                await inner(context).ConfigureAwait(false);
            };
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
