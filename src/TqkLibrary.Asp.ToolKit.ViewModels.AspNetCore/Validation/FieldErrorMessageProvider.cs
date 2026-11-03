using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Validation
{
    /// <summary>Gives every known framework validation attribute a marker message carrying its code, params and message.</summary>
    internal sealed class FieldErrorMessageProvider : IValidationMetadataProvider
    {
        public void CreateValidationMetadata(ValidationMetadataProviderContext context)
        {
            foreach (ValidationAttribute attribute in context.ValidationMetadata.ValidatorMetadata.OfType<ValidationAttribute>())
            {
                // Only the framework's own attributes: a subclass may validate something else entirely and
                // carries its own ErrorMessage, so it stays FIELD_INVALID_VALUE with its sentence.
                if (attribute.GetType().Assembly != typeof(ValidationAttribute).Assembly) continue;
                // Already rewritten (the same instance can be seen twice).
                if (attribute.ErrorMessage is { Length: > 0 } current && current[0] == FieldValidationErrors.Marker) continue;

                string? template = Template(attribute);
                string? marker = attribute switch
                {
                    RequiredAttribute => FieldValidationErrors.Encode(ApiErrorCodes.FieldRequired, template),
                    StringLengthAttribute s => FieldValidationErrors.Encode(ApiErrorCodes.FieldTooLong, template,
                        ("max", s.MaximumLength), ("min", s.MinimumLength > 0 ? s.MinimumLength : null)),
                    MaxLengthAttribute m => FieldValidationErrors.Encode(ApiErrorCodes.FieldTooLong, template, ("max", m.Length)),
                    MinLengthAttribute m => FieldValidationErrors.Encode(ApiErrorCodes.FieldTooShort, template, ("min", m.Length)),
                    LengthAttribute l => FieldValidationErrors.Encode(ApiErrorCodes.FieldOutOfRange, template, ("min", l.MinimumLength), ("max", l.MaximumLength)),
                    RangeAttribute r => FieldValidationErrors.Encode(ApiErrorCodes.FieldOutOfRange, template, ("min", r.Minimum), ("max", r.Maximum)),
                    RegularExpressionAttribute or EmailAddressAttribute or UrlAttribute or PhoneAttribute
                        => FieldValidationErrors.Encode(ApiErrorCodes.FieldInvalidFormat, template),
                    _ => null,
                };
                if (marker is null) continue;

                attribute.ErrorMessage = marker;
                attribute.ErrorMessageResourceType = null;
                attribute.ErrorMessageResourceName = null;
            }
        }

        /// <summary>
        /// The attribute's own message with <c>{0}</c> kept for the display name (resolved once, in the culture of the request that first builds the model metadata for the type).
        /// Remaining braces are escaped for the later <c>string.Format</c>.
        /// </summary>
        static string? Template(ValidationAttribute attribute)
        {
            const string placeholder = "\u0001NAME\u0001";
            try
            {
                return attribute.FormatErrorMessage(placeholder)
                    .Replace("{", "{{").Replace("}", "}}")
                    .Replace(placeholder, "{0}");
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
