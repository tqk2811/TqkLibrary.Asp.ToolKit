namespace TqkLibrary.Asp.ToolKit.Auth.Web.Models
{
    /// <summary>One extra claim of <see cref="CurrentUserInfo.ExtraClaims"/>.</summary>
    /// <param name="Type">Claim type.</param>
    /// <param name="Value">Claim value.</param>
    public sealed record CurrentUserClaim(string Type, string Value);
}
