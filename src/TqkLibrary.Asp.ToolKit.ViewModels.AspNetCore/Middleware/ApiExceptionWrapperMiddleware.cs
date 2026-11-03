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
            catch (OperationCanceledException ex) when (context.RequestAborted.IsCancellationRequested)
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
            ApiErrorMapping mapping = Map(context, exception);

            if (mapping.StatusCode >= 500)
            {
                _logger.LogError(exception, "Unhandled exception. RequestId: {RequestId}", requestId);
            }
            else
            {
                _logger.LogWarning("Request failed. RequestId: {RequestId}, Error: {ErrorCode}, Message: {Message}",
                    requestId, mapping.Code, mapping.Message);
            }

            ApiErrorResponse response = ApiErrorResponse.Create(
                requestId,
                ApiError.Create(mapping.Code, mapping.Message, mapping.Details));

            context.Response.StatusCode = mapping.StatusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            try
            {
                await context.Response.WriteAsync(_options.Value.SerializeError(response), context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Client left while the error body was being written: nothing to report.
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
