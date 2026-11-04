namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>What <see cref="DeviceProofEnforcer"/> did with a request.</summary>
    internal enum DeviceProofResult
    {
        /// <summary>Nothing to enforce, proof valid, or the failure is only reported: the request runs as usual.</summary>
        Proceed,

        /// <summary>The request now runs as a guest without the session token (Reject, SignOut).</summary>
        Guest,

        /// <summary>The failure handler wrote the response: the pipeline must stop.</summary>
        Handled,
    }
}
