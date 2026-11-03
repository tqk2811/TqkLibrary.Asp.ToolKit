using System.Net;
using System.Text;
using TqkLibrary.Asp.ToolKit.Integration.Extensions;
using TqkLibrary.Asp.ToolKit.ViewModels;
using TqkLibrary.Http.Exceptions;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Integration.Tests
{
    public class RequestBuilderApiExtensionsTests
    {
        sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(status)
                {
                    RequestMessage = request,
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
        }

        public sealed class ProductDto
        {
            public string Name { get; set; } = string.Empty;
        }

        sealed class TestApi(HttpClient httpClient) : ApiBase(httpClient)
        {
            public Task<ProductDto> GetAsync() => Build().WithUrlGet("api/products/1").ExecuteApiAsync<ProductDto>();
            public Task<ApiResponse<List<ProductDto>>> ListAsync() => Build().WithUrlGet("api/products").ExecuteApiResponseAsync<List<ProductDto>>();
            public Task DeleteAsync() => Build().WithUrl("api/products/1", HttpMethod.Delete).ExecuteApiVoidAsync();
        }

        static TestApi Create(HttpStatusCode status, string body)
            => new(new HttpClient(new FakeHandler(status, body)) { BaseAddress = new Uri("http://localhost/") });

        [Fact]
        public async Task ExecuteApiAsync_UnwrapsData()
        {
            TestApi api = Create(HttpStatusCode.OK, """{"requestId":"req_1","data":{"name":"a"}}""");
            ProductDto product = await api.GetAsync();
            Assert.Equal("a", product.Name);
            Assert.Equal(new Uri("http://localhost/"), api.BaseAddress);
        }

        [Fact]
        public async Task ExecuteApiAsync_MissingData_Throws()
        {
            TestApi api = Create(HttpStatusCode.OK, """{"requestId":"req_1"}""");
            await Assert.ThrowsAsync<ApiException>(api.GetAsync);
        }

        [Fact]
        public async Task ExecuteApiResponseAsync_KeepsMeta()
        {
            TestApi api = Create(HttpStatusCode.OK,
                """{"requestId":"req_2","data":[{"name":"a"}],"meta":{"page":1,"pageSize":10,"total":11,"totalPages":2}}""");
            ApiResponse<List<ProductDto>> response = await api.ListAsync();
            Assert.Equal("req_2", response.RequestId);
            Assert.Single(response.Data!);
            Assert.Equal(2, response.Meta!.TotalPages);
        }

        [Fact]
        public async Task ErrorResponse_ThrowsApiExceptionWithApiErrorResponse()
        {
            TestApi api = Create(HttpStatusCode.NotFound,
                """{"requestId":"req_3","error":{"code":"PRODUCT_NOT_FOUND","message":"nope","fields":[{"field":"name","code":"FIELD_REQUIRED","message":"m"}]}}""");

            ApiException<ApiErrorResponse> ex = await Assert.ThrowsAsync<ApiException<ApiErrorResponse>>(api.GetAsync);
            Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
            Assert.Equal("req_3", ex.Body!.RequestId);
            Assert.Equal("PRODUCT_NOT_FOUND", ex.Body.Error.Code);
            Assert.Equal("m", ex.Body.Error.Fields![0].Message);
        }

        [Fact]
        public async Task ExecuteApiVoidAsync_ErrorResponse_Throws()
        {
            TestApi api = Create(HttpStatusCode.Conflict, """{"requestId":"req_4","error":{"code":"CONFLICT","message":"c"}}""");
            ApiException<ApiErrorResponse> ex = await Assert.ThrowsAsync<ApiException<ApiErrorResponse>>(api.DeleteAsync);
            Assert.Equal("CONFLICT", ex.Body!.Error.Code);
        }

        [Fact]
        public async Task ExecuteApiVoidAsync_Success()
        {
            TestApi api = Create(HttpStatusCode.OK, """{"requestId":"req_5","data":null}""");
            await api.DeleteAsync();
        }
    }
}
