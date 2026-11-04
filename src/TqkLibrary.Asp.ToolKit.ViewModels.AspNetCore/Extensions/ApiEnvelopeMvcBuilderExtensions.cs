using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Filters;
using TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Validation;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Extensions
{
    public static class ApiEnvelopeMvcBuilderExtensions
    {
        /// <summary>
        /// Registers the API envelope: <see cref="ApiEnvelopeResultFilter"/> on every action, the 400
        /// <c>VALIDATION_FAILED</c> response (<see cref="TqkApiEnvelopeOptions.EnableValidationResponse"/>) and the
        /// field error codes (<see cref="TqkApiEnvelopeOptions.UseFieldErrorCodes"/>). Add the middlewares with
        /// <c>UseTqkRequestId()</c> and <c>UseTqkApiExceptionWrapper()</c>.
        /// </summary>
        public static IMvcBuilder AddTqkApiEnvelope(this IMvcBuilder builder, Action<TqkApiEnvelopeOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            OptionsBuilder<TqkApiEnvelopeOptions> options = builder.Services.AddOptions<TqkApiEnvelopeOptions>();
            if (configure is not null) options.Configure(configure);

            builder.Services.AddOptions<MvcOptions>()
                .Configure<IOptions<TqkApiEnvelopeOptions>>((mvc, envelope) =>
                {
                    if (!mvc.Filters.OfType<TypeFilterAttribute>().Any(x => x.ImplementationType == typeof(ApiEnvelopeResultFilter)))
                        mvc.Filters.Add<ApiEnvelopeResultFilter>();
                });

            // PostConfigure: runs after every Configure, so the DataAnnotations provider is already there
            // (whatever the AddMvcCore/AddDataAnnotations order) and app-set binding messages get wrapped.
            builder.Services.AddOptions<MvcOptions>()
                .PostConfigure<IOptions<TqkApiEnvelopeOptions>>((mvc, envelope) =>
                {
                    if (envelope.Value.UseFieldErrorCodes)
                        mvc.UseFieldErrorCodes();
                });

            builder.Services.AddOptions<ApiBehaviorOptions>()
                .Configure<IOptions<TqkApiEnvelopeOptions>>((api, envelope) =>
                {
                    if (envelope.Value.EnableValidationResponse)
                        api.InvalidModelStateResponseFactory = FieldValidationErrors.CreateResponse;
                });

            return builder;
        }
    }
}
