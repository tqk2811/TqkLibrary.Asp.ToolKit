using Microsoft.Extensions.Options;

namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>
    /// Computes the sliding session / refresh token expiry: now + <see cref="ApiAuthOptions.RefreshTokenLifetime"/>,
    /// capped by the optional <see cref="ApiAuthOptions.MaxSessionLifetime"/> counted from sign-in. Singleton.
    /// </summary>
    public sealed class SlidingSessionPolicy
    {
        private readonly ApiAuthOptions _options;

        public SlidingSessionPolicy(IOptions<ApiAuthOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _options = options.Value;
        }

        /// <summary>Expiry of a session created at <paramref name="utcNow"/>.</summary>
        public DateTime GetInitialExpiry(DateTime utcNow) => GetNextExpiry(utcNow, utcNow)!.Value;

        /// <summary>
        /// New expiry when refreshing at <paramref name="utcNow"/> a session created at <paramref name="sessionCreatedAtUtc"/>;
        /// null when the absolute cap is reached (the session must end, the user signs in again).
        /// </summary>
        public DateTime? GetNextExpiry(DateTime sessionCreatedAtUtc, DateTime utcNow)
        {
            var next = utcNow.Add(_options.RefreshTokenLifetime);
            if (_options.MaxSessionLifetime is not { } max)
                return next;

            var cap = sessionCreatedAtUtc.Add(max);
            if (cap <= utcNow)
                return null;
            return next > cap ? cap : next;
        }
    }
}
