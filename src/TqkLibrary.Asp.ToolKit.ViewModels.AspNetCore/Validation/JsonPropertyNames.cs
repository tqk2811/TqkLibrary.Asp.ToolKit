using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TqkLibrary.Asp.ToolKit.ViewModels.AspNetCore.Validation
{
    /// <summary>
    /// C# member path → JSON path, so field errors point at the name the client sent. Honors System.Text.Json
    /// <see cref="JsonPropertyNameAttribute"/> and Newtonsoft <c>[JsonProperty("name")]</c> (read by reflection on the
    /// attribute type name: no package reference); otherwise camelCase.
    /// </summary>
    internal static class JsonPropertyNames
    {
        const string NewtonsoftJsonPropertyAttribute = "Newtonsoft.Json.JsonPropertyAttribute";

        static readonly ConcurrentDictionary<(Type Type, string Member), PropertyInfo?> _properties = new();

        public static string CamelCase(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

        /// <summary>
        /// <c>Items[0].DisplayName</c> → <c>items[0].displayName</c>, walking the types from <paramref name="rootType"/>
        /// (element type at each <c>[i]</c>).
        /// </summary>
        public static string ToJsonPath(Type? rootType, string path)
        {
            if (path.Length == 0) return path;

            Type? current = rootType;
            string[] segments = path.Split('.');
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                int bracket = segment.IndexOf('[');
                string name = bracket < 0 ? segment : segment[..bracket];
                PropertyInfo? property = FindProperty(current, name);
                segments[i] = (property is null ? CamelCase(name) : NameOf(property))
                    + (bracket < 0 ? string.Empty : segment[bracket..]);

                current = property?.PropertyType;
                if (current is not null && bracket >= 0)
                {
                    // Each [] peels one element level.
                    for (int b = bracket; b >= 0 && current is not null; b = segment.IndexOf('[', b + 1))
                        current = ElementType(current);
                }
            }
            return string.Join('.', segments);
        }

        static string NameOf(PropertyInfo property)
        {
            JsonPropertyNameAttribute? stj = property.GetCustomAttribute<JsonPropertyNameAttribute>();
            if (stj is not null) return stj.Name;

            foreach (object attribute in property.GetCustomAttributes(true))
            {
                Type type = attribute.GetType();
                if (type.FullName != NewtonsoftJsonPropertyAttribute) continue;
                if (type.GetProperty("PropertyName")?.GetValue(attribute) is string { Length: > 0 } name) return name;
            }
            return CamelCase(property.Name);
        }

        static PropertyInfo? FindProperty(Type? type, string memberName)
        {
            if (type is null || memberName.Length == 0) return null;
            return _properties.GetOrAdd((type, memberName), static key =>
            {
                try
                {
                    PropertyInfo[] properties = key.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    return properties.FirstOrDefault(p => p.Name == key.Member)
                        ?? properties.FirstOrDefault(p => string.Equals(p.Name, key.Member, StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception)
                {
                    // A camelCase name is better than throwing while reporting an error.
                    return null;
                }
            });
        }

        static Type? ElementType(Type type)
        {
            if (type.IsArray) return type.GetElementType();
            IEnumerable<Type> types = type.GetInterfaces().Append(type);
            Type? dictionary = types.FirstOrDefault(t => t.IsGenericType
                && (t.GetGenericTypeDefinition() == typeof(IDictionary<,>) || t.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
            if (dictionary is not null) return dictionary.GetGenericArguments()[1];
            Type? enumerable = types.FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return enumerable?.GetGenericArguments()[0] ?? (typeof(IEnumerable).IsAssignableFrom(type) ? typeof(object) : null);
        }
    }
}
