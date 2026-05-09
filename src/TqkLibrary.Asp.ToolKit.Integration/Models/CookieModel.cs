using System.Net;

namespace TqkLibrary.Asp.ToolKit.Integration.Models
{
    public class CookieModel
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string Path { get; set; } = "/";
        public bool Secure { get; set; }
        public bool HttpOnly { get; set; }
        public DateTime? Expires { get; set; }

        public static CookieModel FromCookie(Cookie cookie) => new()
        {
            Name = cookie.Name,
            Value = cookie.Value,
            Domain = cookie.Domain,
            Path = cookie.Path,
            Secure = cookie.Secure,
            HttpOnly = cookie.HttpOnly,
            Expires = cookie.Expires == DateTime.MinValue ? null : cookie.Expires,
        };

        public Cookie ToCookie() => new(Name, Value, Path, Domain)
        {
            Secure = Secure,
            HttpOnly = HttpOnly,
            Expires = Expires ?? DateTime.MinValue,
        };
    }
}
