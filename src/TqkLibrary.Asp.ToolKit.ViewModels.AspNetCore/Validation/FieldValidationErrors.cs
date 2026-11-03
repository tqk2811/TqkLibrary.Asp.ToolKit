using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Extensions;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Validation
{
    /// <summary>
    /// Turns invalid model state into <c>VALIDATION_FAILED</c> + one <see cref="FieldError"/> per field, each with a code
    /// (<c>FIELD_REQUIRED</c>, <c>FIELD_TOO_LONG</c>...), parameters and the formatted message, so a UI can translate the
    /// code instead of showing a server sentence.
    /// <para>
    /// MVC keeps only the error MESSAGE in <see cref="ModelStateDictionary"/>, not the attribute that failed. With
    /// <see cref="TqkApiEnvelopeOptions.UseFieldErrorMessages"/>, every framework validation attribute and every binding
    /// message gets a marker message (<see cref="Marker"/> + code + params + <see cref="Marker"/> + message) parsed back
    /// here. Anything without a marker (JSON reader errors, custom attributes) becomes <c>FIELD_INVALID_VALUE</c> with
    /// its original message.
    /// </para>
    /// </summary>
    public static class FieldValidationErrors
    {
        /// <summary>Unit separator: never typed by a user, never produced by MVC itself.</summary>
        internal const char Marker = '\u001F';

        /// <summary>The <c>InvalidModelStateResponseFactory</c> set by <c>AddTqkApiEnvelope</c>: 400 + <see cref="ApiErrorResponse"/>.</summary>
        public static IActionResult CreateResponse(ActionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var body = context.ActionDescriptor.Parameters
                .FirstOrDefault(p => p.BindingInfo?.BindingSource == BindingSource.Body);
            List<FieldError> fields = ToFieldErrors(context.ModelState, body?.ParameterType, body?.Name);
            return new BadRequestObjectResult(ApiErrorResponse.Create(context.HttpContext.GetRequestId(), CreateError(fields)));
        }

        /// <summary><c>VALIDATION_FAILED</c> error whose message lists (up to 5) fields and codes.</summary>
        public static ApiError CreateError(IReadOnlyList<FieldError> fields, string? message = null)
        {
            ArgumentNullException.ThrowIfNull(fields);
            message ??= fields.Count == 0
                ? "The request is invalid."
                : "The request is invalid: "
                    + string.Join("; ", fields.Take(5).Select(f => (f.Field.Length == 0 ? "(body)" : f.Field) + " " + f.Code))
                    + (fields.Count > 5 ? $"; and {fields.Count - 5} more." : ".");
            return new ApiError { Code = ApiErrorCodes.ValidationFailed, Message = message, Fields = fields.ToList() };
        }

        /// <param name="modelState">Model state to convert; keys without errors are skipped.</param>
        /// <param name="bodyType">Type of the <c>[FromBody]</c> parameter, so names follow its JSON property names.</param>
        /// <param name="bodyName">Its parameter name, stripped when MVC uses it as the key prefix.</param>
        public static List<FieldError> ToFieldErrors(ModelStateDictionary modelState, Type? bodyType = null, string? bodyName = null)
        {
            ArgumentNullException.ThrowIfNull(modelState);
            List<FieldError> result = new();
            foreach (var (key, entry) in modelState)
            {
                if (entry.Errors.Count == 0) continue;
                string field = ToClientPath(key, bodyType, bodyName);
                foreach (ModelError error in entry.Errors)
                {
                    var (code, parameters, message) = Decode(error.ErrorMessage);
                    // Same field + same code twice (e.g. a binding error then the implicit [Required]) adds nothing.
                    if (result.Any(f => f.Field == field && f.Code == code)) continue;
                    result.Add(new FieldError { Field = field, Code = code, Params = parameters, Message = message });
                }
            }
            return result;
        }

        /// <summary>
        /// <c>Items[0].DisplayName</c> → <c>items[0].displayName</c>; <c>$.name</c> (JSON reader path) → <c>name</c>;
        /// <c>$</c> → empty. With <paramref name="bodyType"/>, names follow <c>[JsonPropertyName]</c>/<c>[JsonProperty]</c>.
        /// </summary>
        public static string ToClientPath(string key, Type? bodyType = null, string? bodyName = null)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (key == "$") return string.Empty;
            if (!key.StartsWith("$.", StringComparison.Ordinal))
            {
                // MVC prefixes keys with the parameter name when it binds under that name.
                if (bodyName is { Length: > 0 }
                    && key.StartsWith(bodyName, StringComparison.OrdinalIgnoreCase)
                    && (key.Length == bodyName.Length || key[bodyName.Length] is '.' or '['))
                {
                    string rest = key[bodyName.Length..].TrimStart('.');
                    if (rest.Length == 0 || rest[0] == '[') return rest;
                    key = rest;
                }
                return JsonPropertyNames.ToJsonPath(bodyType, key);
            }

            // JSON reader path: already the names the client sent.
            key = key[2..];
            StringBuilder builder = new(key.Length);
            foreach (string segment in key.Split('.'))
            {
                if (builder.Length > 0) builder.Append('.');
                int bracket = segment.IndexOf('[');
                string name = bracket < 0 ? segment : segment[..bracket];
                builder.Append(JsonPropertyNames.CamelCase(name));
                if (bracket >= 0) builder.Append(segment[bracket..]);
            }
            return builder.ToString();
        }

        /// <summary>Replaces binding messages with markers and adds the attribute-marker provider.</summary>
        internal static MvcOptions UseFieldErrorMessages(this MvcOptions options)
        {
            string required = Encode(ApiErrorCodes.FieldRequired, "A value is required.");

            var messages = options.ModelBindingMessageProvider;
            messages.SetMissingBindRequiredValueAccessor(x => Encode(ApiErrorCodes.FieldRequired, $"A value for the '{x}' parameter or property was not provided."));
            messages.SetMissingKeyOrValueAccessor(() => required);
            messages.SetMissingRequestBodyRequiredValueAccessor(() => Encode(ApiErrorCodes.FieldRequired, "A non-empty request body is required."));
            messages.SetValueMustNotBeNullAccessor(x => Encode(ApiErrorCodes.FieldRequired, $"The value '{x}' is invalid."));
            messages.SetAttemptedValueIsInvalidAccessor((v, f) => Encode(ApiErrorCodes.FieldInvalidValue, $"The value '{v}' is not valid for {f}."));
            messages.SetNonPropertyAttemptedValueIsInvalidAccessor(v => Encode(ApiErrorCodes.FieldInvalidValue, $"The value '{v}' is not valid."));
            messages.SetUnknownValueIsInvalidAccessor(f => Encode(ApiErrorCodes.FieldInvalidValue, $"The supplied value is invalid for {f}."));
            messages.SetNonPropertyUnknownValueIsInvalidAccessor(() => Encode(ApiErrorCodes.FieldInvalidValue, "The supplied value is invalid."));
            messages.SetValueIsInvalidAccessor(v => Encode(ApiErrorCodes.FieldInvalidValue, $"The value '{v}' is invalid."));
            messages.SetValueMustBeANumberAccessor(f => Encode(ApiErrorCodes.FieldInvalidValue, $"The field {f} must be a number."));
            messages.SetNonPropertyValueMustBeANumberAccessor(() => Encode(ApiErrorCodes.FieldInvalidValue, "The field must be a number."));

            // Must run AFTER DataAnnotationsMetadataProvider (which adds the implicit [Required] for
            // non-nullable properties): appending to the list is enough.
            options.ModelMetadataDetailsProviders.Add(new FieldErrorMessageProvider());
            return options;
        }

        /// <summary>
        /// <paramref name="message"/> is appended after a second <see cref="Marker"/>. For attribute markers it still
        /// contains <c>{0}</c>, which the attribute's own <c>string.Format</c> replaces with the display name.
        /// </summary>
        internal static string Encode(string code, string? message, params (string Key, object? Value)[] parameters)
        {
            StringBuilder builder = new StringBuilder().Append(Marker).Append(code);
            foreach (var (k, v) in parameters)
            {
                if (v is null) continue;
                // Braces would break the string.Format that ValidationAttribute runs on ErrorMessage.
                string text = Convert.ToString(v, CultureInfo.InvariantCulture)!.Replace("{", "").Replace("}", "");
                builder.Append('|').Append(k).Append('=').Append(text.Replace("|", "").Replace(Marker.ToString(), ""));
            }
            if (!string.IsNullOrEmpty(message))
                builder.Append(Marker).Append(message.Replace(Marker.ToString(), ""));
            return builder.ToString();
        }

        internal static (string Code, Dictionary<string, object>? Params, string? Message) Decode(string? errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage))
                return (ApiErrorCodes.FieldInvalidValue, null, null);
            if (errorMessage[0] != Marker)
                return (ApiErrorCodes.FieldInvalidValue, null, errorMessage);

            string body = errorMessage[1..];
            string? message = null;
            int split = body.IndexOf(Marker);
            if (split >= 0)
            {
                message = body[(split + 1)..];
                body = body[..split];
            }

            string[] parts = body.Split('|');
            Dictionary<string, object>? parameters = null;
            foreach (string part in parts.Skip(1))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string value = part[(eq + 1)..];
                parameters ??= new();
                parameters[part[..eq]] = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l
                    : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d
                    : value;
            }
            return (parts[0], parameters, string.IsNullOrEmpty(message) ? null : message);
        }
    }
}
