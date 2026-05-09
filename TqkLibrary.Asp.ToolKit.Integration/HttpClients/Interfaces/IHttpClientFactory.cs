namespace TqkLibrary.Asp.ToolKit.Integration.HttpClients.Interfaces
{
    public interface IHttpClientFactory<T> where T : class
    {
        HttpClient CreateClient();
    }
}
