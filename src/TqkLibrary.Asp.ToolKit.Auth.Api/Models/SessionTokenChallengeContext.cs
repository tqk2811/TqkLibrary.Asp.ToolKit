using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Models
{
    /// <summary>Argument of <see cref="SessionTokenAuthenticationOptions.OnChallenge"/>.</summary>
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

        /// <summary>True after <see cref="HandleResponse"/>.</summary>
        public bool Handled { get; private set; }

        /// <summary>The hook wrote the response itself: the default 401 is skipped.</summary>
        public void HandleResponse() => Handled = true;
    }
}
