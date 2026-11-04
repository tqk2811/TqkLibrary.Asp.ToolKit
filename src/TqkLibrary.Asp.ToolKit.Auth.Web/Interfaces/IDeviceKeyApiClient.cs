namespace TqkLibrary.Asp.ToolKit.Auth.Web.Interfaces
{
    /// <summary>
    /// The app's bridge to its API for Device Bound Session Credentials (DBSC). Required only when
    /// <see cref="DeviceBoundSessionOptions.Enabled"/> is true (checked at startup). Implement it on top of the app's own
    /// HTTP client with <see cref="SessionBearerHandler"/> attached, so the call carries the session token of the
    /// session being bound.
    /// </summary>
    public interface IDeviceKeyApiClient
    {
        /// <summary>
        /// Stores the device public key on the API session of the request (the API keeps it with the session row, e.g.
        /// <c>DevicePublicKey</c>/<c>DeviceKeyAlgorithm</c>, and reports <see cref="Models.CurrentUserInfo.IsDeviceBound"/>
        /// = true from then on). Called once per session, after the browser proved it holds the private key.
        /// </summary>
        /// <param name="jwk">Public key as a JSON Web Key (only the public members: EC <c>crv/kty/x/y</c> or RSA <c>e/kty/n</c>).</param>
        /// <param name="algorithm"><c>ES256</c> or <c>RS256</c>.</param>
        /// <param name="cancellationToken">Request cancellation.</param>
        /// <returns>
        /// True when the key was stored; false when the API refuses (the session is already bound, binding disabled for
        /// the user...): the browser then gets 403 and the session stays unbound. A 401 must surface as
        /// <see cref="SessionExpiredException"/> (the bearer handler does that); any other exception is a transient error
        /// (the browser gets 503 and may retry at the next sign-in).
        /// </returns>
        Task<bool> RegisterDeviceKeyAsync(string jwk, string algorithm, CancellationToken cancellationToken);
    }
}
