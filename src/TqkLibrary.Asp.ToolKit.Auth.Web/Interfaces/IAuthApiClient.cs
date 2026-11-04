using TqkLibrary.Asp.ToolKit.Auth.Web.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Interfaces
{
    /// <summary>
    /// The app's bridge to its API. Implement it on top of the app's own HTTP client.
    /// <para><b>Error contract</b> (the library relies on it to decide between "sign out" and "try again later"):</para>
    /// <list type="bullet">
    ///   <item>When the API rejects <c>me</c> (ANY HTTP 401: <c>me</c> has no business 401, and a proxy may strip <c>WWW-Authenticate</c>, which would leave the
    ///   user signed in without permissions forever; a 403 does NOT count) the implementation MUST throw
    ///   <see cref="SessionExpiredException"/>. <see cref="Extensions.AuthApiResponseExtensions.ThrowIfSessionRejected"/>
    ///   does exactly that for an <see cref="HttpResponseMessage"/>.</item>
    ///   <item>Every other exception (5xx, network, timeout, deserialization) is treated as a transient failure
    ///   and the user is NOT signed out. So never map a transient error to <see cref="SessionExpiredException"/>,
    ///   and never swallow such a 401 into another exception type.</item>
    /// </list>
    /// </summary>
    public interface IAuthApiClient
    {
        /// <summary>
        /// The current user of the session. Called through the app's client with <see cref="SessionBearerHandler"/>
        /// attached, which adds the session token as bearer (for those calls only a 401 with a Bearer challenge throws <see cref="SessionExpiredException"/>).
        /// </summary>
        Task<CurrentUserInfo> GetCurrentUserAsync(CancellationToken cancellationToken);

        /// <summary>Revokes the current session on the API (bearer attached by <see cref="SessionBearerHandler"/>).</summary>
        Task LogoutAsync(CancellationToken cancellationToken);
    }
}
