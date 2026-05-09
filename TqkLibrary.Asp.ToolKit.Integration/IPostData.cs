namespace TqkLibrary.Asp.ToolKit.Integration
{
    public interface IPostData
    {
        Task PostAsJsonAsync<TData>(string url, TData data, CancellationToken cancellationToken = default);
        Task<TResult> PostAsJsonAsync<TData, TResult>(string url, TData data, CancellationToken cancellationToken = default) where TResult : class;
    }
}
