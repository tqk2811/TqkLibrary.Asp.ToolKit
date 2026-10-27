using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TqkLibrary.Asp.ToolKit.Caching.Interfaces;
using TqkLibrary.Asp.ToolKit.Caching.Remote;

namespace TqkLibrary.Asp.ToolKit.Caching.Extensions
{
    /// <summary>Service registration of the caching package.</summary>
    public static class CachingServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the system <see cref="TimeProvider"/> (if missing), <see cref="CacheRegistry{TGroup}"/> and
        /// <see cref="ICacheInvalidator{TGroup}"/> as singletons.
        /// </summary>
        /// <typeparam name="TGroup">App-defined enum that names the cache groups.</typeparam>
        public static IServiceCollection AddTqkCaching<TGroup>(this IServiceCollection services) where TGroup : struct, Enum
        {
            ArgumentNullException.ThrowIfNull(services);
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<CacheRegistry<TGroup>>();
            services.TryAddSingleton<ICacheInvalidator<TGroup>, CacheInvalidator<TGroup>>();
            return services;
        }

        /// <summary>
        /// Forwards every local invalidation to another process over HTTP (the receiver uses
        /// <see cref="CacheInvalidationEndpoint{TGroup}"/>). Nothing is sent while
        /// <see cref="CacheInvalidationSenderOptions.TargetUrl"/> or <see cref="CacheInvalidationSenderOptions.Key"/> is empty.
        /// </summary>
        public static IServiceCollection AddTqkCacheInvalidationSender<TGroup>(this IServiceCollection services,
            Action<CacheInvalidationSenderOptions> configure) where TGroup : struct, Enum
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);
            services.AddTqkCaching<TGroup>();
            services.Configure(configure);
            // Calling this method twice must not register the notifier twice (it would be notified twice per invalidation).
            if (!services.Any(d => d.ServiceType == typeof(HttpCacheInvalidationNotifier<TGroup>)))
            {
                services.AddSingleton<HttpCacheInvalidationNotifier<TGroup>>();
                services.AddSingleton<ICacheInvalidationNotifier<TGroup>>(sp => sp.GetRequiredService<HttpCacheInvalidationNotifier<TGroup>>());
            }
            services.AddHttpClient(HttpCacheInvalidationSender<TGroup>.HttpClientName);
            services.AddHostedService<HttpCacheInvalidationSender<TGroup>>();
            return services;
        }

        /// <summary>
        /// Registers the receiving endpoint; map it in the pipeline with
        /// <see cref="CachingApplicationBuilderExtensions.UseTqkCacheInvalidationEndpoint{TGroup}"/>.
        /// </summary>
        public static IServiceCollection AddTqkCacheInvalidationEndpoint<TGroup>(this IServiceCollection services,
            Action<CacheInvalidationEndpointOptions> configure) where TGroup : struct, Enum
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);
            services.AddTqkCaching<TGroup>();
            services.Configure(configure);
            services.TryAddSingleton<CacheInvalidationEndpoint<TGroup>>();
            return services;
        }
    }
}
