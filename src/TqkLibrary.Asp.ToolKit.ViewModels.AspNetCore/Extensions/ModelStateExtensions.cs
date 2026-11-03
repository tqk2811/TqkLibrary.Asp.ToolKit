using Microsoft.AspNetCore.Mvc.ModelBinding;
using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Validation;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Extensions
{
    /// <summary>
    /// Converts an ASP.NET Core <see cref="ModelStateDictionary"/> to the error view models of
    /// <c>TqkLibrary.Asp.ToolKit.ViewModels</c>.
    /// </summary>
    public static class ModelStateExtensions
    {
        /// <summary>One <see cref="FieldError"/> per field and code, see <see cref="FieldValidationErrors.ToFieldErrors"/>.</summary>
        public static List<FieldError> ToFieldErrors(this ModelStateDictionary modelState, Type? bodyType = null, string? bodyName = null)
            => FieldValidationErrors.ToFieldErrors(modelState, bodyType, bodyName);

        /// <summary><c>VALIDATION_FAILED</c> response carrying the field errors of <paramref name="modelState"/>.</summary>
        /// <exception cref="InvalidOperationException">The model state is valid.</exception>
        public static ApiErrorResponse ToApiErrorResponse(
            this ModelStateDictionary modelState,
            string requestId,
            string? message = null,
            Type? bodyType = null,
            string? bodyName = null)
        {
            ArgumentNullException.ThrowIfNull(modelState);
            ArgumentNullException.ThrowIfNull(requestId);
            if (modelState.IsValid) throw new InvalidOperationException("Model state is valid, there is no error to return.");
            return ApiErrorResponse.Create(requestId, FieldValidationErrors.CreateError(modelState.ToFieldErrors(bodyType, bodyName), message));
        }
    }
}
