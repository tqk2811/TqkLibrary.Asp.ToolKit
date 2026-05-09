namespace TqkLibrary.Asp.ToolKit.Integration
{
    public sealed class DownloadFile : IDownloadFile
    {
        readonly HttpClient _httpClient;
        public DownloadFile(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }
        public async Task<Stream> DownloadFileAsync(string url, CancellationToken cancellationToken = default)
        {
            HttpResponseMessage httpResponseMessage = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            try
            {
                httpResponseMessage.EnsureSuccessStatusCode();
            }
            catch
            {
                httpResponseMessage.Dispose();
                throw;
            }
            Stream stream = await httpResponseMessage.Content.ReadAsStreamAsync();
            return new HttpClientStreamWrapper(httpResponseMessage, stream);
        }
    }
}
