using System.Security.Claims;
using TqkLibrary.Asp.ToolKit.Auth.Api.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Extensions
{
    /// <summary>Reads the device binding claims added by <see cref="SessionTokenAuthenticationHandler"/>.</summary>
    public static class DeviceBindingClaimsPrincipalExtensions
    {
        /// <summary>
        /// The device binding of the session of <paramref name="user"/> (claims <see cref="ApiAuthOptions.DeviceKeyJwkClaimType"/>,
        /// <see cref="ApiAuthOptions.DeviceKeyAlgorithmClaimType"/>, <see cref="ApiAuthOptions.DeviceBindingModeClaimType"/>);
        /// null when the session is not bound (no key claim). Use it in the app's <c>me</c> endpoint; the web front end
        /// reads <c>deviceKeyJwk</c> / <c>deviceKeyAlgorithm</c>, <c>isDeviceBound</c> is informational:
        /// <code>
        /// var binding = user.GetDeviceBinding(apiOptions.Value);
        /// return new { /* user fields */, isDeviceBound = binding is not null, deviceKeyJwk = binding?.Jwk,
        ///     deviceKeyAlgorithm = binding?.Algorithm, deviceBindingMode = binding?.Mode };
        /// </code>
        /// </summary>
        public static DeviceBindingInfo? GetDeviceBinding(this ClaimsPrincipal user, ApiAuthOptions options)
        {
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(options);
            var jwk = user.FindFirst(options.DeviceKeyJwkClaimType)?.Value;
            if (string.IsNullOrEmpty(jwk))
                return null;
            var algorithm = user.FindFirst(options.DeviceKeyAlgorithmClaimType)?.Value;
            var mode = user.FindFirst(options.DeviceBindingModeClaimType)?.Value;
            return new DeviceBindingInfo(jwk, string.IsNullOrEmpty(algorithm) ? null : algorithm, string.IsNullOrEmpty(mode) ? null : mode);
        }
    }
}
