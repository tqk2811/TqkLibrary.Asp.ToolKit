using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TqkLibrary.Asp.ToolKit.Auth.Api.Interfaces;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Extensions
{
    /// <summary>Service registration of the API auth package.</summary>
    public static class ApiAuthServiceCollectionExtensions
    {
        /// <summary>
        /// Registers <see cref="ApiAuthOptions"/> (validated at startup), <see cref="SessionAccessCache"/>,
        /// <see cref="AccessTokenFactory"/> and <see cref="SlidingSessionPolicy"/>. The app must register its
        /// <see cref="ISessionAccessSource"/> and add the bearer scheme with <see cref="AddTqkJwtBearer"/>.
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
            services.TryAddSingleton<AccessTokenFactory>();
            services.TryAddSingleton<SlidingSessionPolicy>();
            return services;
        }

        /// <summary>
        /// Adds a JWT bearer scheme that validates tokens of <see cref="AccessTokenFactory"/> with
        /// <see cref="ApiAuthOptions"/> (<c>MapInboundClaims = false</c>) and runs
        /// <see cref="SessionTokenValidator"/> after any <c>OnTokenValidated</c> the app sets in
        /// <paramref name="configure"/>.
        /// <para>
        /// <paramref name="configure"/> runs AFTER the library set <see cref="JwtBearerOptions.TokenValidationParameters"/>:
        /// assigning a new <c>TokenValidationParameters</c> there replaces the library configuration (signing key,
        /// issuer, audience, clock skew), so adjust the existing instance instead.
        /// </para>
        /// <para>
        /// <see cref="AuthenticationSchemeOptions.EventsType"/> is not supported: the handler then resolves its events from
        /// the container and ignores <see cref="JwtBearerOptions.Events"/>, which would silently skip the session check.
        /// Setting it makes the options throw <see cref="InvalidOperationException"/> on first use; call
        /// <see cref="SessionTokenValidator.ValidateAsync"/> from the app's own events class instead.
        /// </para>
        /// </summary>
        public static AuthenticationBuilder AddTqkJwtBearer(this AuthenticationBuilder builder,
            Action<JwtBearerOptions>? configure = null, string authenticationScheme = JwtBearerDefaults.AuthenticationScheme)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.AddOptions<JwtBearerOptions>(authenticationScheme)
                .Configure<IOptions<ApiAuthOptions>>((options, apiOptions) =>
                {
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters = apiOptions.Value.CreateTokenValidationParameters();
                })
                .PostConfigure(options =>
                {
                    if (options.EventsType is not null)
                        throw new InvalidOperationException(
                            "JwtBearerOptions.EventsType is set, so JwtBearerOptions.Events (where AddTqkJwtBearer registers the "
                            + "session check) would be ignored and revoked sessions would still be accepted. Do not use EventsType "
                            + "with AddTqkJwtBearer; call SessionTokenValidator.ValidateAsync(context) from OnTokenValidated of "
                            + "your own events type (after your own checks) instead.");
                });
            return builder.AddJwtBearer(authenticationScheme, options =>
            {
                configure?.Invoke(options);
                options.UseTqkSessionValidation();
            });
        }

        /// <summary>
        /// Chains <see cref="SessionTokenValidator.ValidateAsync"/> after the current <c>OnTokenValidated</c> handler
        /// (skipped when that handler already failed or rejected the token). It only works with
        /// <see cref="JwtBearerOptions.Events"/>: when <see cref="AuthenticationSchemeOptions.EventsType"/> is used the handler
        /// ignores <c>Events</c>, so call <see cref="SessionTokenValidator.ValidateAsync"/> from that events type.
        /// </summary>
        public static JwtBearerOptions UseTqkSessionValidation(this JwtBearerOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Events ??= new JwtBearerEvents();
            var previous = options.Events.OnTokenValidated;
            options.Events.OnTokenValidated = async context =>
            {
                if (previous is not null)
                    await previous(context).ConfigureAwait(false);
                if (context.Result is not null)
                    return;
                await SessionTokenValidator.ValidateAsync(context).ConfigureAwait(false);
            };
            return options;
        }
    }
}
