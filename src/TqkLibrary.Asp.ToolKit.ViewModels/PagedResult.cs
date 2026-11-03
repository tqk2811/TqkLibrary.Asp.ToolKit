namespace TqkLibrary.Asp.ToolKit.ViewModels
{
    /// <summary>One page of items plus the paging numbers.</summary>
    public sealed class PagedResult<T>
    {
        public IReadOnlyList<T> Items { get; init; } = [];
        public int Page { get; init; }
        public int PageSize { get; init; }
        public int Total { get; init; }

        /// <summary>0 when <see cref="PageSize"/> is not positive.</summary>
        public int TotalPages => Paging.TotalPages(Total, PageSize);

        public static PagedResult<T> Create(IReadOnlyList<T> items, int page, int pageSize, int total)
            => new() { Items = items, Page = page, PageSize = pageSize, Total = total };
    }
}
