using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TqkLibrary.Asp.ToolKit.Auth.Api.Interfaces;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Extensions
{
    /// <summary>Service registration of the API auth package.</summary>
    public static class ApiAuthServiceCollectionExtensions
    {
        /// <summary>
        /// Registers <see cref="ApiAuthOptions"/> (validated at startup), <see cref="SessionAccessCache"/> and
        /// <see cref="SlidingSessionPolicy"/>. The app must register its <see cref="ISessionAccessSource"/> and add the
        /// scheme with <see cref="AddTqkSessionToken"/>.
        /// </summary>
        public static IServiceCollection AddTqkApiAuth(this IServiceCollection services, Action<ApiAuthOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);
            services.AddOptions<ApiAuthOptions>()
                .Configure(configure)
                .Validate(options =>
                {
                    options.Validate();
                    return true;
                })
                .ValidateOnStart();
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<SessionAccessCache>();
            services.TryAddSingleton<SlidingSessionPolicy>();
            services.TryAddSingleton<SessionTouchThrottle>();
            return services;
        }

        /// <summary>
        /// Adds the opaque session token scheme (<see cref="SessionTokenAuthenticationHandler"/>): reads
        /// <c>Authorization: Bearer {sessionId}.{secret}</c>, checks it against <see cref="ISessionAccessSource"/> and
        /// slides the session expiry. Requires <see cref="AddTqkApiAuth"/>.
        /// </summary>
        public static AuthenticationBuilder AddTqkSessionToken(this AuthenticationBuilder builder,
            Action<SessionTokenAuthenticationOptions>? configure = null,
            string authenticationScheme = SessionTokenDefaults.AuthenticationScheme)
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.AddScheme<SessionTokenAuthenticationOptions, SessionTokenAuthenticationHandler>(
                authenticationScheme, configure);
        }
    }
}
