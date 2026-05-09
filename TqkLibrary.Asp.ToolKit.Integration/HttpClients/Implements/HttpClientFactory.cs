using TqkLibrary.Asp.ToolKit.Integration.HttpClients.Interfaces;

namespace TqkLibrary.Asp.ToolKit.Integration.HttpClients.Implements
{
    class HttpClientFactory<T> : IHttpClientFactory<T> where T : class
    {
        readonly IHttpClientFactory _httpClientFactory;
        public HttpClientFactory(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }
        public HttpClient CreateClient()
        {
            return _httpClientFactory.CreateClient(typeof(T).FullName!);
        }
    }
}
