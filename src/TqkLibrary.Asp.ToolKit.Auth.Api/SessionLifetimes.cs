namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>
    /// Sliding lifetimes of a session / refresh token, picked by <see cref="SlidingSessionPolicy"/> with its
    /// <c>persistent</c> flag.
    /// </summary>
    /// <param name="Normal">Normal session: each refresh moves the expiry to now + this.</param>
    /// <param name="Persistent">"Remember me" session; null = same as <paramref name="Normal"/>.</param>
    public sealed record SessionLifetimes(TimeSpan Normal, TimeSpan? Persistent = null)
    {
        /// <summary>The lifetime used for a session with the given <paramref name="persistent"/> flag.</summary>
        public TimeSpan Get(bool persistent) => persistent && Persistent is { } value ? value : Normal;
    }
}
