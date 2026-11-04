using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// The short-lived DBSC proof cookie (singleton): value = <c>Protect("v1|sessionId|expiresUnixMs")</c> with a
    /// purpose-separated protector; attributes exactly <see cref="Attributes"/> (host-only), the same string sent in
    /// the session config so the browser knows which cookie it keeps alive. Expiry is inside the value and checked with
    /// <see cref="TimeProvider"/>; the cookie's Max-Age makes the browser drop it, then refresh.
    /// </summary>
    internal sealed class DeviceProofCookie
    {
        /// <summary>Attributes of the proof cookie, as announced in the session config <c>credentials[].attributes</c>.</summary>
        public const string Attributes = "Path=/; Secure; HttpOnly; SameSite=Lax";

        private const string Purpose = "TqkLibrary.Asp.ToolKit.Auth.Web.DeviceBound.Proof.v1";

        private readonly IDataProtector _protector;
        private readonly TimeProvider _clock;
        private readonly IOptions<WebAuthOptions> _options;
        private readonly IOptionsMonitor<CookieAuthenticationOptions> _cookieOptions;

        public DeviceProofCookie(IDataProtectionProvider dataProtection, TimeProvider clock, IOptions<WebAuthOptions> options,
            IOptionsMonitor<CookieAuthenticationOptions> cookieOptions)
        {
            _protector = dataProtection.CreateProtector(Purpose);
            _clock = clock;
            _options = options;
            _cookieOptions = cookieOptions;
        }

        /// <summary><see cref="DeviceBoundSessionOptions.ProofCookieName"/>, or <c>{main cookie name}.proof</c>.</summary>
        public string Name
            => _options.Value.DeviceBound.ProofCookieName
               ?? (_cookieOptions.Get(_options.Value.Scheme).Cookie.Name ?? CookieAuthenticationDefaults.CookiePrefix + _options.Value.Scheme) + ".proof";

        /// <summary>A proof value for <paramref name="sessionId"/>, valid for <see cref="DeviceBoundSessionOptions.ProofLifetime"/>.</summary>
        public string CreateValue(string sessionId)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            var expires = _clock.GetUtcNow() + _options.Value.DeviceBound.ProofLifetime;
            var payload = "v1|" + sessionId + "|" + expires.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            return Base64Url.EncodeToString(_protector.Protect(Encoding.UTF8.GetBytes(payload)));
        }

        /// <summary>
        /// Null when <paramref name="value"/> proves <paramref name="sessionId"/> (an expiry less than
        /// <see cref="DeviceBoundSessionOptions.ProofExpiryGrace"/> ago still counts), otherwise the reason.
        /// </summary>
        public DeviceProofFailureReason? Check(string? value, string sessionId)
        {
            if (string.IsNullOrEmpty(value))
                return DeviceProofFailureReason.Missing;
            if (value.Length > 1024)
                return DeviceProofFailureReason.Invalid;
            string payload;
            try
            {
                payload = Encoding.UTF8.GetString(_protector.Unprotect(Base64Url.DecodeFromChars(value)));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return DeviceProofFailureReason.Invalid;
            }
            var parts = payload.Split('|');
            if (parts.Length != 3 || parts[0] != "v1"
                || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expiresMs))
                return DeviceProofFailureReason.Invalid;
            if (parts[1] != sessionId)
                return DeviceProofFailureReason.WrongSession;
            var expires = DateTimeOffset.FromUnixTimeMilliseconds(expiresMs);
            if (expires + _options.Value.DeviceBound.ProofExpiryGrace <= _clock.GetUtcNow())
                return DeviceProofFailureReason.Expired;
            return null;
        }

        /// <summary>Checks the proof cookie of the request.</summary>
        public DeviceProofFailureReason? CheckRequest(HttpContext context, string sessionId)
            => Check(context.Request.Cookies[Name], sessionId);

        /// <summary>Issues a new proof cookie for <paramref name="sessionId"/>.</summary>
        public void Append(HttpContext context, string sessionId)
        {
            var options = CreateCookieOptions();
            options.MaxAge = _options.Value.DeviceBound.ProofLifetime;
            context.Response.Cookies.Append(Name, CreateValue(sessionId), options);
        }

        /// <summary>Deletes the proof cookie (same attributes, so the browser matches it).</summary>
        public void Delete(HttpContext context) => context.Response.Cookies.Delete(Name, CreateCookieOptions());

        // Must stay in line with Attributes: Path=/; Secure; HttpOnly; SameSite=Lax; no Domain (host-only).
        private static CookieOptions CreateCookieOptions() => new()
        {
            Path = "/",
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
        };
    }
}
