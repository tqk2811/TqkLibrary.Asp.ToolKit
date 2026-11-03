using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Extensions
{
    /// <summary>
    /// Converts between ASP.NET Core <see cref="ModelStateDictionary"/> and the view models of
    /// <c>TqkLibrary.Asp.ToolKit.ViewModels</c>.
    /// </summary>
    public static class ModelStateExtensions
    {
        /// <summary>Message used when a binding error carries only an exception and no message.</summary>
        public const string DefaultErrorMessage = "The value is invalid.";

        /// <summary>
        /// One <see cref="ValidationResult"/> per key that has errors. Keys without errors are skipped.
        /// A binding error with an empty message (only an exception) gets <see cref="DefaultErrorMessage"/>,
        /// so internal exception details are not sent to the client.
        /// </summary>
        public static List<ValidationResult> ToValidationResults(this ModelStateDictionary modelState)
        {
            ArgumentNullException.ThrowIfNull(modelState);
            List<ValidationResult> results = new();
            foreach (var item in modelState)
            {
                if (item.Value.Errors.Count == 0) continue;
                results.Add(new ValidationResult()
                {
                    Name = item.Key,
                    Messages = item.Value.Errors
                        .Select(x => string.IsNullOrEmpty(x.ErrorMessage) ? DefaultErrorMessage : x.ErrorMessage)
                        .ToList(),
                });
            }
            return results;
        }

        /// <summary>Adds every message of <paramref name="validationResults"/> to the model state.</summary>
        public static void AddErrors(this ModelStateDictionary modelState, IEnumerable<IValidationResult> validationResults)
        {
            ArgumentNullException.ThrowIfNull(modelState);
            ArgumentNullException.ThrowIfNull(validationResults);
            foreach (var item in validationResults)
            {
                if (item?.Messages is null) continue;
                foreach (var message in item.Messages)
                {
                    modelState.AddModelError(item.Name, message);
                }
            }
        }

        /// <exception cref="InvalidOperationException">The model state is valid.</exception>
        public static ApiErrorResult ToApiErrorResult(this ModelStateDictionary modelState, string? message = null)
        {
            ThrowIfValid(modelState);
            return new ApiErrorResult(modelState.ToValidationResults(), message);
        }

        /// <exception cref="InvalidOperationException">The model state is valid.</exception>
        public static ApiErrorResult<T> ToApiErrorResult<T>(this ModelStateDictionary modelState, string? message = null)
        {
            ThrowIfValid(modelState);
            return new ApiErrorResult<T>(modelState.ToValidationResults(), message);
        }

        static void ThrowIfValid(ModelStateDictionary modelState)
        {
            ArgumentNullException.ThrowIfNull(modelState);
            if (modelState.IsValid) throw new InvalidOperationException("Model state is valid, there is no error to return.");
        }
    }
}
