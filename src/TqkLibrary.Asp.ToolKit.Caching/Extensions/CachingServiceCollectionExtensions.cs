using Microsoft.Extensions.Configuration;
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
            services.Configure(configure);
            return services.AddTqkCacheInvalidationSenderCore<TGroup>();
        }

        /// <summary>
        /// Same as <see cref="AddTqkCacheInvalidationSender{TGroup}(IServiceCollection, Action{CacheInvalidationSenderOptions})"/>
        /// but binds <see cref="CacheInvalidationSenderOptions"/> from <paramref name="section"/> (keys are the option
        /// property names: <c>TargetUrl</c>, <c>Key</c>, <c>KeyHeader</c>, <c>Debounce</c>, ...). The binding is
        /// reloaded when the configuration changes, so the per-batch values (URL, key, timeout, retries) follow it;
        /// whether sending is enabled is still decided once at startup.
        /// </summary>
        public static IServiceCollection AddTqkCacheInvalidationSender<TGroup>(this IServiceCollection services,
            IConfiguration section) where TGroup : struct, Enum
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(section);
            // Bind registers a ConfigurationChangeTokenSource, so IOptionsMonitor sees configuration reloads.
            services.AddOptions<CacheInvalidationSenderOptions>().Bind(section);
            return services.AddTqkCacheInvalidationSenderCore<TGroup>();
        }

        private static IServiceCollection AddTqkCacheInvalidationSenderCore<TGroup>(this IServiceCollection services)
            where TGroup : struct, Enum
        {
            services.AddTqkCaching<TGroup>();
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
            services.Configure(configure);
            return services.AddTqkCacheInvalidationEndpointCore<TGroup>();
        }

        /// <summary>
        /// Same as <see cref="AddTqkCacheInvalidationEndpoint{TGroup}(IServiceCollection, Action{CacheInvalidationEndpointOptions})"/>
        /// but binds <see cref="CacheInvalidationEndpointOptions"/> from <paramref name="section"/> (keys are the option
        /// property names: <c>Path</c>, <c>KeyHeader</c>, <c>Key</c>, <c>RequireLoopback</c>, ...), reloaded when the
        /// configuration changes.
        /// </summary>
        public static IServiceCollection AddTqkCacheInvalidationEndpoint<TGroup>(this IServiceCollection services,
            IConfiguration section) where TGroup : struct, Enum
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(section);
            services.AddOptions<CacheInvalidationEndpointOptions>().Bind(section);
            return services.AddTqkCacheInvalidationEndpointCore<TGroup>();
        }

        private static IServiceCollection AddTqkCacheInvalidationEndpointCore<TGroup>(this IServiceCollection services)
            where TGroup : struct, Enum
        {
            services.AddTqkCaching<TGroup>();
            services.TryAddSingleton<CacheInvalidationEndpoint<TGroup>>();
            return services;
        }
    }
}
