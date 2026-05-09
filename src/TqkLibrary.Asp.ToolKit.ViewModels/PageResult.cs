namespace TqkLibrary.Asp.ToolKit.ViewModels
{
    public class PageResult<T>
    {
        public required int PageIndex { get; init; }
        public required int PageSize { get; init; }
        public required int TotalCount { get; init; }
        public required int TotalPages { get; init; }
        public required IReadOnlyList<T> Items { get; init; }
    }
}
