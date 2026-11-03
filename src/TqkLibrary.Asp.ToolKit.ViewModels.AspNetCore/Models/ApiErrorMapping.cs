namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Models
{
    /// <summary>Result of <see cref="Interfaces.IApiExceptionMapper.Map"/>.</summary>
    public sealed record ApiErrorMapping(int StatusCode, string Code, string Message, Dictionary<string, object>? Details = null);
}
