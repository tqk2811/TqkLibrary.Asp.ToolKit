using Microsoft.AspNetCore.Http;
using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Models;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Interfaces
{
    /// <summary>
    /// Post-processes every error mapping (from <see cref="ApiErrorException"/>, an <see cref="IApiExceptionMapper"/>
    /// or the default 500) before the exception wrapper middleware writes it. Use it for rules keyed on the error code
    /// whatever the source, e.g. a <c>Retry-After</c> header or a retryable flag. Register implementations in DI; they
    /// run in registration order, each receiving the previous result (register the app's own transformers last so they
    /// see what library transformers produced). A transformer that throws is logged and skipped. They are resolved from
    /// <see cref="HttpContext.RequestServices"/>, so any lifetime works, scoped services included.
    /// </summary>
    public interface IApiErrorTransformer
    {
        /// <returns>
        /// A modified copy (<c>mapping with { ... }</c>), or <paramref name="mapping"/> itself or null to keep it unchanged.
        /// </returns>
        ApiErrorMapping? Transform(HttpContext context, Exception exception, ApiErrorMapping mapping);
    }
}
