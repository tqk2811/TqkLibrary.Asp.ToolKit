using Microsoft.Extensions.Options;

namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>
    /// Computes the sliding session expiry: last access + the lifetime of <see cref="ApiAuthOptions.SessionLifetimes"/>
    /// picked by the <c>persistent</c> ("remember me") flag, capped by the optional
    /// <see cref="ApiAuthOptions.MaxSessionLifetime"/> counted from sign-in (for both kinds). Used by the app at sign-in
    /// (<see cref="GetInitialExpiry"/>) and by <see cref="SessionTokenAuthenticationHandler"/> on every request. Singleton.
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
        /// New expiry for an access at <paramref name="utcNow"/> of a session created at <paramref name="createdAtUtc"/>;
        /// null when the absolute cap is reached (the session must end, the user signs in again).
        /// </summary>
        public virtual DateTime? GetNextExpiry(DateTime createdAtUtc, DateTime utcNow, bool persistent = false)
        {
            var next = utcNow.Add(Options.SessionLifetimes.Get(persistent));
            if (GetAbsoluteExpiry(createdAtUtc) is not { } cap)
                return next;
            if (cap <= utcNow)
                return null;
            return next > cap ? cap : next;
        }

        /// <summary>
        /// The absolute end of a session created at <paramref name="createdAtUtc"/> (<see cref="ApiAuthOptions.MaxSessionLifetime"/>),
        /// or null without a cap. Give it to the web at sign-in as the cookie expiry: the cookie never outlives the
        /// session, and until then the API alone decides whether the session is still alive.
        /// </summary>
        public virtual DateTime? GetAbsoluteExpiry(DateTime createdAtUtc)
            => Options.MaxSessionLifetime is { } max ? createdAtUtc.Add(max) : null;
    }
}
