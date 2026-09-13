using TqkLibrary.Asp.ToolKit.Integration.HttpClients.Implements;
using TqkLibrary.Asp.ToolKit.Integration.HttpClients.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;

namespace TqkLibrary.Asp.ToolKit.Integration.HttpClients
{
    public static class HttpClientExtensions
    {
        /// <summary>
        /// Registers the per-type client/handler factories and the primary-handler defaults that
        /// every client added through <see cref="AddApiHttpClient{TService}"/> inherits.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The primary handler MUST NOT manage cookies or follow redirects. <c>IHttpClientFactory</c>
        /// pools and reuses the primary handler per client name, so anything stored on it is shared
        /// by the whole process rather than scoped to a request or a user. A <c>CookieContainer</c>
        /// placed there becomes one cookie jar for every caller: on a multi-user server the last
        /// account to sign in overwrites the session cookies of everyone else, and two users end up
        /// swapping identities. Auto-followed redirects are disabled for a related reason — they
        /// consume <c>Set-Cookie</c> before any <c>DelegatingHandler</c> in the chain can see it.
        /// </para>
        /// <para>
        /// These defaults are applied via <c>ConfigureHttpClientDefaults</c>, which means they are
        /// only DEFAULTS: a later per-client <c>ConfigurePrimaryHttpMessageHandler</c> silently
        /// overrides them, with no warning and no build error. Do not treat this call as a guard
        /// rail — if you need cookies or redirects, add them as per-instance handlers
        /// (<see cref="T:TqkLibrary.Http.HttpClientHandles.CookieHandler"/> and
        /// <see cref="T:TqkLibrary.Http.HttpClientHandles.RedirectHandler"/>, in that nesting order)
        /// rather than re-enabling them on the pooled primary handler. Note that handlers added with
        /// <c>AddHttpMessageHandler</c> are pooled along with the primary handler, so they cannot
        /// isolate state per user either.
        /// </para>
        /// </remarks>
        public static IServiceCollection AddApiHttpClientFactory(this IServiceCollection services)
        {
            services.AddSingleton(typeof(IHttpClientFactory<>), typeof(HttpClientFactory<>));
            services.AddSingleton(typeof(IHttpMessageHandlerFactory<>), typeof(HttpMessageHandlerFactory<>));
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

        /// <summary>
        /// Registers <typeparamref name="TService"/> as a transient service plus the named
        /// <see cref="HttpClient"/> backing it.
        /// </summary>
        /// <remarks>
        /// The named client is registered under <c>typeof(TService).FullName</c>. Resolve it with
        /// <c>IHttpClientFactory&lt;TService&gt;</c> — see the naming note on
        /// <see cref="AddApiHttpClient{TService, TImplementation}"/>.
        /// </remarks>
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

        /// <summary>
        /// Registers <typeparamref name="TService"/> as a transient service implemented by
        /// <typeparamref name="TImplementation"/>, plus the named <see cref="HttpClient"/> backing it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// NAMING: the named client is always registered under the CONCRETE type —
        /// <c>typeof(TImplementation).FullName</c>, not the service interface. Both overloads follow
        /// the same rule (in the single-parameter overload <typeparamref name="TService"/> IS the
        /// concrete type), and <c>HttpClientFactory&lt;T&gt;.CreateClient()</c> looks the client up
        /// by <c>typeof(T).FullName</c>. So <typeparamref name="TImplementation"/> must take
        /// <c>IHttpClientFactory&lt;TImplementation&gt;</c> in its constructor, NOT
        /// <c>IHttpClientFactory&lt;TService&gt;</c>.
        /// </para>
        /// <para>
        /// Getting that wrong fails SILENTLY: <c>IHttpClientFactory</c> hands back a default client
        /// for any unregistered name, so there is no startup error and no exception — just a client
        /// with no <c>BaseAddress</c> and none of the configuration applied here. It usually
        /// surfaces much later as a malformed request URI, or as the default 100-second timeout
        /// cutting off a call that was meant to run long.
        /// </para>
        /// </remarks>
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
