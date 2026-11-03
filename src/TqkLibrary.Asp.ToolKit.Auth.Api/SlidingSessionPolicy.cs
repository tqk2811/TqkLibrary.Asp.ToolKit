using Microsoft.Extensions.Options;

namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>
    /// Computes the sliding session / refresh token expiry: now + the lifetime of <see cref="ApiAuthOptions.SessionLifetimes"/>
    /// picked by the <c>persistent</c> ("remember me") flag, capped by the optional
    /// <see cref="ApiAuthOptions.MaxSessionLifetime"/> counted from sign-in (for both kinds). Singleton.
    /// <para>
    /// Not sealed: an app can subclass it and override the methods. <c>AddTqkApiAuth</c> registers it with
    /// <c>TryAddSingleton</c>: register your subclass BEFORE <c>AddTqkApiAuth</c> as
    /// <c>services.AddSingleton&lt;SlidingSessionPolicy, MyPolicy&gt;()</c> and it wins, or replace the registration
    /// afterwards (<c>services.Replace(...)</c>).
    /// </para>
    /// </summary>
    public class SlidingSessionPolicy
    {
        /// <summary>Validated options.</summary>
        protected ApiAuthOptions Options { get; }

        public SlidingSessionPolicy(IOptions<ApiAuthOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);
            Options = options.Value;
        }

        /// <summary>Expiry of a session created at <paramref name="utcNow"/>.</summary>
        public virtual DateTime GetInitialExpiry(DateTime utcNow, bool persistent = false)
            => GetNextExpiry(utcNow, utcNow, persistent)
               ?? throw new InvalidOperationException("GetNextExpiry returned null for a new session.");

        /// <summary>
        /// New expiry when refreshing at <paramref name="utcNow"/> a session created at <paramref name="createdAtUtc"/>;
        /// null when the absolute cap is reached (the session must end, the user signs in again).
        /// </summary>
        public virtual DateTime? GetNextExpiry(DateTime createdAtUtc, DateTime utcNow, bool persistent = false)
        {
            var next = utcNow.Add(Options.SessionLifetimes.Get(persistent));
            if (Options.MaxSessionLifetime is not { } max)
                return next;

            var cap = createdAtUtc.Add(max);
            if (cap <= utcNow)
                return null;
            return next > cap ? cap : next;
        }
    }
}
