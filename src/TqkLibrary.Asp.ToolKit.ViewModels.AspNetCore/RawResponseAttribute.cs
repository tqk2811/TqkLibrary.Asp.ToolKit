namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore
{
    /// <summary>
    /// Marks an endpoint whose result must NOT be wrapped into <see cref="ApiResponse{T}"/> by
    /// <see cref="Filters.ApiEnvelopeResultFilter"/> (raw text, health checks, file downloads...).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public sealed class RawResponseAttribute : Attribute
    {
    }
}
