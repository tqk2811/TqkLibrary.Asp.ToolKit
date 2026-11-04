namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>A verified (or rejected) DBSC proof.</summary>
    internal sealed record DeviceProofJwt(DeviceProofJwtError Error, string? Jti = null, DeviceKey? Key = null, string? Authorization = null)
    {
        public bool IsValid => Error == DeviceProofJwtError.None;
    }
}
