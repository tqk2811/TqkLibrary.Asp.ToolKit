namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Thrown by <see cref="CurrentUserMiddleware"/> in <see cref="CurrentUserFailureMode.Throw"/> mode when the current
    /// user could not be loaded from the API. <see cref="Exception.InnerException"/> is the original error, or null when
    /// the failure was remembered from an earlier request (<see cref="WebAuthOptions.CurrentUserFailureCacheFor"/>).
    /// </summary>
    public sealed class CurrentUserUnavailableException : Exception
    {
        public CurrentUserUnavailableException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
