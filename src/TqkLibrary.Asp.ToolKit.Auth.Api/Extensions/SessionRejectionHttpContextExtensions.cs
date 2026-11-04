using Microsoft.AspNetCore.Http;
using TqkLibrary.Asp.ToolKit.Auth.Api.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Extensions
{
    /// <summary>Reads the <see cref="SessionRejection"/> left by <see cref="SessionTokenAuthenticationHandler"/>.</summary>
    public static class SessionRejectionHttpContextExtensions
    {
        /// <summary>
        /// Why the session token of the request was rejected (reason code from <see cref="Interfaces.ISessionAccessSource"/>
        /// or one of <see cref="SessionRejectionReasons"/> set by the library), or null when it was not rejected (no token,
        /// valid token, or the source threw).
        /// </summary>
        public static SessionRejection? GetSessionRejection(this HttpContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return context.Features.Get<SessionRejection>();
        }

        /// <summary>True when authentication failed because the session token was rejected.</summary>
        public static bool IsSessionRejected(this HttpContext context) => context.GetSessionRejection() is not null;
    }
}
