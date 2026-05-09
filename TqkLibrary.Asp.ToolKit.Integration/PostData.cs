using TqkLibrary.Http;

namespace TqkLibrary.Asp.ToolKit.Integration
{
    public sealed class PostData : BaseApi, IPostData
    {
        public PostData(HttpClient httpClient) : base(httpClient)
        {
        }


        public async Task PostAsJsonAsync<TData>(string url, TData data, CancellationToken cancellationToken = default)
        {
            using var res = await Build()
                .WithUrlPostJson(url, data!)
                .ExecuteAsync(cancellationToken);
            string body = await res.Content.ReadAsStringAsync();
            res.EnsureSuccessStatusCode();
        }

        public Task<TResult> PostAsJsonAsync<TData, TResult>(string url, TData data, CancellationToken cancellationToken = default) where TResult : class
        {
            return Build()
                .WithUrlPostJson(url, data!)
                .ExecuteAsync<TResult>(cancellationToken);
        }
    }
}
