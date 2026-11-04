using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using TqkLibrary.Asp.ToolKit.Auth.Api.Enums;
using TqkLibrary.Asp.ToolKit.Auth.Api.Interfaces;
using TqkLibrary.Asp.ToolKit.Auth.Api.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>
    /// Registers the device public key of the CURRENT session (DBSC, Device Bound Session Credentials). Scoped, registered
    /// by <see cref="Extensions.ApiAuthServiceCollectionExtensions.AddTqkApiAuth"/>; <see cref="IDeviceKeyStore"/> is
    /// resolved only when a key is registered, so apps without device binding register nothing more.
    /// <para>
    /// Order of the checks: the caller must be authenticated by the session token scheme
    /// (<see cref="SessionTokenAuthenticationHandler"/>) with a session id claim (<see cref="DeviceKeyRegistrationResult.NoSession"/>);
    /// a session already bound per its claim answers <see cref="DeviceKeyRegistrationResult.AlreadyBound"/>; then the
    /// algorithm (<c>ES256</c> or <c>RS256</c> only) and the JWK (public members only, size at most
    /// <see cref="MaxJwkLength"/>, EC P-256 point on the curve or RSA 2048..8192 bits with exponent 65537) are validated
    /// (<see cref="DeviceKeyRegistrationResult.Invalid"/>); the canonical JWK is stored with
    /// <see cref="IDeviceKeyStore.TrySetDeviceKeyAsync"/> (false → <see cref="DeviceKeyRegistrationResult.AlreadyBound"/>);
    /// on success the session's cached record is invalidated so the next request carries
    /// <see cref="ApiAuthOptions.DeviceBoundClaimType"/>.
    /// </para>
    /// <para>
    /// A key can only be registered within <see cref="ApiAuthOptions.DeviceKeyRegistrationWindow"/> after the session was
    /// created (the store enforces it); later attempts answer <see cref="DeviceKeyRegistrationResult.AlreadyBound"/>.
    /// </para>
    /// <para>
    /// This class does NOT verify proof of possession: the caller (the Web) must verify the DBSC registration JWT signed
    /// by this key before calling. The session token scheme is detected through <see cref="ClaimsIdentity.AuthenticationType"/>,
    /// so an app must not create identities with the session token scheme name by hand. Resolve it only inside a request
    /// scope. If an app ever removes a device key it must call <see cref="SessionAccessCache.InvalidateSession"/>,
    /// because the <see cref="DeviceKeyRegistrationResult.AlreadyBound"/> shortcut trusts the claim.
    /// </para>
    /// </summary>
    public sealed class DeviceKeyRegistrar
    {
        /// <summary>Maximum length (characters) of the JWK text accepted by <see cref="RegisterAsync"/>.</summary>
        public const int MaxJwkLength = 4096;

        private readonly IServiceProvider _services;
        private readonly SessionAccessCache _cache;
        private readonly ApiAuthOptions _options;
        private readonly TimeProvider _clock;

        public DeviceKeyRegistrar(IServiceProvider services, SessionAccessCache cache, IOptions<ApiAuthOptions> options, TimeProvider clock)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(cache);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(clock);
            _services = services;
            _cache = cache;
            _options = options.Value;
            _clock = clock;
        }

        /// <summary>
        /// Stores <paramref name="publicKeyJwk"/> (the device's public key as a JWK JSON object) and
        /// <paramref name="algorithm"/> on the session of <paramref name="user"/>, once per session. See the class remarks
        /// for the checks. Throws <see cref="InvalidOperationException"/> when no <see cref="IDeviceKeyStore"/> is registered.
        /// </summary>
        public async Task<DeviceKeyRegistrationResult> RegisterAsync(ClaimsPrincipal user, string publicKeyJwk, string algorithm,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(user);

            var identity = await FindSessionIdentityAsync(user).ConfigureAwait(false);
            var sessionId = identity?.FindFirst(_options.SessionIdClaimType)?.Value;
            if (identity is null || string.IsNullOrEmpty(sessionId))
                return DeviceKeyRegistrationResult.NoSession;

            // The claim may lag (cache) only from "not bound" to "bound", so "bound" is final.
            if (identity.HasClaim(c => c.Type == _options.DeviceBoundClaimType && string.Equals(c.Value, "true", StringComparison.OrdinalIgnoreCase)))
                return DeviceKeyRegistrationResult.AlreadyBound;

            if (!DeviceKeyJwk.TryCanonicalize(publicKeyJwk, algorithm, MaxJwkLength, out var canonical))
                return DeviceKeyRegistrationResult.Invalid;

            var store = _services.GetService<IDeviceKeyStore>()
                ?? throw new InvalidOperationException("Device key registration requires an IDeviceKeyStore service registered by the app.");
            var now = _clock.GetUtcNow();
            DateTimeOffset? createdNotBefore = _options.DeviceKeyRegistrationWindow is { } w ? now - w : null;
            if (!await store.TrySetDeviceKeyAsync(sessionId, canonical, algorithm, now, createdNotBefore, cancellationToken).ConfigureAwait(false))
                return DeviceKeyRegistrationResult.AlreadyBound;

            _cache.InvalidateSession(sessionId);
            return DeviceKeyRegistrationResult.Registered;
        }

        /// <summary>The authenticated identity issued by a scheme whose handler is <see cref="SessionTokenAuthenticationHandler"/>.</summary>
        private async Task<ClaimsIdentity?> FindSessionIdentityAsync(ClaimsPrincipal user)
        {
            var schemes = _services.GetService<IAuthenticationSchemeProvider>();
            if (schemes is null)
                return null;
            foreach (var identity in user.Identities)
            {
                if (!identity.IsAuthenticated || string.IsNullOrEmpty(identity.AuthenticationType))
                    continue;
                var scheme = await schemes.GetSchemeAsync(identity.AuthenticationType).ConfigureAwait(false);
                if (scheme?.HandlerType == typeof(SessionTokenAuthenticationHandler))
                    return identity;
            }
            return null;
        }
    }
}
