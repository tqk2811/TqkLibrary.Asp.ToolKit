namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>DBSC state of one request, computed by <see cref="DeviceProofEnforcer.CheckAsync"/>.</summary>
    /// <param name="Applicable">False when DBSC is off or the path is excluded: nothing to enforce.</param>
    /// <param name="Bound">The cookie ticket carries a device key.</param>
    /// <param name="Failure">Why the proof failed (bound tickets only); null = proven or not bound.</param>
    internal readonly record struct DeviceProofCheck(bool Applicable, bool Bound, DeviceProofFailureReason? Failure)
    {
        public static DeviceProofCheck NotApplicable => new(false, false, null);
    }
}
