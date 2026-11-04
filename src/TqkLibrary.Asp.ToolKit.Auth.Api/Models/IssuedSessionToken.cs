namespace TqkLibrary.Asp.ToolKit.Auth.Api.Models
{
    /// <summary>
    /// A new session token: <see cref="Token"/> goes to the client (the web keeps it in its encrypted cookie), only
    /// <see cref="SecretHash"/> is stored with the session.
    /// </summary>
    public sealed record IssuedSessionToken(string Token, string SecretHash);
}
