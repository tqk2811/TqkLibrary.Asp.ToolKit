using Microsoft.AspNetCore.Http;
using TqkLibrary.Asp.ToolKit.Auth.Web.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Device Bound Session Credentials (DBSC, W3C draft) for the web session: a supporting browser keeps a key pair in
    /// the device (TPM) and periodically proves it holds the private key; the web then issues a short-lived proof cookie.
    /// A main cookie copied to another machine has no valid proof after <see cref="ProofLifetime"/>. Browsers without DBSC
    /// keep working with the main cookie only. Off by default (<see cref="Enabled"/>).
    /// </summary>
    public sealed class DeviceBoundSessionOptions
    {
        /// <summary>The algorithms this library can verify (JWS names).</summary>
        internal static readonly IReadOnlyList<string> SupportedAlgorithms = ["ES256", "RS256"];

        /// <summary>
        /// Turns DBSC on. Requires an <see cref="Interfaces.IDeviceKeyApiClient"/> registration (checked at startup). Off
        /// = the web session behaves exactly as without this feature.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>Path (without PathBase) the browser posts the registration proof to.</summary>
        public PathString RegistrationPath { get; set; } = "/dbsc/start";

        /// <summary>Path (without PathBase) the browser posts the refresh proof to.</summary>
        public PathString RefreshPath { get; set; } = "/dbsc/refresh";

        /// <summary>Name of the proof cookie; null = <c>{main cookie name}.proof</c>.</summary>
        public string? ProofCookieName { get; set; }

        /// <summary>Lifetime of the proof cookie (the browser refreshes it when it expires). At least one minute.</summary>
        public TimeSpan ProofLifetime { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>How long the challenge sent with the registration header stays valid.</summary>
        public TimeSpan RegistrationChallengeLifetime { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>How long a refresh challenge stays valid.</summary>
        public TimeSpan RefreshChallengeLifetime { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>Accepted signature algorithms, in order of preference (subset of <c>ES256</c>, <c>RS256</c>).</summary>
        public IList<string> Algorithms { get; set; } = new List<string>(SupportedAlgorithms);

        /// <summary>Smallest accepted RSA modulus in bits (at least 2048).</summary>
        public int MinRsaKeySize { get; set; } = 2048;

        /// <summary>
        /// Paths (prefix match by segment, without PathBase) where the proof is not checked, e.g. static files or
        /// health checks. They are also sent to the browser as <c>exclude</c> scope rules, so it never delays these
        /// requests to refresh the proof first.
        /// </summary>
        public IList<PathString> ExcludedPaths { get; set; } = new List<PathString>();

        /// <summary>
        /// Mode applied when the proof of a bound session fails. With <see cref="AllowUserChoice"/> = false it is the
        /// FIXED mode of the project; with true it is the default for users who have not chosen. Default
        /// <see cref="DeviceProofFailureMode.Reject"/>.
        /// </summary>
        public DeviceProofFailureMode FailureMode { get; set; } = DeviceProofFailureMode.Reject;

        /// <summary>
        /// Policy of the project. False (default, "fixed"): <see cref="FailureMode"/> applies to everybody and
        /// <see cref="CurrentUserInfo.DeviceBindingMode"/> is ignored. True ("user choice"): each user's
        /// <see cref="CurrentUserInfo.DeviceBindingMode"/> (from <c>me</c>, stored by the app) wins when it is one of
        /// <see cref="AllowedUserModes"/>; otherwise <see cref="FailureMode"/> applies (a value outside the allowed set is
        /// logged).
        /// </summary>
        public bool AllowUserChoice { get; set; }

        /// <summary>Modes a user may pick when <see cref="AllowUserChoice"/> is on. Default: all three.</summary>
        public IReadOnlySet<DeviceProofFailureMode> AllowedUserModes { get; set; } = new HashSet<DeviceProofFailureMode>(Enum.GetValues<DeviceProofFailureMode>());

        /// <summary>
        /// A genuine proof of the right session that expired less than this long ago still counts as valid (requests in
        /// flight while the browser refreshes, clock drift). Never applies to a missing, foreign or invalid proof.
        /// </summary>
        public TimeSpan ProofExpiryGrace { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Last word on a failed proof, after the project default and the user's choice: receives the reason and the
        /// mode decided so far and returns the mode to apply. Null = keep the decided mode. An exception is logged and
        /// the decided mode is kept.
        /// </summary>
        public Func<DeviceProofFailureContext, ValueTask<DeviceProofFailureMode>>? OnProofFailure { get; set; }

        /// <summary>
        /// Sends <c>Clear-Site-Data: "cookies"</c> when the web session is signed out or dropped, which also ends the
        /// browser's DBSC session. Off by default because it deletes EVERY cookie of the site, not only this app's.
        /// </summary>
        public bool ClearSiteDataOnSignOut { get; set; }

        /// <summary>Throws <see cref="InvalidOperationException"/> naming the first invalid value.</summary>
        public void Validate()
        {
            if (GetValidationError() is { } error)
                throw new InvalidOperationException("Invalid WebAuthOptions.DeviceBound: " + error);
        }

        internal string? GetValidationError()
        {
            if (!IsValidPath(RegistrationPath))
                return "RegistrationPath must be a non-empty path starting with '/'.";
            if (!IsValidPath(RefreshPath))
                return "RefreshPath must be a non-empty path starting with '/'.";
            if (RegistrationPath.Equals(RefreshPath, StringComparison.OrdinalIgnoreCase))
                return "RegistrationPath and RefreshPath must differ.";
            if (ProofCookieName is not null && (ProofCookieName.Length == 0 || !ProofCookieName.All(IsCookieNameChar)))
                return "ProofCookieName must be null or a valid cookie name.";
            if (ProofLifetime < TimeSpan.FromMinutes(1))
                return "ProofLifetime must be at least one minute.";
            if (RegistrationChallengeLifetime <= TimeSpan.Zero || RefreshChallengeLifetime <= TimeSpan.Zero)
                return "Challenge lifetimes must be positive.";
            if (Algorithms is null || Algorithms.Count == 0)
                return "Algorithms must contain ES256 and/or RS256.";
            foreach (var algorithm in Algorithms)
            {
                if (!SupportedAlgorithms.Contains(algorithm))
                    return $"Algorithm '{algorithm}' is not supported (only ES256 and RS256).";
            }
            if (MinRsaKeySize < 2048)
                return "MinRsaKeySize must be at least 2048.";
            if (ExcludedPaths is null || ExcludedPaths.Any(p => !IsValidPath(p)))
                return "ExcludedPaths must contain only non-empty paths starting with '/'.";
            if (!Enum.IsDefined(FailureMode))
                return "FailureMode is not a defined value.";
            if (AllowedUserModes is null)
                return "AllowedUserModes must not be null.";
            if (ProofExpiryGrace < TimeSpan.Zero)
                return "ProofExpiryGrace must not be negative.";
            return null;
        }

        private static bool IsValidPath(PathString path) => path.HasValue && path.Value!.Length > 1;

        // RFC 6265 token characters.
        private static bool IsCookieNameChar(char c)
            => c > 0x20 && c < 0x7F && "()<>@,;:\\\"/[]?={}".IndexOf(c) < 0;
    }
}
