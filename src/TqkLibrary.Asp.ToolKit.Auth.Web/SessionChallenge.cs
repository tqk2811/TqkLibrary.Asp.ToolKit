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

        /// <summary>
        /// <see cref="EndsSession(HttpResponseMessage, bool)"/> with the rule of <paramref name="options"/>
        /// (<see cref="WebAuthOptions.TreatAnyUnauthorizedAsSessionExpired"/>), the same one <see cref="SessionBearerHandler"/> applies.
        /// </summary>
        public static bool EndsSession(HttpResponseMessage response, WebAuthOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return EndsSession(response, options.TreatAnyUnauthorizedAsSessionExpired);
        }

        /// <summary>
        /// Default of <see cref="WebAuthOptions.IsSessionRejectedException"/>: true when <paramref name="exception"/> or one of
        /// its inner exceptions (every entry of an <see cref="AggregateException"/>'s <see cref="AggregateException.InnerExceptions"/>
        /// too, not only the first) is an <see cref="HttpRequestException"/> with status 401. A 403 or any other status is false.
        /// At most 16 exceptions are examined in total (breadth first), so a cyclic or very deep chain cannot hang a request.
        /// A timeout or cancellation is never a rejected session: the middleware does not call the predicate for an
        /// <see cref="OperationCanceledException"/> or <see cref="TimeoutException"/> at the top of the failure.
        /// </summary>
        public static bool IsSessionRejectedException(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            var pending = new Queue<Exception>();
            pending.Enqueue(exception);
            for (var examined = 0; examined < 16 && pending.Count > 0; examined++)
            {
                var current = pending.Dequeue();
                if (current is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
                    return true;
                if (current is AggregateException aggregate)
                {
                    foreach (var inner in aggregate.InnerExceptions)
                        pending.Enqueue(inner);
                }
                else if (current.InnerException is { } next)
                    pending.Enqueue(next);
            }
            return false;
        }
    }
}
