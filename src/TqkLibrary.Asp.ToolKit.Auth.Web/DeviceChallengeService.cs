using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Stateless DBSC challenges (singleton): <c>Protect("v1|kind|sessionId|nonce|expiresUnixMs")</c> with a
    /// purpose-separated Data Protection protector, so nothing is stored per challenge and every instance sharing the
    /// key ring accepts it. Expiry is inside the payload and checked with <see cref="TimeProvider"/> (an
    /// <c>ITimeLimitedDataProtector</c> would read the system clock and could not tell "expired" from "forged").
    /// <para>Replay protection is best effort: a used challenge is remembered in <see cref="IMemoryCache"/> until it
    /// expires, on THIS instance only. Behind a load balancer a proof replayed to another instance within the challenge
    /// lifetime is accepted; the replayed proof is still bound to the same session and device key.</para>
    /// </summary>
    internal sealed class DeviceChallengeService
    {
        private const string Purpose = "TqkLibrary.Asp.ToolKit.Auth.Web.DeviceBound.Challenge.v1";
        private const string UsedPrefix = "tqk-dbsc-used-jti:";

        private readonly IDataProtector _protector;
        private readonly IMemoryCache _cache;
        private readonly TimeProvider _clock;
        private readonly IOptions<WebAuthOptions> _options;
        private readonly object _consumeLock = new();

        public DeviceChallengeService(IDataProtectionProvider dataProtection, IMemoryCache cache, TimeProvider clock,
            IOptions<WebAuthOptions> options)
        {
            _protector = dataProtection.CreateProtector(Purpose);
            _cache = cache;
            _clock = clock;
            _options = options;
        }

        /// <summary>A new challenge for <paramref name="sessionId"/>, valid for the configured lifetime of <paramref name="kind"/>.</summary>
        public string Issue(DeviceChallengeKind kind, string sessionId)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            var expires = _clock.GetUtcNow() + Lifetime(kind);
            var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
            var payload = string.Join('|', "v1", KindCode(kind), sessionId, nonce,
                expires.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            return Base64Url.EncodeToString(_protector.Protect(Encoding.UTF8.GetBytes(payload)));
        }

        /// <summary>
        /// Checks <paramref name="challenge"/> (the proof's <c>jti</c>). With <paramref name="consume"/> a valid challenge
        /// is marked used, so a second proof with it gets <see cref="DeviceChallengeStatus.Replayed"/>. Consume only
        /// after the proof signature was verified (otherwise anybody could burn a challenge).
        /// </summary>
        public DeviceChallengeStatus Validate(string? challenge, DeviceChallengeKind kind, string sessionId, bool consume)
        {
            if (string.IsNullOrEmpty(challenge) || challenge.Length > 2048 || string.IsNullOrEmpty(sessionId))
                return DeviceChallengeStatus.Invalid;
            byte[] raw;
            string payload;
            try
            {
                raw = Base64Url.DecodeFromChars(challenge);
                payload = Encoding.UTF8.GetString(_protector.Unprotect(raw));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return DeviceChallengeStatus.Invalid;
            }
            var parts = payload.Split('|');
            if (parts.Length != 5 || parts[0] != "v1" || parts[1] != KindCode(kind) || parts[2] != sessionId
                || !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var expiresMs))
                return DeviceChallengeStatus.Invalid;
            var remaining = DateTimeOffset.FromUnixTimeMilliseconds(expiresMs) - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return DeviceChallengeStatus.Expired;
            if (!consume)
                return IsUsed(raw) ? DeviceChallengeStatus.Replayed : DeviceChallengeStatus.Valid;

            lock (_consumeLock)
            {
                if (IsUsed(raw))
                    return DeviceChallengeStatus.Replayed;
                // Keep the mark a little longer than the challenge lives (the cache runs on its own clock). Size: an app
                // may register IMemoryCache with a SizeLimit, which then requires a size on every entry.
                _cache.Set(UsedKey(raw), true, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = remaining + TimeSpan.FromMinutes(1),
                    Size = 1,
                });
                return DeviceChallengeStatus.Valid;
            }
        }

        private bool IsUsed(byte[] raw) => _cache.TryGetValue(UsedKey(raw), out _);

        /// <summary>
        /// Keyed on the DECODED challenge bytes, not on the text: base64url has several spellings of the same bytes (the
        /// unused low bits of the last character), and a re-encoded challenge must count as the same, already used one.
        /// </summary>
        private static string UsedKey(byte[] raw) => UsedPrefix + Convert.ToHexString(SHA256.HashData(raw));

        private TimeSpan Lifetime(DeviceChallengeKind kind) => kind == DeviceChallengeKind.Registration
            ? _options.Value.DeviceBound.RegistrationChallengeLifetime
            : _options.Value.DeviceBound.RefreshChallengeLifetime;

        private static string KindCode(DeviceChallengeKind kind) => kind == DeviceChallengeKind.Registration ? "reg" : "refresh";
    }
}
