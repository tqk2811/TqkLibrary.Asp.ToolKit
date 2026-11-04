namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// What happens to a request of a device-bound session (DBSC) whose proof cookie is missing, expired, issued for
    /// another session or invalid. See <see cref="DeviceBoundSessionOptions.FailureMode"/>.
    /// </summary>
    public enum DeviceProofFailureMode
    {
        /// <summary>The request runs as usual; the failure is only logged (Warning) and counted. Use it to roll out.</summary>
        ReportOnly,

        /// <summary>
        /// The request runs as a guest. The session is NOT signed out and the main cookie is kept, so the browser can
        /// refresh the proof and the next request works again.
        /// </summary>
        Reject,

        /// <summary>
        /// The session is revoked on the API (best effort), the main and proof cookies are deleted and the request runs
        /// as a guest.
        /// </summary>
        SignOut,
    }
}
