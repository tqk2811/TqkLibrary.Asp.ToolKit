using TqkLibrary.Asp.ToolKit.Integration.HttpClients.Implements;
using TqkLibrary.Asp.ToolKit.Integration.HttpClients.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;

namespace TqkLibrary.Asp.ToolKit.Integration.HttpClients
{
    public static class HttpClientExtensions
    {
        public static IServiceCollection AddApiHttpClientFactory(this IServiceCollection services)
        {
            services.AddSingleton(typeof(IHttpClientFactory<>), typeof(HttpClientFactory<>));
            services.AddSingleton(typeof(IHttpMessageHandlerFactory<>), typeof(HttpMessageHandlerFactory<>));
            // Primary handler must NOT manage cookies or follow redirects: it is pooled across sessions
            // by IHttpClientFactory, so a shared CookieContainer would mix cookies from different
            // accounts, and auto-followed redirects would hide Set-Cookie from the CookieHandler.
            // TqkLibrary's CookieHandler + RedirectHandler in BaseSiteImplement take over both jobs.
            services.ConfigureHttpClientDefaults(builder =>
            {
                builder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    UseCookies = false,
                    AllowAutoRedirect = false,
                });
            });
            return services;
        }

        public static IHttpClientBuilder AddApiHttpClient<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>(
            this IServiceCollection services,
            Action<IServiceProvider, HttpClient>? configureClient = null,
            Func<IServiceProvider, TService>? implementationFactory = null
            )
            where TService : class
        {
            if (implementationFactory is not null) services.AddTransient<TService>(implementationFactory);
            else services.AddTransient<TService>();
            if (configureClient is not null) return services.AddHttpClient(typeof(TService).FullName!, configureClient);
            else return services.AddHttpClient(typeof(TService).FullName!);
        }
        public static IHttpClientBuilder AddApiHttpClient<TService, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(
            this IServiceCollection services,
            Action<IServiceProvider, HttpClient>? configureClient = null,
            Func<IServiceProvider, TImplementation>? implementationFactory = null
            )
            where TService : class
            where TImplementation : class, TService
        {
            if (implementationFactory is not null) services.AddTransient<TService, TImplementation>(implementationFactory);
            else services.AddTransient<TService, TImplementation>();
            if (configureClient is not null) return services.AddHttpClient(typeof(TImplementation).FullName!, configureClient);
            else return services.AddHttpClient(typeof(TImplementation).FullName!);
        }
    }
}
