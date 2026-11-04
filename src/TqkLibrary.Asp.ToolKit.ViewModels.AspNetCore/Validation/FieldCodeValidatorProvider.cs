using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Validation
{
    /// <summary>
    /// Wraps the validators the DataAnnotations provider built for the framework's own attributes, so each failure
    /// message is tagged with its code and params (<see cref="FieldErrorTags"/>). Messages, resources and
    /// client-side validation are left untouched. Must be added after the DataAnnotations provider.
    /// </summary>
    internal sealed class FieldCodeValidatorProvider : IModelValidatorProvider
    {
        public void CreateValidators(ModelValidatorProviderContext context)
        {
            foreach (ValidatorItem item in context.Results)
            {
                if (item.Validator is null or FieldCodeValidator) continue;
                if (item.ValidatorMetadata is not ValidationAttribute attribute) continue;
                // Only the framework's own attributes: a subclass may validate something else entirely,
                // so it stays FIELD_INVALID_VALUE with its own message.
                if (attribute.GetType().Assembly != typeof(ValidationAttribute).Assembly) continue;
                if (Describe(attribute) is not { } described) continue;
                item.Validator = new FieldCodeValidator(item.Validator, described.Code, described.Params);
            }
        }

        static (string Code, Dictionary<string, object>? Params)? Describe(ValidationAttribute attribute) => attribute switch
        {
            RequiredAttribute => (ApiErrorCodes.FieldRequired, null),
            // With a minimum the same message covers too short and too long: report the range.
            StringLengthAttribute { MinimumLength: > 0 } s => (ApiErrorCodes.FieldOutOfRange, Params(("min", s.MinimumLength), ("max", s.MaximumLength))),
            StringLengthAttribute s => (ApiErrorCodes.FieldTooLong, Params(("max", s.MaximumLength))),
            MaxLengthAttribute m => (ApiErrorCodes.FieldTooLong, Params(("max", m.Length))),
            MinLengthAttribute m => (ApiErrorCodes.FieldTooShort, Params(("min", m.Length))),
            LengthAttribute l => (ApiErrorCodes.FieldOutOfRange, Params(("min", l.MinimumLength), ("max", l.MaximumLength))),
            RangeAttribute r => (ApiErrorCodes.FieldOutOfRange, Params(("min", r.Minimum), ("max", r.Maximum))),
            RegularExpressionAttribute or EmailAddressAttribute or UrlAttribute or PhoneAttribute => (ApiErrorCodes.FieldInvalidFormat, null),
            _ => null,
        };

        static Dictionary<string, object>? Params(params (string Key, object? Value)[] values)
        {
            Dictionary<string, object>? result = null;
            foreach (var (key, value) in values)
            {
                if (value is null) continue;
                // [Range(typeof(decimal), "1", "10")] keeps its bounds as strings: send them as numbers when they are.
                (result ??= new())[key] = value is string text
                    ? long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l
                        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d
                        : text
                    : value;
            }
            return result;
        }
    }
}
