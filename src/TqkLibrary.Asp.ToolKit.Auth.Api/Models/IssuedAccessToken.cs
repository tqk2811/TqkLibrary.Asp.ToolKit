namespace TqkLibrary.Asp.ToolKit.Auth.Api.Models
{
    /// <summary>A signed access token and its expiry (UTC).</summary>
    public sealed record IssuedAccessToken(string Token, DateTime ExpiresAt);
}
