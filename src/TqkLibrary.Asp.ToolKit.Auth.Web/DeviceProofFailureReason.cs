namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>Why the proof cookie of a device-bound session did not prove the device.</summary>
    public enum DeviceProofFailureReason
    {
        /// <summary>The request carries no proof cookie.</summary>
        Missing,

        /// <summary>The proof is genuine but expired, beyond <see cref="DeviceBoundSessionOptions.ProofExpiryGrace"/>.</summary>
        Expired,

        /// <summary>The proof is genuine but was issued for another session.</summary>
        WrongSession,

        /// <summary>The proof cannot be decrypted (forged, corrupted, or protected with another key ring).</summary>
        Invalid,
    }
}
