namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>Behaviour of <see cref="CurrentUserMiddleware"/> when the API current-user lookup fails (not an expired session).</summary>
    public enum CurrentUserFailureMode
    {
        /// <summary>The request continues authenticated with the session id only and without any permission (default).</summary>
        SessionOnly,

        /// <summary>The request fails with <see cref="CurrentUserUnavailableException"/> (inner = the original error).</summary>
        Throw,
    }
}
