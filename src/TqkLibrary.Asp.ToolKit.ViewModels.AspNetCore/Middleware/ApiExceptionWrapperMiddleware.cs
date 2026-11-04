using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Extensions;
using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Interfaces;
using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Models;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Middleware
{
    /// <summary>
    /// Turns unhandled exceptions into an <see cref="ApiErrorResponse"/>:
    /// <list type="bullet">
    ///   <item>client aborted the request: 499, no body;</item>
    ///   <item>response already started: the connection is aborted (a truncated body must not look complete);</item>
    ///   <item><see cref="ApiErrorException"/>: its status and code;</item>
    ///   <item>otherwise each <see cref="IApiExceptionMapper"/> from DI, in registration order;</item>
    ///   <item>fallback: 500 <see cref="ApiErrorCodes.InternalError"/> (exception details are not sent).</item>
    /// </list>
    /// </summary>
    public sealed class ApiExceptionWrapperMiddleware
    {
        /// <summary>"Client closed request" (nginx convention).</summary>
        public const int ClientClosedRequestStatusCode = 499;
        public const string DefaultErrorMessage = "An unexpected error occurred.";

        readonly RequestDelegate _next;
        readonly ILogger<ApiExceptionWrapperMiddleware> _logger;
        readonly IOptions<TqkApiEnvelopeOptions> _options;

        public ApiExceptionWrapperMiddleware(
            RequestDelegate next,
            ILogger<ApiExceptionWrapperMiddleware> logger,
            IOptions<TqkApiEnvelopeOptions> options)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            // ConnectionResetException alone already means "client left" (it can arrive before RequestAborted is set);
            // cancellation and IOException only count once RequestAborted is set, so a server side IOException (disk
            // full...) while the client is still there stays a server error.
            catch (Exception ex) when (ex is ConnectionResetException
                || (context.RequestAborted.IsCancellationRequested && ex is OperationCanceledException or IOException))
            {
                // Nobody reads the body any more; not a server error.
                _logger.LogInformation(ex, "Request aborted by client. RequestId: {RequestId}", context.GetRequestId());
                if (!context.Response.HasStarted)
                    context.Response.StatusCode = ClientClosedRequestStatusCode;
            }
            catch (Exception ex)
            {
                if (context.Response.HasStarted)
                {
                    // Setting the status now would throw over the original error, and just returning lets the
                    // server end the response normally: the client would take a truncated body as complete.
                    _logger.LogError(ex, "Unhandled exception after response started. RequestId: {RequestId}", context.GetRequestId());
                    context.Abort();
                    return;
                }
                await HandleExceptionAsync(context, ex);
            }
        }

        async Task HandleExceptionAsync(HttpContext context, Exception exception)
        {
            string requestId = context.GetRequestId();
            ApiErrorMapping mapping = Transform(context, exception, Map(context, exception));

            LogLevel level = mapping.LogLevel ?? (mapping.StatusCode >= 500 ? LogLevel.Error : LogLevel.Warning);
            if (mapping.StatusCode >= 500 && mapping.LogLevel is null)
            {
                _logger.LogError(exception, "Unhandled exception. RequestId: {RequestId}", requestId);
            }
            else
            {
                _logger.Log(level, level >= LogLevel.Error ? exception : null,
                    "Request failed. RequestId: {RequestId}, Error: {ErrorCode}, Message: {Message}",
                    requestId, mapping.Code, mapping.Message);
            }

            ApiErrorResponse response = ApiErrorResponse.Create(
                requestId,
                new ApiError
                {
                    Code = mapping.Code,
                    Message = mapping.Message,
                    Details = mapping.Details,
                    Fields = mapping.Fields,
                    Retryable = mapping.Retryable,
                });

            // Drop whatever the failed action already set (Content-Disposition, caching, ETag...), they belong to the
            // success response; keep the request id header RequestIdMiddleware added so the client can still quote it.
            context.Response.Clear();
            if (context.Items.ContainsKey(HttpContextRequestIdExtensions.RequestIdItemKey))
                context.Response.Headers[_options.Value.RequestIdHeader] = requestId;
            context.Response.StatusCode = mapping.StatusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            if (mapping.Headers is not null)
            {
                foreach (KeyValuePair<string, string> header in mapping.Headers)
                    context.Response.Headers[header.Key] = header.Value;
            }
            try
            {
                await context.Response.WriteAsync(_options.Value.SerializeError(response), context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Client left while the error body was being written: nothing to report.
            }
        }

        ApiErrorMapping Transform(HttpContext context, Exception exception, ApiErrorMapping mapping)
        {
            var transform = _options.Value.TransformError;
            if (transform is null) return mapping;
            try
            {
                return transform(context, exception, mapping) ?? mapping;
            }
            catch (Exception transformException)
            {
                // Same rule as a broken mapper: never hide the original error.
                _logger.LogError(transformException, "TransformError threw while handling {ExceptionType}. RequestId: {RequestId}",
                    exception.GetType().FullName, context.GetRequestId());
                return mapping;
            }
        }

        ApiErrorMapping Map(HttpContext context, Exception exception)
        {
            if (exception is ApiErrorException api)
                return new ApiErrorMapping(api.StatusCode, api.Code, api.Message, api.Details);

            foreach (IApiExceptionMapper mapper in context.RequestServices.GetServices<IApiExceptionMapper>())
            {
                try
                {
                    ApiErrorMapping? mapping = mapper.Map(exception);
                    if (mapping is not null) return mapping;
                }
                catch (Exception mapperException)
                {
                    // A broken mapper must not hide the original error: fall back to the default 500 for it.
                    _logger.LogError(mapperException, "Exception mapper {Mapper} threw while mapping {ExceptionType}. RequestId: {RequestId}",
                        mapper.GetType().FullName, exception.GetType().FullName, context.GetRequestId());
                    break;
                }
            }

            return new ApiErrorMapping(StatusCodes.Status500InternalServerError, ApiErrorCodes.InternalError, DefaultErrorMessage);
        }
    }
}
