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
    /// The short-lived DBSC proof cookie (singleton): value = <c>Protect("v2|sessionId|keyThumbprint|expiresUnixMs")</c>
    /// with a purpose-separated protector (the thumbprint is the RFC 7638 thumbprint of the device key the proof was made
    /// with, so a proof only counts for the key the API has stored); attributes exactly <see cref="Attributes"/> (host-only), the same string sent in
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

        /// <summary>
        /// A proof value for <paramref name="sessionId"/> and the device key <paramref name="keyThumbprint"/>, valid for
        /// <see cref="DeviceBoundSessionOptions.ProofLifetime"/>.
        /// </summary>
        public string CreateValue(string sessionId, string keyThumbprint)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            ArgumentException.ThrowIfNullOrEmpty(keyThumbprint);
            var expires = _clock.GetUtcNow() + _options.Value.DeviceBound.ProofLifetime;
            var payload = "v2|" + sessionId + "|" + keyThumbprint + "|" + expires.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            return Base64Url.EncodeToString(_protector.Protect(Encoding.UTF8.GetBytes(payload)));
        }

        /// <summary>
        /// Null when <paramref name="value"/> proves <paramref name="sessionId"/> with the device key
        /// <paramref name="keyThumbprint"/> (an expiry less than <see cref="DeviceBoundSessionOptions.ProofExpiryGrace"/>
        /// ago still counts), otherwise the reason. A proof made for another key is <see cref="DeviceProofFailureReason.Invalid"/>.
        /// </summary>
        public DeviceProofFailureReason? Check(string? value, string sessionId, string keyThumbprint)
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
            if (parts.Length != 4 || parts[0] != "v2"
                || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var expiresMs))
                return DeviceProofFailureReason.Invalid;
            if (parts[1] != sessionId)
                return DeviceProofFailureReason.WrongSession;
            if (!string.Equals(parts[2], keyThumbprint, StringComparison.Ordinal))
                return DeviceProofFailureReason.Invalid;
            var expires = DateTimeOffset.FromUnixTimeMilliseconds(expiresMs);
            if (expires + _options.Value.DeviceBound.ProofExpiryGrace <= _clock.GetUtcNow())
                return DeviceProofFailureReason.Expired;
            return null;
        }

        /// <summary>Checks the proof cookie of the request.</summary>
        public DeviceProofFailureReason? CheckRequest(HttpContext context, string sessionId, string keyThumbprint)
            => Check(context.Request.Cookies[Name], sessionId, keyThumbprint);

        /// <summary>Issues a new proof cookie for <paramref name="sessionId"/> and the device key <paramref name="keyThumbprint"/>.</summary>
        public void Append(HttpContext context, string sessionId, string keyThumbprint)
        {
            var options = CreateCookieOptions();
            options.MaxAge = _options.Value.DeviceBound.ProofLifetime;
            context.Response.Cookies.Append(Name, CreateValue(sessionId, keyThumbprint), options);
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
