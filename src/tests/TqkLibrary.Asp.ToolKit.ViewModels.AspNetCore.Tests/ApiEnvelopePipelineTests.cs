using System.Net;
using System.Text;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Interfaces;
using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Middleware;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Tests
{
    public class ApiEnvelopePipelineTests
    {
        [Fact]
        public async Task Filter_WrapsPlainResult()
        {
            await using TestApp app = await TestApp.StartAsync();
            using HttpResponseMessage response = await app.Client.GetAsync("/t/plain");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            JsonElement json = await TestApp.ReadJsonAsync(response);
            Assert.Equal("a", json.GetProperty("data").GetProperty("name").GetString());
            Assert.StartsWith("req_", json.GetProperty("requestId").GetString());
        }

        [Fact]
        public async Task Filter_WrapsVoidAction_WithNullData()
        {
            await using TestApp app = await TestApp.StartAsync();
            using HttpResponseMessage response = await app.Client.GetAsync("/t/void");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            JsonElement json = await TestApp.ReadJsonAsync(response);
            Assert.True(json.TryGetProperty("requestId", out _));
            Assert.True(!json.TryGetProperty("data", out JsonElement data) || data.ValueKind == JsonValueKind.Null);
        }

        [Fact]
        public async Task Filter_SkipsAlreadyEnveloped()
        {
            await using TestApp app = await TestApp.StartAsync();
            using HttpResponseMessage response = await app.Client.GetAsync("/t/enveloped");

            JsonElement json = await TestApp.ReadJsonAsync(response);
            Assert.Equal("custom", json.GetProperty("requestId").GetString());
            Assert.Equal(JsonValueKind.Array, json.GetProperty("data").ValueKind);
            Assert.Equal(3, json.GetProperty("meta").GetProperty("totalPages").GetInt32());
        }

        [Fact]
        public async Task Filter_SkipsRawResponse()
        {
            await using TestApp app = await TestApp.StartAsync();
            using HttpResponseMessage response = await app.Client.GetAsync("/t/raw");

            JsonElement json = await TestApp.ReadJsonAsync(response);
            Assert.Equal("raw", json.GetProperty("name").GetString());
            Assert.False(json.TryGetProperty("data", out _));
        }

        [Fact]
        public async Task RequestId_IncomingHeader_IsEchoedAndUsed()
        {
            await using TestApp app = await TestApp.StartAsync();
            using HttpRequestMessage request = new(HttpMethod.Get, "/t/plain");
            request.Headers.Add("X-Request-Id", "client-123");
            using HttpResponseMessage response = await app.Client.SendAsync(request);

            Assert.Equal("client-123", response.Headers.GetValues("X-Request-Id").Single());
            JsonElement json = await TestApp.ReadJsonAsync(response);
            Assert.Equal("client-123", json.GetProperty("requestId").GetString());
        }

        [Fact]
        public async Task RequestId_Generated_UsesOptions()
        {
            await using TestApp app = await TestApp.StartAsync(configure: x =>
            {
                x.RequestIdHeader = "X-Correlation-Id";
                x.RequestIdPrefix = "abc_";
            });
            using HttpResponseMessage response = await app.Client.GetAsync("/t/plain");

            string header = response.Headers.GetValues("X-Correlation-Id").Single();
            Assert.StartsWith("abc_", header);
            Assert.Equal(4 + 32, header.Length);
            JsonElement json = await TestApp.ReadJsonAsync(response);
            Assert.Equal(header, json.GetProperty("requestId").GetString());
        }

        [Fact]
        public async Task ExceptionWrapper_ApiErrorException_UsesItsStatusAndCode()
        {
            await using TestApp app = await TestApp.StartAsync();
            using HttpResponseMessage response = await app.Client.GetAsync("/t/api-error");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            JsonElement json = await TestApp.ReadJsonAsync(response);
            Assert.Equal(response.Headers.GetValues("X-Request-Id").Single(), json.GetProperty("requestId").GetString());
            JsonElement error = json.GetProperty("error");
            Assert.Equal("PRODUCT_NOT_FOUND", error.GetProperty("code").GetString());
            Assert.Equal("Product not found.", error.GetProperty("message").GetString());
            // Nulls are not written.
            Assert.False(error.TryGetProperty("fields", out _));
            Assert.False(error.TryGetProperty("retryable", out _));
        }

        [Fact]
        public async Task ExceptionWrapper_DropsHeadersSetBeforeThrow_KeepsRequestId()
        {
            await using TestApp app = await TestApp.StartAsync();
            using HttpRequestMessage request = new(HttpMethod.Get, "/t/download-fail");
            request.Headers.Add("X-Request-Id", "req-clear-1");
            using HttpResponseMessage response = await app.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Null(response.Content.Headers.ContentDisposition);
            Assert.False(response.Headers.Contains("ETag"));
            Assert.Equal("req-clear-1", response.Headers.GetValues("X-Request-Id").Single());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            JsonElement json = await TestApp.ReadJsonAsync(response);
            Assert.Equal("req-clear-1", json.GetProperty("requestId").GetString());
        }

        [Fact]
        public async Task ExceptionWrapper_UsesRegisteredMapper()
        {
            await using TestApp app = await TestApp.StartAsync(services => services.AddSingleton<IApiExceptionMapper, KeyNotFoundMapper>());
            using HttpResponseMessage response = await app.Client.GetAsync("/t/mapped");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            JsonElement json = await TestApp.ReadJsonAsync(response);
            Assert.Equal("KEY_NOT_FOUND", json.GetProperty("error").GetProperty("code").GetString());
        }

        [Fact]
        public async Task ExceptionWrapper_MapperExtras_WriteFieldsRetryableAndHeaders()
        {
            await using TestApp app = await TestApp.StartAsync(services => services.AddSingleton<IApiExceptionMapper, RichMapper>());
            using HttpResponseMessage response = await app.Client.GetAsync("/t/mapped");

            Assert.Equal((HttpStatusCode)429, response.StatusCode);
            Assert.Equal("7", Assert.Single(response.Headers.GetValues("Retry-After")));
            JsonElement error = (await TestApp.ReadJsonAsync(response)).GetProperty("error");
            Assert.True(error.GetProperty("retryable").GetBoolean());
            JsonElement field = Assert.Single(error.GetProperty("fields").EnumerateArray());
            Assert.Equal("name", field.GetProperty("field").GetString());
            Assert.Equal(ApiErrorCodes.FieldTooLong, field.GetProperty("code").GetString());
        }

        [Fact]
        public async Task ExceptionWrapper_Transformer_AppliesToApiErrorException()
        {
            await using TestApp app = await TestApp.StartAsync(services => services.AddSingleton<IApiErrorTransformer>(
                new DelegateTransformer((_, _, m) => m.Code == "PRODUCT_NOT_FOUND"
                    ? m with { Retryable = false, Headers = new Dictionary<string, StringValues> { ["X-Hint"] = new[] { "reload", "retry" } } }
                    : m)));
            using HttpResponseMessage response = await app.Client.GetAsync("/t/api-error");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(new[] { "reload", "retry" }, response.Headers.GetValues("X-Hint"));
            JsonElement error = (await TestApp.ReadJsonAsync(response)).GetProperty("error");
            Assert.Equal("PRODUCT_NOT_FOUND", error.GetProperty("code").GetString());
            Assert.False(error.GetProperty("retryable").GetBoolean());
        }

        [Fact]
        public async Task ExceptionWrapper_Transformers_RunInOrder_NullKeepsMapping_ThrowingIsSkipped()
        {
            await using TestApp app = await TestApp.StartAsync(services => services
                .AddSingleton<IApiErrorTransformer>(new DelegateTransformer((_, _, m) => m with { Message = m.Message + " [1]" }))
                .AddSingleton<IApiErrorTransformer>(new DelegateTransformer((_, _, _) => throw new InvalidOperationException("transform bug")))
                .AddSingleton<IApiErrorTransformer>(new DelegateTransformer((_, _, _) => null))
                .AddSingleton<IApiErrorTransformer>(new DelegateTransformer((_, _, m) => m with { Message = m.Message + " [2]" })));
            using HttpResponseMessage response = await app.Client.GetAsync("/t/api-error");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            string text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("transform bug", text);
            JsonElement error = JsonDocument.Parse(text).RootElement.GetProperty("error");
            Assert.Equal("PRODUCT_NOT_FOUND", error.GetProperty("code").GetString());
            Assert.EndsWith(" [1] [2]", error.GetProperty("message").GetString());
        }

        [Fact]
        public void WithHeader_ReplacesWhateverCase_KeepsOthers()
        {
            Models.ApiErrorMapping mapping = new Models.ApiErrorMapping(401, "X", "x")
                .WithHeader("www-authenticate", "old")
                .WithHeader("X-Other", new StringValues(new[] { "a", "b" }))
                .WithHeader("WWW-Authenticate", "new");

            Assert.Equal(2, mapping.Headers!.Count);
            Assert.Equal("new", mapping.Headers["WWW-AUTHENTICATE"].ToString());
            Assert.Equal(new[] { "a", "b" }, mapping.Headers["x-other"].ToArray());
        }

        [Fact]
        public async Task ExceptionWrapper_Mapper_ReceivesHttpContext()
        {
            await using TestApp app = await TestApp.StartAsync(services => services.AddSingleton<IApiExceptionMapper, PathAwareMapper>());
            using HttpResponseMessage response = await app.Client.GetAsync("/t/mapped");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            JsonElement error = (await TestApp.ReadJsonAsync(response)).GetProperty("error");
            Assert.Equal("at /t/mapped", error.GetProperty("message").GetString());
        }

        [Fact]
        public async Task ExceptionWrapper_ConnectionReset_Is499WithoutBody()
        {
            await using TestApp app = await TestApp.StartAsync();
            using HttpResponseMessage response = await app.Client.GetAsync("/t/reset");

            Assert.Equal((HttpStatusCode)499, response.StatusCode);
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task ExceptionWrapper_ThrowingMapper_FallsBackToInternalError()
        {
            await using TestApp app = await TestApp.StartAsync(services => services.AddSingleton<IApiExceptionMapper, ThrowingMapper>());
            using HttpResponseMessage response = await app.Client.GetAsync("/t/crash");

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            string text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("mapper bug", text);
            JsonElement json = JsonDocument.Parse(text).RootElement;
            Assert.Equal(ApiErrorCodes.InternalError, json.GetProperty("error").GetProperty("code").GetString());
        }

        [Fact]
        public async Task Filter_CreatedAtAction_KeepsStatusAndLocation()
        {
            await using TestApp app = await TestApp.StartAsync();
            using HttpResponseMessage response = await app.Client.PostAsync("/t/created", null);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.NotNull(response.Headers.Location);
            Assert.EndsWith("/t/plain", response.Headers.Location!.ToString());
            JsonElement json = await TestApp.ReadJsonAsync(response);
            Assert.Equal("new", json.GetProperty("data").GetProperty("name").GetString());
            Assert.True(json.TryGetProperty("requestId", out _));
        }

        [Fact]
        public async Task Filter_Accepted_KeepsStatus()
        {
            await using TestApp app = await TestApp.StartAsync();
            using HttpResponseMessage response = await app.Client.PostAsync("/t/accepted", null);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            JsonElement json = await TestApp.ReadJsonAsync(response);
            Assert.Equal("queued", json.GetProperty("data").GetProperty("name").GetString());
        }

        [Fact]
        public void RequestId_EmptyHeaderOption_ThrowsAtConstruction()
        {
            Assert.Throws<InvalidOperationException>(() => new RequestIdMiddleware(
                _ => Task.CompletedTask,
                Options.Create(new TqkApiEnvelopeOptions { RequestIdHeader = " " })));
        }

        [Fact]
        public async Task ExceptionWrapper_Default_Is500WithoutDetails()
        {
            await using TestApp app = await TestApp.StartAsync();
            using HttpResponseMessage response = await app.Client.GetAsync("/t/crash");

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            string text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("secret", text);
            JsonElement json = JsonDocument.Parse(text).RootElement;
            Assert.Equal(ApiErrorCodes.InternalError, json.GetProperty("error").GetProperty("code").GetString());
        }

        [Fact]
        public async Task ExceptionWrapper_ClientAbort_Is499WithoutBody()
        {
            using CancellationTokenSource cts = new();
            cts.Cancel();
            DefaultHttpContext context = new() { RequestAborted = cts.Token };
            context.Response.Body = new MemoryStream();
            ApiExceptionWrapperMiddleware middleware = new(
                _ => throw new OperationCanceledException(cts.Token),
                NullLogger<ApiExceptionWrapperMiddleware>.Instance,
                Options.Create(new TqkApiEnvelopeOptions()));

            await middleware.InvokeAsync(context);

            Assert.Equal(ApiExceptionWrapperMiddleware.ClientClosedRequestStatusCode, context.Response.StatusCode);
            Assert.Equal(0, context.Response.Body.Length);
        }

        [Fact]
        public async Task ExceptionWrapper_CancelWithoutAbort_Is500()
        {
            DefaultHttpContext context = new() { RequestServices = new ServiceCollection().BuildServiceProvider() };
            context.Response.Body = new MemoryStream();
            ApiExceptionWrapperMiddleware middleware = new(
                _ => throw new OperationCanceledException(),
                NullLogger<ApiExceptionWrapperMiddleware>.Instance,
                Options.Create(new TqkApiEnvelopeOptions()));

            await middleware.InvokeAsync(context);

            Assert.Equal(500, context.Response.StatusCode);
        }

        [Fact]
        public async Task ExceptionWrapper_IOExceptionWithoutAbort_Is500AndLoggedAsError()
        {
            DefaultHttpContext context = new() { RequestServices = new ServiceCollection().BuildServiceProvider() };
            context.Response.Body = new MemoryStream();
            CapturingLogger logger = new();
            ApiExceptionWrapperMiddleware middleware = new(
                _ => throw new IOException("disk full"),
                logger,
                Options.Create(new TqkApiEnvelopeOptions()));

            await middleware.InvokeAsync(context);

            Assert.Equal(500, context.Response.StatusCode);
            Assert.True(context.Response.Body.Length > 0);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is IOException);
        }

        [Fact]
        public async Task ExceptionWrapper_5xxLog_SameTemplateWithOrWithoutLogLevel()
        {
            async Task<CapturingLogger.Entry> RunAsync(LogLevel? level)
            {
                DefaultHttpContext context = new() { RequestServices = new ServiceCollection().BuildServiceProvider() };
                context.Response.Body = new MemoryStream();
                context.RequestServices = new ServiceCollection()
                    .AddSingleton<IApiErrorTransformer>(new DelegateTransformer((_, _, m) => m with { LogLevel = level }))
                    .BuildServiceProvider();
                CapturingLogger logger = new();
                ApiExceptionWrapperMiddleware middleware = new(
                    _ => throw new InvalidOperationException("boom"),
                    logger,
                    Options.Create(new TqkApiEnvelopeOptions()));
                await middleware.InvokeAsync(context);
                return Assert.Single(logger.Entries);
            }

            CapturingLogger.Entry implicitLevel = await RunAsync(null);
            CapturingLogger.Entry explicitLevel = await RunAsync(LogLevel.Critical);

            Assert.Equal(LogLevel.Error, implicitLevel.Level);
            Assert.Equal(LogLevel.Critical, explicitLevel.Level);
            Assert.Equal(implicitLevel.Template, explicitLevel.Template);
            Assert.Contains("{ErrorCode}", implicitLevel.Template);
            Assert.IsType<InvalidOperationException>(implicitLevel.Exception);
        }

        sealed class CapturingLogger : ILogger<ApiExceptionWrapperMiddleware>
        {
            public sealed record Entry(LogLevel Level, Exception? Exception, string? Template);
            public List<Entry> Entries { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                string? template = (state as IEnumerable<KeyValuePair<string, object?>>)?
                    .FirstOrDefault(p => p.Key == "{OriginalFormat}").Value as string;
                Entries.Add(new Entry(logLevel, exception, template));
            }
        }

        [Fact]
        public async Task Validation_ReturnsFieldErrors_WithClientPathsAndMessages()
        {
            await using TestApp app = await TestApp.StartAsync();
            const string body = """{"code":"toolong","unit_price":50,"items":[{"itemName":null}],"tags":["a","b","c"]}""";
            using HttpResponseMessage response = await app.Client.PostAsync("/t/validate", new StringContent(body, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            JsonElement json = await TestApp.ReadJsonAsync(response);
            JsonElement error = json.GetProperty("error");
            Assert.Equal(ApiErrorCodes.ValidationFailed, error.GetProperty("code").GetString());
            Dictionary<string, JsonElement> fields = error.GetProperty("fields").EnumerateArray()
                .ToDictionary(x => x.GetProperty("field").GetString()!);

            Assert.Equal(ApiErrorCodes.FieldRequired, fields["displayName"].GetProperty("code").GetString());
            Assert.Equal("The DisplayName field is required.", fields["displayName"].GetProperty("message").GetString());

            Assert.Equal(ApiErrorCodes.FieldTooLong, fields["code"].GetProperty("code").GetString());
            Assert.Equal(5, fields["code"].GetProperty("params").GetProperty("max").GetInt32());
            Assert.Contains("Code", fields["code"].GetProperty("message").GetString());

            Assert.Equal(ApiErrorCodes.FieldOutOfRange, fields["unit_price"].GetProperty("code").GetString());
            Assert.Equal(10, fields["unit_price"].GetProperty("params").GetProperty("max").GetInt32());

            Assert.Equal(ApiErrorCodes.FieldRequired, fields["items[0].itemName"].GetProperty("code").GetString());

            // The attribute's own ErrorMessage is kept and still formatted, the code comes from the attribute type.
            Assert.Equal(ApiErrorCodes.FieldTooLong, fields["tags"].GetProperty("code").GetString());
            Assert.Equal(2, fields["tags"].GetProperty("params").GetProperty("max").GetInt32());
            Assert.Equal("Too many tags for Tags.", fields["tags"].GetProperty("message").GetString());
        }

        [Fact]
        public async Task Validation_FieldCodes_DoNotRewriteAttributeMessages()
        {
            await using TestApp app = await TestApp.StartAsync();
            using HttpResponseMessage response = await app.Client.PostAsync("/t/validate", new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            // The cached metadata MVC validates with (and Razor / client-side validation read) keeps the original messages.
            IModelMetadataProvider provider = app.Services.GetRequiredService<IModelMetadataProvider>();
            ModelMetadata property = provider.GetMetadataForProperty(typeof(CreateProductModel), nameof(CreateProductModel.DisplayName));
            RequiredAttribute required = property.ValidatorMetadata.OfType<RequiredAttribute>().Single();
            Assert.Null(required.ErrorMessage);
            Assert.Equal("The DisplayName field is required.", required.FormatErrorMessage("DisplayName"));

            ModelMetadata tags = provider.GetMetadataForProperty(typeof(CreateProductModel), nameof(CreateProductModel.Tags));
            Assert.Equal("Too many tags for {0}.", tags.ValidatorMetadata.OfType<MaxLengthAttribute>().Single().ErrorMessage);
        }

        [Fact]
        public async Task Validation_BindingError_KeepsMvcMessageWithCode()
        {
            await using TestApp app = await TestApp.StartAsync();
            using HttpResponseMessage response = await app.Client.GetAsync("/t/number");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            JsonElement json = await TestApp.ReadJsonAsync(response);
            JsonElement field = json.GetProperty("error").GetProperty("fields").EnumerateArray().Single();
            Assert.Equal("value", field.GetProperty("field").GetString());
            Assert.Equal(ApiErrorCodes.FieldRequired, field.GetProperty("code").GetString());
            Assert.Equal("A value for the 'value' parameter or property was not provided.", field.GetProperty("message").GetString());
        }

        [Fact]
        public async Task Validation_AppBindingMessageSetAfterEnvelope_KeepsTextAndCode()
        {
            await using TestApp app = await TestApp.StartAsync(services => services.Configure<MvcOptions>(
                o => o.ModelBindingMessageProvider.SetMissingBindRequiredValueAccessor(x => $"Thiếu {x}.")));
            using HttpResponseMessage response = await app.Client.GetAsync("/t/number");

            JsonElement json = await TestApp.ReadJsonAsync(response);
            JsonElement field = json.GetProperty("error").GetProperty("fields").EnumerateArray().Single();
            Assert.Equal(ApiErrorCodes.FieldRequired, field.GetProperty("code").GetString());
            Assert.Equal("Thiếu value.", field.GetProperty("message").GetString());
        }

        [Fact]
        public async Task Validation_StringLengthWithMinimum_IsOutOfRange()
        {
            await using TestApp app = await TestApp.StartAsync();
            const string body = """{"displayName":"x","slug":"a"}""";
            using HttpResponseMessage response = await app.Client.PostAsync("/t/validate", new StringContent(body, Encoding.UTF8, "application/json"));

            JsonElement json = await TestApp.ReadJsonAsync(response);
            JsonElement field = json.GetProperty("error").GetProperty("fields").EnumerateArray()
                .Single(x => x.GetProperty("field").GetString() == "slug");
            Assert.Equal(ApiErrorCodes.FieldOutOfRange, field.GetProperty("code").GetString());
            Assert.Equal(3, field.GetProperty("params").GetProperty("min").GetInt32());
            Assert.Equal(8, field.GetProperty("params").GetProperty("max").GetInt32());
        }

        [Fact]
        public async Task Validation_FieldCodesOff_KeepsOriginalMessage()
        {
            await using TestApp app = await TestApp.StartAsync(configure: x => x.UseFieldErrorCodes = false);
            using HttpResponseMessage response = await app.Client.PostAsync("/t/validate", new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            JsonElement json = await TestApp.ReadJsonAsync(response);
            JsonElement field = json.GetProperty("error").GetProperty("fields").EnumerateArray()
                .Single(x => x.GetProperty("field").GetString() == "displayName");
            Assert.Equal(ApiErrorCodes.FieldInvalidValue, field.GetProperty("code").GetString());
            Assert.Equal("The DisplayName field is required.", field.GetProperty("message").GetString());
        }
    }
}
