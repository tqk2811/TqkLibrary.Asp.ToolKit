namespace TqkLibrary.Asp.ToolKit.Auth.Api.Models
{
    /// <summary>
    /// A new refresh token: <see cref="Token"/> goes to the client, only <see cref="SecretHash"/> is stored with the
    /// session.
    /// </summary>
    public sealed record IssuedRefreshToken(string Token, string SecretHash);
}
