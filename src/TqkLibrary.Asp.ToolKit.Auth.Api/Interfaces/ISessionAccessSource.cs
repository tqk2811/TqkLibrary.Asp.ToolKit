using TqkLibrary.Asp.ToolKit.Auth.Api.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Interfaces
{
    /// <summary>
    /// App-provided lookup run (through <see cref="SessionAccessCache"/>) for every authenticated request. Resolved from
    /// the request services, so it may be scoped (DbContext).
    /// </summary>
    public interface ISessionAccessSource
    {
        /// <summary>
        /// <see cref="SessionAccessResult.Grant"/> (or a <see cref="SessionAccessRecord"/>, converted implicitly) with the
        /// current access data of <paramref name="userId"/> when session <paramref name="sessionId"/> belongs to that
        /// user, is not revoked, not expired and the account is still usable; otherwise
        /// <see cref="SessionAccessResult.Reject"/> with an optional reason (request gets 401; the reason is readable in
        /// <c>OnChallenge</c> through <see cref="SessionTokenValidator.GetSessionRejection"/>). Rejections are never cached.
        /// </summary>
        Task<SessionAccessResult> GetAsync(string sessionId, string userId, CancellationToken cancellationToken);
    }
}
