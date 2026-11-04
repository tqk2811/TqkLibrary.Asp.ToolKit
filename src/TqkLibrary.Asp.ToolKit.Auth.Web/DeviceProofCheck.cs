namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>DBSC state of one request, computed by <see cref="DeviceProofEnforcer"/>.</summary>
    /// <param name="Applicable">False when DBSC is off or the path is excluded: nothing to enforce.</param>
    /// <param name="Bound">The session is known to be bound (binding cache, or <c>me</c> said so).</param>
    /// <param name="Failure">Why the proof failed (bound sessions only); null = proven, not bound, or already decided.</param>
    internal readonly record struct DeviceProofCheck(bool Applicable, bool Bound, DeviceProofFailureReason? Failure)
    {
        public static DeviceProofCheck NotApplicable => new(false, false, null);
    }
}
