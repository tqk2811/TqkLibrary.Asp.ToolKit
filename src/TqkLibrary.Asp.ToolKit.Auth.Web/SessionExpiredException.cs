namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// The web session can no longer be used (API revoked the session, refresh token rejected, cookie without tokens).
    /// <see cref="SessionExpiredMiddleware"/> catches it, clears the cookie and sends the user to the login page
    /// (AJAX callers get a 401 JSON body).
    /// </summary>
    public sealed class SessionExpiredException : Exception
    {
        public SessionExpiredException()
            : base("The session has expired.")
        {
        }

        public SessionExpiredException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
