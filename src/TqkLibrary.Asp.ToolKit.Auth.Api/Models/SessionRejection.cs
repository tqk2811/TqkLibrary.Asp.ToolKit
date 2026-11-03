namespace TqkLibrary.Asp.ToolKit.Auth.Api.Models
{
    /// <summary>
    /// Why <see cref="SessionTokenValidator"/> rejected the token of the current request. Stored as an
    /// <see cref="Microsoft.AspNetCore.Http.HttpContext.Features"/> entry; read it in <c>JwtBearerEvents.OnChallenge</c>
    /// through <see cref="SessionTokenValidator.GetSessionRejection"/> to write a custom 401 body.
    /// </summary>
    /// <param name="Reason">Reason code (<see cref="SessionRejectionReasons"/> or the app's own), null when not given.</param>
    /// <param name="Message">Message passed to <c>context.Fail</c> (diagnostics, not meant for end users).</param>
    public sealed record SessionRejection(string? Reason, string Message);
}
