using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Extensions;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Filters
{
    /// <summary>
    /// Wraps the value an action returns into <see cref="ApiResponse{T}"/>, so actions can return their data type
    /// directly (<c>Task&lt;ProductDto&gt;</c>). The result stays an <see cref="ObjectResult"/>, serialized by the app's
    /// output formatter.
    /// <para>Left untouched:</para>
    /// <list type="bullet">
    ///   <item>response already started (the action wrote the body itself, e.g. SSE): replaced by an empty result;</item>
    ///   <item>endpoints marked with <see cref="RawResponseAttribute"/>;</item>
    ///   <item>values that are already <see cref="ApiResponse{T}"/> or <see cref="ApiErrorResponse"/>;</item>
    ///   <item>non 2xx object results and any other result kind (files, redirects, challenges...).</item>
    /// </list>
    /// A returned <see cref="Stream"/> becomes a <see cref="FileStreamResult"/> using the response Content-Type.
    /// </summary>
    public sealed class ApiEnvelopeResultFilter : IAsyncResultFilter
    {
        public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            context.Result = Rewrite(context);
            return next();
        }

        static IActionResult Rewrite(ResultExecutingContext context)
        {
            IActionResult result = context.Result;

            // Leaving the result would make MVC serialize it into the middle of the stream being written.
            if (context.HttpContext.Response.HasStarted)
                return new EmptyResult();

            bool isRaw = context.ActionDescriptor.EndpointMetadata.OfType<RawResponseAttribute>().Any();

            if (result is ObjectResult { Value: Stream stream } streamResult && (streamResult.StatusCode is null || streamResult.StatusCode < 300))
                return ToFileResult(context, stream);

            // A raw action returning a string with its own Content-Type: without ContentResult MVC looks for a
            // formatter of that type (e.g. text/markdown), finds none and answers 406.
            if (isRaw
                && result is ObjectResult { Value: string text } stringResult
                && !string.IsNullOrWhiteSpace(context.HttpContext.Response.ContentType))
            {
                return new ContentResult
                {
                    Content = text,
                    ContentType = context.HttpContext.Response.ContentType,
                    StatusCode = stringResult.StatusCode,
                };
            }

            if (isRaw)
                return result;

            switch (result)
            {
                case ObjectResult objectResult:
                    if (objectResult.StatusCode >= 300)
                        return result;
                    if (IsAlreadyEnvelope(objectResult.Value))
                        return result;
                    // Keep the original result (status 201/202, Location header of Created*, ContentTypes, Formatters)
                    // and only swap its value.
                    objectResult.Value = ApiResponse<object>.Ok(objectResult.Value!, context.HttpContext.GetRequestId());
                    objectResult.DeclaredType = typeof(ApiResponse<object>);
                    return objectResult;

                // An action returning Task (no data) gives EmptyResult: still send the envelope with data null,
                // so the response shape does not change from one endpoint to another.
                case EmptyResult:
                case StatusCodeResult { StatusCode: StatusCodes.Status200OK or StatusCodes.Status204NoContent }:
                    return Envelope(null, context.HttpContext.GetRequestId());

                default:
                    return result;
            }
        }

        static ObjectResult Envelope(object? value, string requestId)
            => new(ApiResponse<object>.Ok(value!, requestId))
            {
                StatusCode = StatusCodes.Status200OK,
                DeclaredType = typeof(ApiResponse<object>),
            };

        static bool IsAlreadyEnvelope(object? value)
        {
            if (value is ApiErrorResponse)
                return true;

            Type? type = value?.GetType();
            return type is { IsGenericType: true } && type.GetGenericTypeDefinition() == typeof(ApiResponse<>);
        }

        static FileStreamResult ToFileResult(ResultExecutingContext context, Stream stream)
        {
            // FileStreamResult overwrites Content-Type, so pass the one the action set.
            string? contentType = context.HttpContext.Response.ContentType;
            if (string.IsNullOrWhiteSpace(contentType))
                contentType = "application/octet-stream";

            return new FileStreamResult(stream, contentType)
            {
                // Range needs a seekable stream.
                EnableRangeProcessing = stream.CanSeek,
            };
        }
    }
}
