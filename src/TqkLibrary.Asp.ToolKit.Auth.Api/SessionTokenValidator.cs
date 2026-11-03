using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using TqkLibrary.Asp.ToolKit.Auth.Api.Interfaces;
using TqkLibrary.Asp.ToolKit.Auth.Api.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>
    /// Body of <see cref="JwtBearerEvents.OnTokenValidated"/>: checks on EVERY request that the token's session is still
    /// alive (through <see cref="SessionAccessCache"/> and the app's <see cref="ISessionAccessSource"/>) and REPLACES
    /// name, email, role and permission claims with the current data. Revocation, role and permission changes therefore
    /// apply on the next request instead of when the token expires.
    /// <para>
    /// A token without session id/user id, or with a dead session, fails authentication (401) and the request is
    /// marked so <see cref="IsSessionRejected"/> / <see cref="GetSessionRejection"/> let a custom <c>OnChallenge</c>
    /// write a specific error body (the web reads it to sign out instead of retrying). The rejection, with the reason
    /// returned by <see cref="SessionAccessResult.Reject"/>, is stored as the <see cref="SessionRejection"/> feature of
    /// <see cref="HttpContext.Features"/>.
    /// </para>
    /// </summary>
    public static class SessionTokenValidator
    {
        /// <summary>True when authentication failed because the token's session is missing, revoked or expired.</summary>
        public static bool IsSessionRejected(HttpContext context) => GetSessionRejection(context) is not null;

        /// <summary>
        /// Why the session of the request was rejected (reason code from <see cref="ISessionAccessSource"/> or
        /// <see cref="SessionRejectionReasons.NoSession"/> / <see cref="SessionRejectionReasons.UserMismatch"/>), or null
        /// when it was not rejected. Use it in <c>JwtBearerEvents.OnChallenge</c> to write the 401 body.
        /// </summary>
        public static SessionRejection? GetSessionRejection(HttpContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return context.Features.Get<SessionRejection>();
        }

        /// <summary>Validates the session of <paramref name="context"/> and rebuilds its principal.</summary>
        public static async Task ValidateAsync(TokenValidatedContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var services = context.HttpContext.RequestServices;
            var options = services.GetRequiredService<IOptions<ApiAuthOptions>>().Value;

            var principal = context.Principal;
            var sessionId = principal?.FindFirst(options.SessionIdClaimType)?.Value;
            var userId = principal?.FindFirst(options.UserIdClaimType)?.Value
                ?? principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (principal is null || string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(userId))
            {
                // Correctly signed but carries no session: not issued by this scheme.
                Reject(context, SessionRejectionReasons.NoSession, "The token carries no session.");
                return;
            }

            var source = services.GetRequiredService<ISessionAccessSource>();
            var result = await services.GetRequiredService<SessionAccessCache>()
                .GetAsync(sessionId, userId, ct => source.GetAsync(sessionId, userId, ct), context.HttpContext.RequestAborted)
                .ConfigureAwait(false);
            if (!result.IsGranted)
            {
                Reject(context, result.Reason, "The session was revoked or has expired"
                    + (result.Reason is null ? "." : " (" + result.Reason + ")."));
                return;
            }
            var access = result.Access;

            // Keep the identity claims of the token, REPLACE name/email/roles/permissions with current data.
            var replaced = new HashSet<string>(StringComparer.Ordinal)
            {
                ClaimTypes.Name, ClaimTypes.Email, ClaimTypes.Role, ClaimTypes.NameIdentifier, options.PermissionClaimType,
            };
            var claims = principal.Claims.Where(c => !replaced.Contains(c.Type)).Select(c => new Claim(c.Type, c.Value, c.ValueType, c.Issuer)).ToList();
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
            claims.Add(new Claim(ClaimTypes.Name, access.UserName));
            if (!string.IsNullOrEmpty(access.Email))
                claims.Add(new Claim(ClaimTypes.Email, access.Email));
            claims.AddRange(access.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
            claims.AddRange(access.Permissions.Select(permission => new Claim(options.PermissionClaimType, permission)));

            context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, context.Scheme.Name, ClaimTypes.Name, ClaimTypes.Role));
        }

        private static void Reject(TokenValidatedContext context, string? reason, string message)
        {
            context.HttpContext.Features.Set(new SessionRejection(reason, message));
            context.Fail(message);
        }
    }
}
