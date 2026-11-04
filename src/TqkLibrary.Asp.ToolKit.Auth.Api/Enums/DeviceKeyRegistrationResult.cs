namespace TqkLibrary.Asp.ToolKit.Auth.Api.Enums
{
    /// <summary>Outcome of <see cref="DeviceKeyRegistrar.RegisterAsync"/>.</summary>
    public enum DeviceKeyRegistrationResult
    {
        /// <summary>The key was stored on the session (suggested HTTP answer: 200).</summary>
        Registered,

        /// <summary>The session already has a device key (or was revoked meanwhile); nothing changed (409).</summary>
        AlreadyBound,

        /// <summary>The algorithm or the JWK is not acceptable; nothing was stored (400).</summary>
        Invalid,

        /// <summary>The caller is not authenticated by the session token scheme with a session id (401).</summary>
        NoSession,
    }
}
