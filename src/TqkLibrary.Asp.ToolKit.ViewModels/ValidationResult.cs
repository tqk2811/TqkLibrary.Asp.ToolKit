using System.Diagnostics.CodeAnalysis;

namespace TqkLibrary.Asp.ToolKit.ViewModels
{
    public interface IValidationResult
    {
        string Name { get; }
        IReadOnlyList<string>? Messages { get; }
    }
    public class ValidationResult : IValidationResult
    {
        public ValidationResult()
        {

        }
        [SetsRequiredMembers]
        public ValidationResult(string name)
        {
            this.Name = name;
        }
        [SetsRequiredMembers]
        public ValidationResult(string name, string message)
        {
            this.Name = name;
            this.Messages = new List<string>() { message };
        }
        public required string Name { get; set; }
        public List<string>? Messages { get; set; }
        IReadOnlyList<string>? IValidationResult.Messages => Messages;

        public static ValidationResult? Clone(IValidationResult? validationResult)
        {
            if (validationResult is null) return null;
            return new ValidationResult()
            {
                Name = validationResult.Name,
                Messages = validationResult.Messages?.ToList(),
            };
        }

        /// <summary>
        /// Builds validation results from a field → messages map (same shape as <c>ValidationProblemDetails.Errors</c>).
        /// </summary>
        public static List<ValidationResult> FromErrors(IReadOnlyDictionary<string, string[]> errors)
        {
            ArgumentNullException.ThrowIfNull(errors);
            return errors
                .Select(x => new ValidationResult() { Name = x.Key, Messages = x.Value?.ToList() ?? new() })
                .ToList();
        }
    }
}
