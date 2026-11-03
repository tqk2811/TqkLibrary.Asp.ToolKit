using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using TqkLibrary.Http;

namespace TqkLibrary.Asp.ToolKit.Integration
{
    /// <summary>
    /// Base class of clients for APIs using the <c>TqkLibrary.Asp.ToolKit.ViewModels</c> envelope; call endpoints with
    /// <see cref="Extensions.RequestBuilderApiExtensions"/>.
    /// <para>
    /// The <see cref="HttpClient"/> is passed in (typically from an <c>IHttpClientFactory</c>) and is NOT owned: its
    /// handler lifetime is managed by the factory.
    /// </para>
    /// </summary>
    public abstract class ApiBase : BaseApi, IBaseAddress
    {
        /// <summary>Uses <see cref="CreateDefaultJsonSerializerSettings"/>.</summary>
        protected ApiBase(HttpClient httpClient)
            : this(httpClient, CreateDefaultJsonSerializerSettings())
        {
        }

        /// <param name="httpClient">Client to send requests with (not disposed by this class).</param>
        /// <param name="jsonSerializerSettings">Settings matching the server's serializer.</param>
        protected ApiBase(HttpClient httpClient, JsonSerializerSettings jsonSerializerSettings)
            : base(httpClient ?? throw new ArgumentNullException(nameof(httpClient)))
        {
            DefaultJsonSerializerSettings = jsonSerializerSettings ?? throw new ArgumentNullException(nameof(jsonSerializerSettings));
        }

        /// <inheritdoc />
        public Uri? BaseAddress => _httpClient.BaseAddress;

        /// <summary>
        /// camelCase, nulls ignored. Empty strings are still sent (unlike an ignore-empty-string resolver): otherwise a
        /// request resetting a field to empty would silently mean "leave unchanged".
        /// </summary>
        public static JsonSerializerSettings CreateDefaultJsonSerializerSettings() => new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
        };
    }
}
