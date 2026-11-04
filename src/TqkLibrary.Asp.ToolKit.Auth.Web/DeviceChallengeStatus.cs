namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>Result of <see cref="DeviceChallengeService.Validate"/>.</summary>
    internal enum DeviceChallengeStatus
    {
        /// <summary>Issued by this app (key ring) for this kind and session, not expired, not used before.</summary>
        Valid,

        /// <summary>Genuine, right kind and session, but past its lifetime.</summary>
        Expired,

        /// <summary>Not decryptable, another kind, or another session.</summary>
        Invalid,

        /// <summary>Already used by a previous proof (seen on this instance).</summary>
        Replayed,
    }
}
