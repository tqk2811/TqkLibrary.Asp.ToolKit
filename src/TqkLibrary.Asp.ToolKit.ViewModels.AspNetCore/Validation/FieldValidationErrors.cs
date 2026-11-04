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
    /// <see cref="TqkApiEnvelopeOptions.UseFieldErrorCodes"/>, the validators of the framework validation attributes and
    /// the model binding messages tag each message string they produce with its code and params, leaving the text as
    /// is (Razor, client-side validation and resource localization are unaffected). Anything untagged (JSON reader
    /// errors, custom attributes, messages added by hand) becomes <c>FIELD_INVALID_VALUE</c> with its original message.
    /// The tag follows the string instance MVC stored: a message rebuilt from its text (trimmed, concatenated,
    /// re-localized) loses it.
    /// </para>
    /// </summary>
    public static class FieldValidationErrors
    {
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

        /// <summary>Tags the model binding messages and wraps the DataAnnotations validators with their codes.</summary>
        internal static MvcOptions UseFieldErrorCodes(this MvcOptions options)
        {
            // Idempotent: the provider doubles as the "already applied" flag.
            if (options.ModelValidatorProviders.OfType<FieldCodeValidatorProvider>().Any()) return options;

            // Wrap the accessors already set (MVC defaults or the app's own localized ones): the text stays theirs.
            var m = options.ModelBindingMessageProvider;
            var missingBindRequired = m.MissingBindRequiredValueAccessor;
            var missingKeyOrValue = m.MissingKeyOrValueAccessor;
            var missingBody = m.MissingRequestBodyRequiredValueAccessor;
            var mustNotBeNull = m.ValueMustNotBeNullAccessor;
            var attemptedInvalid = m.AttemptedValueIsInvalidAccessor;
            var nonPropertyAttemptedInvalid = m.NonPropertyAttemptedValueIsInvalidAccessor;
            var unknownInvalid = m.UnknownValueIsInvalidAccessor;
            var nonPropertyUnknownInvalid = m.NonPropertyUnknownValueIsInvalidAccessor;
            var valueInvalid = m.ValueIsInvalidAccessor;
            var mustBeNumber = m.ValueMustBeANumberAccessor;
            var nonPropertyMustBeNumber = m.NonPropertyValueMustBeANumberAccessor;

            const string required = ApiErrorCodes.FieldRequired;
            const string invalid = ApiErrorCodes.FieldInvalidValue;
            m.SetMissingBindRequiredValueAccessor(x => FieldErrorTags.Attach(missingBindRequired(x), required));
            m.SetMissingKeyOrValueAccessor(() => FieldErrorTags.Attach(missingKeyOrValue(), required));
            m.SetMissingRequestBodyRequiredValueAccessor(() => FieldErrorTags.Attach(missingBody(), required));
            m.SetValueMustNotBeNullAccessor(x => FieldErrorTags.Attach(mustNotBeNull(x), required));
            m.SetAttemptedValueIsInvalidAccessor((v, f) => FieldErrorTags.Attach(attemptedInvalid(v, f), invalid));
            m.SetNonPropertyAttemptedValueIsInvalidAccessor(v => FieldErrorTags.Attach(nonPropertyAttemptedInvalid(v), invalid));
            m.SetUnknownValueIsInvalidAccessor(f => FieldErrorTags.Attach(unknownInvalid(f), invalid));
            m.SetNonPropertyUnknownValueIsInvalidAccessor(() => FieldErrorTags.Attach(nonPropertyUnknownInvalid(), invalid));
            m.SetValueIsInvalidAccessor(v => FieldErrorTags.Attach(valueInvalid(v), invalid));
            m.SetValueMustBeANumberAccessor(f => FieldErrorTags.Attach(mustBeNumber(f), invalid));
            m.SetNonPropertyValueMustBeANumberAccessor(() => FieldErrorTags.Attach(nonPropertyMustBeNumber(), invalid));

            // Must run AFTER the DataAnnotations validator provider (it builds the validators this one wraps):
            // called from PostConfigure, so appending is enough.
            options.ModelValidatorProviders.Add(new FieldCodeValidatorProvider());
            return options;
        }

        internal static (string Code, Dictionary<string, object>? Params, string? Message) Decode(string? errorMessage)
        {
            string? message = string.IsNullOrEmpty(errorMessage) ? null : errorMessage;
            return FieldErrorTags.TryGet(errorMessage, out FieldErrorTags.Tag? tag)
                ? (tag!.Code, tag.Params is null ? null : new(tag.Params), message)
                : (ApiErrorCodes.FieldInvalidValue, null, message);
        }
    }
}
