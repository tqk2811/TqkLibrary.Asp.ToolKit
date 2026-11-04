using TqkLibrary.Asp.ToolKit.Auth.Web.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Interfaces
{
    /// <summary>
    /// The app's bridge to its API. Implement it on top of the app's own HTTP client.
    /// <para><b>Error contract</b> (the library relies on it to decide between "sign out" and "try again later"):</para>
    /// <list type="bullet">
    ///   <item>When the API rejects <c>me</c> (HTTP 401 or 403) the implementation MUST throw
    ///   <see cref="SessionExpiredException"/>. <see cref="Extensions.AuthApiResponseExtensions.ThrowIfSessionRejected"/>
    ///   does exactly that for an <see cref="HttpResponseMessage"/>.</item>
    ///   <item>Every other exception (5xx, network, timeout, deserialization) is treated as a transient failure
    ///   and the user is NOT signed out. So never map a transient error to <see cref="SessionExpiredException"/>,
    ///   and never swallow a 401/403 into another exception type.</item>
    /// </list>
    /// </summary>
    public interface IAuthApiClient
    {
        /// <summary>
        /// The current user of the session. Called through the app's client with <see cref="SessionBearerHandler"/>
        /// attached, which adds the session token as bearer (a 401 there already throws <see cref="SessionExpiredException"/>).
        /// </summary>
        Task<CurrentUserInfo> GetCurrentUserAsync(CancellationToken cancellationToken);

        /// <summary>Revokes the current session on the API (bearer attached by <see cref="SessionBearerHandler"/>).</summary>
        Task LogoutAsync(CancellationToken cancellationToken);
    }
}
