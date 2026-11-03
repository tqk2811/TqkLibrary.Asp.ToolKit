namespace TqkLibrary.Asp.ToolKit.Caching.Remote
{
    /// <summary>Options of <see cref="CacheInvalidationEndpoint{TGroup}"/> (the receiving side).</summary>
    public sealed class CacheInvalidationEndpointOptions
    {
        /// <summary>Request path of the endpoint.</summary>
        public string Path { get; set; } = "/_internal/cache/invalidate";

        /// <summary>Header carrying the shared key.</summary>
        public string KeyHeader { get; set; } = "X-Cache-Key";

        /// <summary>Shared key. Empty disables the endpoint completely (it answers 404 like it does not exist).</summary>
        public string? Key { get; set; }

        /// <summary>
        /// Only accept direct loopback connections without any proxy forwarding header (default true). Leave it on
        /// unless the sender runs on another machine and the network path is otherwise protected.
        /// </summary>
        public bool RequireLoopback { get; set; } = true;

        /// <summary>Largest accepted request body, in bytes.</summary>
        public int MaxBodyBytes { get; set; } = 4096;

        /// <summary>
        /// Headers whose presence means the request came through a proxy/CDN (i.e. from outside).
        /// <see langword="null"/> (the default) means <see cref="DefaultForwardingHeaders"/>; the default lives outside
        /// the property because the configuration binder APPENDS to any non-null collection, so a configured list would
        /// otherwise only extend the defaults. A configured list replaces them.
        /// </summary>
        public string[]? ForwardingHeaders { get; set; }

        /// <summary>Headers used when <see cref="ForwardingHeaders"/> is <see langword="null"/>.</summary>
        public static IReadOnlyList<string> DefaultForwardingHeaders { get; } =
        [
            "X-Forwarded-For", "Forwarded", "X-Real-IP", "CF-Connecting-IP", "X-Original-For",
        ];

        /// <summary><see cref="ForwardingHeaders"/> when set, otherwise <see cref="DefaultForwardingHeaders"/>.</summary>
        public IReadOnlyList<string> EffectiveForwardingHeaders => ForwardingHeaders ?? DefaultForwardingHeaders;
    }
}
