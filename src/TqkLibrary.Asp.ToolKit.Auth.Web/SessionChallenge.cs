using System.Net;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Tells a 401 that ends the session (the API authentication handler's <c>WWW-Authenticate: Bearer</c> challenge)
    /// from a business 401 of an endpoint (e.g. a wrong 2FA code), which must not sign the user out. Used by
    /// <see cref="SessionBearerHandler"/>; public for code that calls the API WITHOUT that handler (a reverse proxy or
    /// forwarder relaying browser calls, a hand-built <see cref="HttpClient"/>) and must apply the same rule, for example
    /// by throwing <see cref="SessionExpiredException"/> or signing the user out when <see cref="EndsSession"/> is true.
    /// </summary>
    public static class SessionChallenge
    {
        /// <summary>True when the response is a 401 that carries a <c>Bearer</c> challenge.</summary>
        public static bool IsSessionChallenge(HttpResponseMessage response)
        {
            ArgumentNullException.ThrowIfNull(response);
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
        /// <paramref name="treatAnyUnauthorizedAsSessionExpired"/> is set (pass
        /// <see cref="WebAuthOptions.TreatAnyUnauthorizedAsSessionExpired"/> to match <see cref="SessionBearerHandler"/>).
        /// </summary>
        public static bool EndsSession(HttpResponseMessage response, bool treatAnyUnauthorizedAsSessionExpired = false)
        {
            ArgumentNullException.ThrowIfNull(response);
            return treatAnyUnauthorizedAsSessionExpired
                ? response.StatusCode == HttpStatusCode.Unauthorized
                : IsSessionChallenge(response);
        }
    }
}
