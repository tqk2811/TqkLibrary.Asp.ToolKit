namespace TqkLibrary.Asp.ToolKit.Auth.Web.Extensions
{
    /// <summary>Helpers for <see cref="Interfaces.IAuthApiClient"/> implementations.</summary>
    public static class AuthApiResponseExtensions
    {
        /// <summary>
        /// Throws <see cref="SessionExpiredException"/> when the API rejected the session: HTTP 401 carrying a
        /// <c>WWW-Authenticate: Bearer</c> challenge (what Auth.Api sends for a dead session). Does nothing for any
        /// other status, including a 401 without the challenge and a 403 (a proxy/WAF 403 must not sign users out).
        /// Call it on the response of <c>GetCurrentUserAsync</c> before <c>EnsureSuccessStatusCode</c>, so a dead
        /// session signs the user out while a 5xx stays a transient failure.
        /// </summary>
        /// <param name="response">Response of the API.</param>
        /// <param name="message">Optional exception message; null for the default.</param>
        /// <param name="treatAnyUnauthorizedAsSessionExpired">True for APIs that send no challenge header: any 401
        /// counts (mirror of <see cref="WebAuthOptions.TreatAnyUnauthorizedAsSessionExpired"/>).</param>
        /// <exception cref="SessionExpiredException">The status is 401 with a Bearer challenge.</exception>
        public static void ThrowIfSessionRejected(this HttpResponseMessage response, string? message = null,
            bool treatAnyUnauthorizedAsSessionExpired = false)
        {
            ArgumentNullException.ThrowIfNull(response);
            if (SessionChallenge.EndsSession(response, treatAnyUnauthorizedAsSessionExpired))
            {
                throw message is null
                    ? new SessionExpiredException()
                    : new SessionExpiredException(message);
            }
        }
    }
}
