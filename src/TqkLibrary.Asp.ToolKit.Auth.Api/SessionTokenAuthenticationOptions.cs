using Microsoft.AspNetCore.Authentication;
using TqkLibrary.Asp.ToolKit.Auth.Api.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>Scheme options of <see cref="SessionTokenAuthenticationHandler"/>.</summary>
    public sealed class SessionTokenAuthenticationOptions : AuthenticationSchemeOptions
    {
        /// <summary>
        /// Optional hook run on a challenge (401) before the default response. Read
        /// <see cref="SessionTokenChallengeContext.Rejection"/> to write an own body and call
        /// <see cref="SessionTokenChallengeContext.HandleResponse"/> to skip the default (bare 401 with
        /// <c>WWW-Authenticate: Bearer</c>).
        /// </summary>
        public Func<SessionTokenChallengeContext, Task>? OnChallenge { get; set; }
    }
}
