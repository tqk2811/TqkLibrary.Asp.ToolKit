using TqkLibrary.Asp.ToolKit.Integration.HttpClients.Interfaces;

namespace TqkLibrary.Asp.ToolKit.Integration.HttpClients.Implements
{
    class HttpMessageHandlerFactory<T> : IHttpMessageHandlerFactory<T> where T : class
    {
        readonly IHttpMessageHandlerFactory _httpMessageHandlerFactory;
        public HttpMessageHandlerFactory(IHttpMessageHandlerFactory httpMessageHandlerFactory)
        {
            _httpMessageHandlerFactory = httpMessageHandlerFactory;
        }

        public HttpMessageHandler CreateHandler() => CreateHandler(typeof(T).FullName!);
        public HttpMessageHandler CreateHandler(string name) => _httpMessageHandlerFactory.CreateHandler(name);
    }
}
