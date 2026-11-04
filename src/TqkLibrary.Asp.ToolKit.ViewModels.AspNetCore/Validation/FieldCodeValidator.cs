using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Validation
{
    /// <summary>Runs the inner validator and tags each failure message with a fixed code (text unchanged).</summary>
    internal sealed class FieldCodeValidator : IModelValidator
    {
        readonly IModelValidator _inner;
        readonly string _code;
        readonly Dictionary<string, object>? _params;

        public FieldCodeValidator(IModelValidator inner, string code, Dictionary<string, object>? parameters)
        {
            _inner = inner;
            _code = code;
            _params = parameters;
        }

        public IEnumerable<ModelValidationResult> Validate(ModelValidationContext context)
        {
            foreach (ModelValidationResult result in _inner.Validate(context))
            {
                // Each error gets its own params copy so a consumer mutating one FieldError cannot leak into another.
                Dictionary<string, object>? parameters = _params is null ? null : new(_params);
                yield return new ModelValidationResult(result.MemberName, FieldErrorTags.Attach(result.Message, _code, parameters));
            }
        }
    }
}
