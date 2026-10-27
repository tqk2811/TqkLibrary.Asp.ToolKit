using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TqkLibrary.Asp.ToolKit.Auth.Api.Interfaces;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Extensions
{
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
            services.AddMemoryCache();
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
                });
            return builder.AddJwtBearer(authenticationScheme, options =>
            {
                configure?.Invoke(options);
                options.UseTqkSessionValidation();
            });
        }

        /// <summary>
        /// Chains <see cref="SessionTokenValidator.ValidateAsync"/> after the current <c>OnTokenValidated</c> handler
        /// (skipped when that handler already failed or rejected the token).
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
