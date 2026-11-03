using System.Text.Json;
using System.Text.Json.Serialization;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore
{
    /// <summary>Options of <c>AddTqkApiEnvelope</c>, <c>UseTqkRequestId</c> and <c>UseTqkApiExceptionWrapper</c>.</summary>
    public class TqkApiEnvelopeOptions
    {
        static readonly JsonSerializerOptions _defaultJsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>
        /// Header read from the request and echoed on the response. Default <c>X-Request-Id</c>. Must not be empty.
        /// Incoming ids longer than 128 characters or containing non-printable / non-ASCII characters are replaced by a generated id.
        /// </summary>
        public string RequestIdHeader { get; set; } = "X-Request-Id";

        /// <summary>Prefix of generated request ids (<c>prefix + Guid "N"</c>). Default <c>req_</c>.</summary>
        public string RequestIdPrefix { get; set; } = "req_";

        /// <summary>
        /// Serializes the <see cref="ApiErrorResponse"/> written by the exception wrapper middleware (which runs outside
        /// MVC, so the MVC formatter is not used). Default: System.Text.Json, camelCase, nulls ignored.
        /// Apps whose MVC output uses Newtonsoft must set it to their own serializer so the middleware error bodies
        /// match the MVC responses, e.g. <c>o.SerializeError = v =&gt; JsonConvert.SerializeObject(v, settings)</c>.
        /// Null properties (<c>meta</c>, <c>fields</c>, <c>details</c>...) are omitted only if the serializer ignores nulls.
        /// </summary>
        public Func<object, string> SerializeError { get; set; } = x => JsonSerializer.Serialize(x, x.GetType(), _defaultJsonOptions);

        /// <summary>
        /// Replaces the DataAnnotations / model binding messages globally so each <see cref="FieldError"/> gets a precise
        /// code (<c>FIELD_REQUIRED</c>, <c>FIELD_TOO_LONG</c>...) and params. Off (default): every field error is
        /// <c>FIELD_INVALID_VALUE</c> with the normal MVC message (the 400 <c>VALIDATION_FAILED</c> response and field
        /// paths still work).
        /// <para>
        /// Enable it only in API-only apps: it rewrites <c>ErrorMessage</c> of every DataAnnotations attribute app-wide,
        /// so Razor views and client-side validation would show marker strings. Messages are captured once per type,
        /// when MVC first builds the model metadata for it (the first request touching the type), in that request's
        /// culture; resource-based localization (<c>ErrorMessageResourceType</c>) is dropped.
        /// </para>
        /// </summary>
        public bool UseFieldErrorMessages { get; set; } = false;

        /// <summary>Sets <c>ApiBehaviorOptions.InvalidModelStateResponseFactory</c> to a 400 <c>VALIDATION_FAILED</c> response. Default true.</summary>
        public bool EnableValidationResponse { get; set; } = true;
    }
}
