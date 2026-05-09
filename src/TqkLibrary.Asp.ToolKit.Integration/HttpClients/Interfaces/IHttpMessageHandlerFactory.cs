namespace TqkLibrary.Asp.ToolKit.Integration.HttpClients.Interfaces
{
    public interface IHttpMessageHandlerFactory<T> : IHttpMessageHandlerFactory
        where T : class
    {
        HttpMessageHandler CreateHandler();
    }
}
