using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Extensions;
using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Interfaces;
using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Models;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Tests
{
    /// <summary>TestServer host running the whole envelope pipeline over <see cref="EnvelopeTestController"/>.</summary>
    internal sealed class TestApp : IAsyncDisposable
    {
        readonly IHost _host;

        TestApp(IHost host)
        {
            _host = host;
            Client = host.GetTestClient();
        }

        public HttpClient Client { get; }

        public IServiceProvider Services => _host.Services;

        public static async Task<TestApp> StartAsync(Action<IServiceCollection>? configureServices = null, Action<TqkApiEnvelopeOptions>? configure = null)
        {
            IHost host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddControllers()
                            .AddApplicationPart(typeof(EnvelopeTestController).Assembly)
                            .AddTqkApiEnvelope(configure);
                        configureServices?.Invoke(services);
                    })
                    .Configure(app =>
                    {
                        app.UseTqkRequestId();
                        // Stands for a security-headers middleware set before the wrapper.
                        app.Use((context, next) =>
                        {
                            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                            return next(context);
                        });
                        app.UseTqkApiExceptionWrapper();
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
                    }))
                .Build();
            await host.StartAsync();
            return new TestApp(host);
        }

        public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
        {
            string text = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    public sealed class ProductDto
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class CreateProductModel
    {
        [Required]
        public string? DisplayName { get; set; }

        [StringLength(5)]
        public string? Code { get; set; }

        [JsonPropertyName("unit_price")]
        [Range(1, 10)]
        public int UnitPrice { get; set; } = 1;

        public List<CreateProductItem>? Items { get; set; }

        [StringLength(8, MinimumLength = 3)]
        public string? Slug { get; set; }

        [MaxLength(2, ErrorMessage = "Too many tags for {0}.")]
        public List<string>? Tags { get; set; }
    }

    public sealed class CreateProductItem
    {
        [Required]
        public string? ItemName { get; set; }
    }

    public sealed class KeyNotFoundMapper : IApiExceptionMapper
    {
        public ApiErrorMapping? Map(HttpContext context, Exception exception)
            => exception is KeyNotFoundException ? new ApiErrorMapping(404, "KEY_NOT_FOUND", exception.Message) : null;
    }

    public sealed class RichMapper : IApiExceptionMapper
    {
        public ApiErrorMapping? Map(HttpContext context, Exception exception)
            => exception is KeyNotFoundException
                ? new ApiErrorMapping(429, "SLOW_DOWN", exception.Message)
                {
                    Fields = new List<FieldError> { new FieldError { Field = "name", Code = ApiErrorCodes.FieldTooLong } },
                    Retryable = true,
                    Headers = new Dictionary<string, StringValues> { ["Retry-After"] = "7" },
                    LogLevel = Microsoft.Extensions.Logging.LogLevel.Information,
                }
                : null;
    }

    public sealed class ThrowingMapper : IApiExceptionMapper
    {
        public ApiErrorMapping? Map(HttpContext context, Exception exception) => throw new InvalidOperationException("mapper bug");
    }

    /// <summary>Echoes the request path into the message: proves the mapper gets the current HttpContext.</summary>
    public sealed class PathAwareMapper : IApiExceptionMapper
    {
        public ApiErrorMapping? Map(HttpContext context, Exception exception)
            => exception is KeyNotFoundException ? new ApiErrorMapping(404, "KEY_NOT_FOUND", "at " + context.Request.Path) : null;
    }

    public sealed class DelegateTransformer : IApiErrorTransformer
    {
        readonly Func<HttpContext, Exception, ApiErrorMapping, ApiErrorMapping?> _transform;
        public DelegateTransformer(Func<HttpContext, Exception, ApiErrorMapping, ApiErrorMapping?> transform) => _transform = transform;
        public ApiErrorMapping? Transform(HttpContext context, Exception exception, ApiErrorMapping mapping) => _transform(context, exception, mapping);
    }

    [ApiController]
    [Route("t")]
    public sealed class EnvelopeTestController : ControllerBase
    {
        [HttpGet("plain")]
        public ProductDto Plain() => new() { Name = "a" };

        [HttpPost("created")]
        public IActionResult CreatedProduct() => CreatedAtAction(nameof(Plain), new ProductDto { Name = "new" });

        [HttpPost("accepted")]
        public IActionResult AcceptedProduct() => Accepted(new ProductDto { Name = "queued" });

        [HttpGet("void")]
        public Task Void() => Task.CompletedTask;

        [HttpGet("enveloped")]
        public ApiResponse<int[]> Enveloped() => ApiResponse<int[]>.Ok([1, 2], "custom", 1, 2, 5);

        [HttpGet("raw")]
        [RawResponse]
        public ProductDto Raw() => new() { Name = "raw" };

        [HttpGet("api-error")]
        public ProductDto ApiError() => throw ApiErrorException.NotFound("PRODUCT_NOT_FOUND", "Product not found.");

        [HttpGet("mapped")]
        public ProductDto Mapped() => throw new KeyNotFoundException("missing key");

        [HttpGet("reset")]
        public ProductDto Reset() => throw new Microsoft.AspNetCore.Connections.ConnectionResetException("peer reset");

        [HttpGet("download-fail")]
        public ProductDto DownloadFail()
        {
            Response.Headers["Content-Disposition"] = "attachment; filename=report.csv";
            Response.Headers["ETag"] = "\"v1\"";
            throw ApiErrorException.NotFound("REPORT_NOT_FOUND", "Report not found.");
        }

        [HttpGet("crash")]
        public ProductDto Crash() => throw new InvalidOperationException("secret internal detail");

        [HttpPost("validate")]
        public ProductDto Validate(CreateProductModel model) => new() { Name = model.DisplayName ?? string.Empty };

        [HttpGet("number")]
        public ProductDto Number([FromQuery, Microsoft.AspNetCore.Mvc.ModelBinding.BindRequired] int value) => new() { Name = value.ToString() };
    }
}
