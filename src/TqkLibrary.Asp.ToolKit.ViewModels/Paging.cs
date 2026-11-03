namespace TqkLibrary.Asp.ToolKit.ViewModels
{
    internal static class Paging
    {
        /// <summary>Number of pages; 0 when <paramref name="pageSize"/> is not positive or <paramref name="total"/> is negative.</summary>
        public static int TotalPages(int total, int pageSize)
        {
            if (pageSize <= 0 || total <= 0) return 0;
            return (int)((total + (long)pageSize - 1) / pageSize);
        }
    }
}
