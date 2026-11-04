using System.Net;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Tells a 401 that ends the session (the API authentication handler's <c>WWW-Authenticate: Bearer</c> challenge)
    /// from a business 401 of an endpoint (e.g. a wrong 2FA code), which must not sign the user out.
    /// </summary>
    internal static class SessionChallenge
    {
        /// <summary>True when the response is a 401 that carries a <c>Bearer</c> challenge.</summary>
        public static bool IsSessionChallenge(HttpResponseMessage response)
        {
            if (response.StatusCode != HttpStatusCode.Unauthorized)
                return false;
            foreach (var challenge in response.Headers.WwwAuthenticate)
            {
                if (string.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// True when the response ends the session: a Bearer-challenge 401, or any 401 when
        /// <paramref name="treatAnyUnauthorizedAsSessionExpired"/> is set.
        /// </summary>
        public static bool EndsSession(HttpResponseMessage response, bool treatAnyUnauthorizedAsSessionExpired)
            => treatAnyUnauthorizedAsSessionExpired
                ? response.StatusCode == HttpStatusCode.Unauthorized
                : IsSessionChallenge(response);
    }
}
