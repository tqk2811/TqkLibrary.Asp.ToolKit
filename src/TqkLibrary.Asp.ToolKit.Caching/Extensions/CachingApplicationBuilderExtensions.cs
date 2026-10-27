using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using TqkLibrary.Asp.ToolKit.Caching.Remote;

namespace TqkLibrary.Asp.ToolKit.Caching.Extensions
{
    public static class CachingApplicationBuilderExtensions
    {
        /// <summary>
        /// Short-circuits requests to <see cref="CacheInvalidationEndpointOptions.Path"/> into
        /// <see cref="CacheInvalidationEndpoint{TGroup}"/>. Place it BEFORE <c>UseForwardedHeaders</c>,
        /// <c>UseHttpsRedirection</c> and status code pages. Requires
        /// <see cref="CachingServiceCollectionExtensions.AddTqkCacheInvalidationEndpoint{TGroup}"/>.
        /// </summary>
        public static IApplicationBuilder UseTqkCacheInvalidationEndpoint<TGroup>(this IApplicationBuilder app) where TGroup : struct, Enum
        {
            ArgumentNullException.ThrowIfNull(app);
            var endpoint = app.ApplicationServices.GetRequiredService<CacheInvalidationEndpoint<TGroup>>();
            return app.Use(async (context, next) =>
            {
                if (!endpoint.Matches(context))
                {
                    await next(context).ConfigureAwait(false);
                    return;
                }
                await endpoint.HandleAsync(context).ConfigureAwait(false);
            });
        }
    }
}
