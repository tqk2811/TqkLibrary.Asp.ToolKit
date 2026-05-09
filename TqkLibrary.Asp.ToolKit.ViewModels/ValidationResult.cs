using Microsoft.AspNetCore.Mvc.ModelBinding;
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
    }
    public static class ValidationResultExtension
    {
        public static IEnumerable<ValidationResult> Parse(this ModelStateDictionary modelStateDictionary)
        {
            foreach (var item in modelStateDictionary)
            {
                var result = new ValidationResult()
                {
                    Name = item.Key,
                    Messages = new(),
                };
                foreach (var err in item.Value.Errors)
                {
                    result.Messages.Add(err.ErrorMessage);
                }
                yield return result;
            }
        }
        public static void AddErrors(this ModelStateDictionary modelStateDictionary, IEnumerable<IValidationResult> validationResults)
        {
            foreach (var item in validationResults)
            {
                if (item?.Messages?.Any() == true)
                {
                    foreach (var item1 in item.Messages)
                    {
                        modelStateDictionary.AddModelError(item.Name, item1);
                    }
                }
            }
        }
    }
}
