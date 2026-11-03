using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Models;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Interfaces
{
    /// <summary>
    /// Maps an app specific exception to an error response. Register implementations in DI; the exception wrapper
    /// middleware tries them in registration order after <see cref="ApiErrorException"/> and before the default 500.
    /// </summary>
    public interface IApiExceptionMapper
    {
        /// <returns>The mapping, or null when this mapper does not handle <paramref name="exception"/>.</returns>
        ApiErrorMapping? Map(Exception exception);
    }
}
