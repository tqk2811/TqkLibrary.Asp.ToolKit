using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Models
{
    /// <summary>
    /// Argument of <see cref="SessionTokenAuthenticationOptions.OnChallenge"/>. The response status (401) and the
    /// <c>WWW-Authenticate: Bearer</c> header are already set: the hook may write a body but MUST NOT remove that
    /// header, because the web side relies on it to tell an ended session from a business 401.
    /// </summary>
    public sealed class SessionTokenChallengeContext
    {
        internal SessionTokenChallengeContext(HttpContext httpContext, SessionRejection? rejection, AuthenticationProperties properties)
        {
            HttpContext = httpContext;
            Rejection = rejection;
            Properties = properties;
        }

        /// <summary>The request being challenged.</summary>
        public HttpContext HttpContext { get; }

        /// <summary>Why the session token was rejected; null when the request carried no session token at all.</summary>
        public SessionRejection? Rejection { get; }

        /// <summary>Properties of the challenge.</summary>
        public AuthenticationProperties Properties { get; }

        /// <summary>
        /// True after <see cref="HandleResponse"/>. Kept for source compatibility only: the status (401) and the
        /// <c>WWW-Authenticate</c> header are always set BEFORE the hook runs, so this no longer skips anything.
        /// </summary>
        public bool Handled { get; private set; }

        /// <summary>
        /// Marks that the hook wrote the response body itself. Kept for source compatibility only: it no longer skips
        /// anything (status 401 and <c>WWW-Authenticate</c> are always set before the hook).
        /// </summary>
        public void HandleResponse() => Handled = true;
    }
}
