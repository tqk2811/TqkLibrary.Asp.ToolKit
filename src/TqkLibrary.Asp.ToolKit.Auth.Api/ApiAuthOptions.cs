namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>Options of the opaque session token API authentication.</summary>
    public sealed class ApiAuthOptions
    {
        /// <summary>Claim type of the user id on the authenticated principal (also set as <c>ClaimTypes.NameIdentifier</c>).</summary>
        public string UserIdClaimType { get; set; } = "sub";

        /// <summary>Claim type of the session id on the authenticated principal.</summary>
        public string SessionIdClaimType { get; set; } = "sid";

        /// <summary>Claim type used for permissions on the authenticated principal.</summary>
        public string PermissionClaimType { get; set; } = "permission";

        /// <summary>
        /// Sliding lifetimes of a session (normal and "remember me"): each touch moves the expiry to last access + the
        /// lifetime picked by <see cref="SlidingSessionPolicy"/>. Default: 30 days for both.
        /// </summary>
        public SessionLifetimes SessionLifetimes { get; set; } = new(TimeSpan.FromDays(30));

        /// <summary>
        /// Optional absolute cap counted from sign-in: sliding stops there and the user must sign in again.
        /// Null (default) = no cap.
        /// </summary>
        public TimeSpan? MaxSessionLifetime { get; set; }

        /// <summary>
        /// Minimum time between two writes of the sliding expiry of one session
        /// (<see cref="Interfaces.ISessionAccessSource.TouchAsync"/>), per process. The stored expiry therefore lags the
        /// real last access by at most this much. Must be shorter than every session lifetime. Zero = every request.
        /// Default 1 hour.
        /// </summary>
        public TimeSpan SessionTouchInterval { get; set; } = TimeSpan.FromHours(1);

        /// <summary>How long a session lookup is cached by <see cref="SessionAccessCache"/>.</summary>
        public TimeSpan SessionAccessCacheLifetime { get; set; } = TimeSpan.FromSeconds(30);

        private static readonly TimeSpan MaxLifetimeLimit = TimeSpan.FromDays(3650);

        /// <summary>Throws when the options are not usable.</summary>
        public void Validate()
        {
            if (SessionLifetimes is null)
                throw new InvalidOperationException("ApiAuthOptions.SessionLifetimes is required.");
            if (SessionLifetimes.Normal <= TimeSpan.Zero)
                throw new InvalidOperationException("ApiAuthOptions.SessionLifetimes.Normal must be positive.");
            if (SessionLifetimes.Persistent is { } persistent && persistent <= TimeSpan.Zero)
                throw new InvalidOperationException("ApiAuthOptions.SessionLifetimes.Persistent must be positive when set.");
            if (MaxSessionLifetime is { } max && max <= TimeSpan.Zero)
                throw new InvalidOperationException("ApiAuthOptions.MaxSessionLifetime must be positive when set.");
            // Expiry arithmetic (now + lifetime) must not overflow DateTime.
            if (SessionLifetimes.Normal > MaxLifetimeLimit)
                throw new InvalidOperationException("ApiAuthOptions.SessionLifetimes.Normal must not exceed 3650 days.");
            if (SessionLifetimes.Persistent is { } persistentMax && persistentMax > MaxLifetimeLimit)
                throw new InvalidOperationException("ApiAuthOptions.SessionLifetimes.Persistent must not exceed 3650 days.");
            if (MaxSessionLifetime is { } maxLimit && maxLimit > MaxLifetimeLimit)
                throw new InvalidOperationException("ApiAuthOptions.MaxSessionLifetime must not exceed 3650 days.");
            if (SessionTouchInterval < TimeSpan.Zero)
                throw new InvalidOperationException("ApiAuthOptions.SessionTouchInterval must not be negative.");
            // A session must be touched before it can expire while in use.
            var shortest = SessionLifetimes.Persistent is { } p && p < SessionLifetimes.Normal ? p : SessionLifetimes.Normal;
            if (SessionTouchInterval >= shortest)
                throw new InvalidOperationException("ApiAuthOptions.SessionTouchInterval must be shorter than every session lifetime.");
            if (string.IsNullOrEmpty(UserIdClaimType) || string.IsNullOrEmpty(SessionIdClaimType) || string.IsNullOrEmpty(PermissionClaimType))
                throw new InvalidOperationException("ApiAuthOptions claim types must not be empty.");
        }
    }
}
