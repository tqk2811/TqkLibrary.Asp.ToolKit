namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>Outcome of <see cref="DeviceProofJwtVerifier"/>.</summary>
    internal enum DeviceProofJwtError
    {
        None,
        Malformed,
        TooLarge,
        WrongType,
        AlgorithmNotAllowed,
        MissingKey,
        InvalidKey,
        KeyNotAllowed,
        AlgorithmMismatch,
        MissingJti,
        BadSignature,
    }
}
