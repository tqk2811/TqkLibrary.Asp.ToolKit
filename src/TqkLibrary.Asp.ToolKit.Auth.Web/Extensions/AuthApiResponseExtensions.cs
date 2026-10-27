namespace TqkLibrary.Asp.ToolKit.Auth.Web.Extensions
{
    /// <summary>Helpers for <see cref="Interfaces.IAuthApiClient"/> implementations.</summary>
    public static class AuthApiResponseExtensions
    {
        /// <summary>
        /// Throws <see cref="SessionExpiredException"/> when the API rejected the session (HTTP 401 or 403); does
        /// nothing for any other status. Call it on the response of <c>RefreshAsync</c> / <c>GetCurrentUserAsync</c>
        /// before <c>EnsureSuccessStatusCode</c>, so a dead session signs the user out while a 5xx stays a transient
        /// failure.
        /// </summary>
        /// <param name="response">Response of the API.</param>
        /// <param name="message">Optional exception message; null for the default.</param>
        /// <exception cref="SessionExpiredException">The status is 401 or 403.</exception>
        public static void ThrowIfSessionRejected(this HttpResponseMessage response, string? message = null)
        {
            ArgumentNullException.ThrowIfNull(response);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                throw message is null
                    ? new SessionExpiredException()
                    : new SessionExpiredException(message);
            }
        }
    }
}
