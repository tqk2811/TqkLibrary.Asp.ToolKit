namespace TqkLibrary.Asp.ToolKit.Auth.Web.Extensions
{
    /// <summary>Helpers for <see cref="Interfaces.IAuthApiClient"/> implementations.</summary>
    public static class AuthApiResponseExtensions
    {
        /// <summary>
        /// Throws <see cref="SessionExpiredException"/> when the <c>me</c> call answered HTTP 401. <c>me</c> has no
        /// business 401, so ANY 401 (with or without a <c>WWW-Authenticate</c> header) means the session ended: a proxy
        /// may strip the header, and without this the user would stay signed in without permissions forever. A 403 and
        /// every other status do nothing (a proxy/WAF 403 must not sign users out).
        /// Call it on the response of <c>GetCurrentUserAsync</c> before <c>EnsureSuccessStatusCode</c>, so a dead
        /// session signs the user out while a 5xx stays a transient failure. Do not use it for other API calls: there
        /// only a 401 with a Bearer challenge ends the session (see <see cref="SessionBearerHandler"/>).
        /// </summary>
        /// <param name="response">Response of the <c>me</c> call.</param>
        /// <param name="message">Optional exception message; null for the default.</param>
        /// <exception cref="SessionExpiredException">The status is 401.</exception>
        public static void ThrowIfSessionRejected(this HttpResponseMessage response, string? message = null)
        {
            ArgumentNullException.ThrowIfNull(response);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw message is null
                    ? new SessionExpiredException()
                    : new SessionExpiredException(message);
            }
        }
    }
}
