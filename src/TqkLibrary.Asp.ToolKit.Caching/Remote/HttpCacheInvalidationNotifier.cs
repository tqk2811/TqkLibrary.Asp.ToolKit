using Microsoft.Extensions.Options;
using System.Threading.Channels;
using TqkLibrary.Asp.ToolKit.Caching.Interfaces;

namespace TqkLibrary.Asp.ToolKit.Caching.Remote
{
    /// <summary>
    /// Singleton notifier: only pushes invalidated groups into a bounded channel (no IO, never throws) for
    /// <see cref="HttpCacheInvalidationSender{TGroup}"/> to send in the background. When the channel is full the
    /// oldest group is dropped: the remote side still expires it by TTL.
    /// </summary>
    /// <typeparam name="TGroup">App-defined enum that names the cache groups.</typeparam>
    public sealed class HttpCacheInvalidationNotifier<TGroup> : ICacheInvalidationNotifier<TGroup> where TGroup : struct, Enum
    {
        private readonly Channel<TGroup> _channel;

        /// <summary>Creates the notifier; the queue capacity is read once from <paramref name="options"/>.</summary>
        /// <param name="options">Sender options (<see cref="CacheInvalidationSenderOptions.QueueCapacity"/>).</param>
        public HttpCacheInvalidationNotifier(IOptions<CacheInvalidationSenderOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _channel = Channel.CreateBounded<TGroup>(new BoundedChannelOptions(Math.Max(1, options.Value.QueueCapacity))
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
        }

        /// <summary>Reader for the sender (single reader).</summary>
        public ChannelReader<TGroup> Reader => _channel.Reader;

        /// <inheritdoc />
        public void OnInvalidated(IReadOnlyList<TGroup> groups)
        {
            try
            {
                foreach (var group in groups)
                    _channel.Writer.TryWrite(group);
            }
            catch
            {
                // Contract of ICacheInvalidationNotifier: never throw into the writing request.
            }
        }
    }
}
