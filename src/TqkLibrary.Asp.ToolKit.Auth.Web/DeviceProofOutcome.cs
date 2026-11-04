namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// What the library does with a request of a device-bound session (DBSC) whose proof failed, as returned by
    /// <see cref="DeviceProofFailureHandler.HandleAsync"/>.
    /// </summary>
    public enum DeviceProofOutcome
    {
        /// <summary>The request runs as usual (same as <see cref="DeviceProofFailureMode.ReportOnly"/>); only logged and counted.</summary>
        Continue,

        /// <summary>
        /// The request runs as a guest WITHOUT the session token (see <see cref="DeviceProofFailureMode.Reject"/>). The main
        /// cookie is kept.
        /// </summary>
        Reject,

        /// <summary>The session is revoked on the API and both cookies are deleted (see <see cref="DeviceProofFailureMode.SignOut"/>).</summary>
        SignOut,

        /// <summary>
        /// The handler wrote the response itself: the library stops the pipeline right there (the rest of the app does not
        /// run), sends no session token, does not cache the user and leaves both cookies as they are. Returned without
        /// starting the response: the library answers 403 without body. (A <see cref="SignOut"/> returned after the
        /// handler started the response is treated as this outcome.)
        /// </summary>
        Handled,
    }
}
