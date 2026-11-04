namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>DBSC state of one request, computed by <see cref="DeviceProofEnforcer"/>.</summary>
    /// <param name="Applicable">False when DBSC is off or the path is excluded: nothing to enforce.</param>
    /// <param name="Bound">The session is known to be bound (binding cache, or <c>me</c> said so).</param>
    /// <param name="Failure">Why the proof failed (bound sessions only); null = proven, not bound, or already decided.</param>
    /// <param name="KnownUnbound">
    /// A fresh <c>me</c> reported the session as not bound within <see cref="DeviceBoundSessionOptions.UnboundRecheckInterval"/>.
    /// </param>
    internal readonly record struct DeviceProofCheck(bool Applicable, bool Bound, DeviceProofFailureReason? Failure, bool KnownUnbound = false)
    {
        public static DeviceProofCheck NotApplicable => new(false, false, null);

        /// <summary>DBSC applies but nothing is known about the binding: it must be learned from a fresh <c>me</c>.</summary>
        public bool BindingUnknown => Applicable && !Bound && !KnownUnbound;
    }
}
