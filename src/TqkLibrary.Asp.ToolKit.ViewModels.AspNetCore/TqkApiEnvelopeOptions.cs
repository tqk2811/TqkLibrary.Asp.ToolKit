using Microsoft.AspNetCore.Http;
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
        /// Gives each <see cref="FieldError"/> a precise code (<c>FIELD_REQUIRED</c>, <c>FIELD_TOO_LONG</c>...) and params
        /// for the framework DataAnnotations attributes and the model binding errors. Default true.
        /// <para>
        /// Messages are not changed: the validators and binding message accessors (MVC's or the app's own, set before
        /// <c>AddTqkApiEnvelope</c>) keep their text, so Razor views, client-side validation and resource localization
        /// work as usual. Off: every field error is <c>FIELD_INVALID_VALUE</c> with its message.
        /// </para>
        /// </summary>
        public bool UseFieldErrorCodes { get; set; } = true;

        /// <summary>
        /// Optional last step of the exception wrapper middleware: receives every mapping (from
        /// <see cref="ApiErrorException"/>, the mappers or the default 500) and returns the one to write. Use it for
        /// rules keyed on the error code whatever the source, e.g. a <c>Retry-After</c> header or a retryable flag.
        /// Return the received mapping (or null) to keep it unchanged, or a modified copy (<c>mapping with { ... }</c>).
        /// If it throws, the error is logged and the original mapping is written.
        /// </summary>
        public Func<HttpContext, Exception, Models.ApiErrorMapping, Models.ApiErrorMapping?>? TransformError { get; set; }

        /// <summary>Sets <c>ApiBehaviorOptions.InvalidModelStateResponseFactory</c> to a 400 <c>VALIDATION_FAILED</c> response. Default true.</summary>
        public bool EnableValidationResponse { get; set; } = true;
    }
}
