using Microsoft.AspNetCore.Authentication;
using TqkLibrary.Asp.ToolKit.Auth.Api.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>Scheme options of <see cref="SessionTokenAuthenticationHandler"/>.</summary>
    public sealed class SessionTokenAuthenticationOptions : AuthenticationSchemeOptions
    {
        /// <summary>
        /// Optional hook run on a challenge. The status (401) and the <c>WWW-Authenticate: Bearer</c> header are ALREADY
        /// set when it runs. Read <see cref="SessionTokenChallengeContext.Rejection"/> to write an own body.
        /// The hook may change the body but MUST NOT remove the <c>WWW-Authenticate</c> header: the web side
        /// (<c>SessionBearerHandler</c>) relies on it to tell an ended session from a business 401.
        /// </summary>
        public Func<SessionTokenChallengeContext, Task>? OnChallenge { get; set; }
    }
}
