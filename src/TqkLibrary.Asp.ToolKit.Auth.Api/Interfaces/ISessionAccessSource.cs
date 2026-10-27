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
        /// The current access data of <paramref name="userId"/> when session <paramref name="sessionId"/> belongs to
        /// that user, is not revoked, not expired and the account is still usable; otherwise null (request gets 401).
        /// </summary>
        Task<SessionAccessRecord?> GetAsync(string sessionId, string userId, CancellationToken cancellationToken);
    }
}
