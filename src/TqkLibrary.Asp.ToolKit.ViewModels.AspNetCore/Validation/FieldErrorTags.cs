using System.Runtime.CompilerServices;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Validation
{
    /// <summary>
    /// Attaches a field error code to one error message string without changing its text.
    /// <para>
    /// <see cref="Microsoft.AspNetCore.Mvc.ModelBinding.ModelError"/> keeps the message string it was given, so the code
    /// travels with that string instance. Each tagged message is a fresh copy: two errors never share an instance, and
    /// an interned or cached message from elsewhere is never tagged. Entries die with their string.
    /// </para>
    /// </summary>
    internal static class FieldErrorTags
    {
        internal sealed record Tag(string Code, Dictionary<string, object>? Params);

        static readonly ConditionalWeakTable<string, Tag> _tags = new();

        /// <summary>A copy of <paramref name="message"/> carrying <paramref name="code"/>; empty messages are returned as is (cannot be tagged).</summary>
        public static string Attach(string message, string code, Dictionary<string, object>? parameters = null)
        {
            if (string.IsNullOrEmpty(message)) return message;
            string copy = new(message.AsSpan());
            _tags.AddOrUpdate(copy, new Tag(code, parameters));
            return copy;
        }

        public static bool TryGet(string? message, out Tag? tag)
        {
            tag = null;
            return message is { Length: > 0 } && _tags.TryGetValue(message, out tag);
        }
    }
}
