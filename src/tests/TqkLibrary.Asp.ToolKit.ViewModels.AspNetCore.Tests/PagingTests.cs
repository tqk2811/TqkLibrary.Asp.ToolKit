using Xunit;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Tests
{
    public class PagingTests
    {
        [Theory]
        [InlineData(0, 10, 0)]
        [InlineData(1, 10, 1)]
        [InlineData(10, 10, 1)]
        [InlineData(11, 10, 2)]
        [InlineData(25, 10, 3)]
        [InlineData(5, 0, 0)]
        [InlineData(5, -1, 0)]
        [InlineData(int.MaxValue, 1, int.MaxValue)]
        public void ApiResponse_Ok_Paged_ComputesTotalPages(int total, int pageSize, int expected)
        {
            ApiResponse<string[]> response = ApiResponse<string[]>.Ok([], "req", 1, pageSize, total);

            Assert.Equal("req", response.RequestId);
            Assert.NotNull(response.Meta);
            Assert.Equal(1, response.Meta!.Page);
            Assert.Equal(pageSize, response.Meta.PageSize);
            Assert.Equal(total, response.Meta.Total);
            Assert.Equal(expected, response.Meta.TotalPages);
        }

        [Theory]
        [InlineData(0, 10, 0)]
        [InlineData(21, 10, 3)]
        [InlineData(7, 0, 0)]
        public void PagedResult_TotalPages(int total, int pageSize, int expected)
        {
            PagedResult<int> result = PagedResult<int>.Create([1, 2], 2, pageSize, total);

            Assert.Equal(expected, result.TotalPages);
            Assert.Equal(2, result.Page);
            Assert.Equal([1, 2], result.Items);
        }

        [Fact]
        public void ApiResponse_Ok_WithoutPaging_HasNoMeta()
        {
            ApiResponse<int> response = ApiResponse<int>.Ok(5, "req");
            Assert.Equal(5, response.Data);
            Assert.Null(response.Meta);
        }
    }
}
