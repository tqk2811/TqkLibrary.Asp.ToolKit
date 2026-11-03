namespace TqkLibrary.Asp.ToolKit.ViewModels
{
    /// <summary>Paging information of an <see cref="ApiResponse{T}"/>.</summary>
    public sealed class ApiResponseMetadata
    {
        public int Page { get; init; }
        public int PageSize { get; init; }
        public int Total { get; init; }
        public int TotalPages { get; init; }

        /// <summary>Computes <see cref="TotalPages"/>; 0 when <paramref name="pageSize"/> is not positive.</summary>
        public static ApiResponseMetadata Create(int page, int pageSize, int total)
            => new()
            {
                Page = page,
                PageSize = pageSize,
                Total = total,
                TotalPages = Paging.TotalPages(total, pageSize),
            };
    }
}
