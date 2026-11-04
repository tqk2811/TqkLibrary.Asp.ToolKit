using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using TqkLibrary.Asp.ToolKit.Auth.Api.Interfaces;
using TqkLibrary.Asp.ToolKit.Auth.Api.Models;

namespace TqkLibrary.Asp.ToolKit.Auth.Api
{
    /// <summary>
    /// Authenticates <c>Authorization: Bearer {sessionId}.{secret}</c> (see <see cref="SessionTokenCodec"/>) on every
    /// request:
    /// <list type="number">
    ///   <item>loads the session through <see cref="SessionAccessCache"/> → <see cref="ISessionAccessSource.GetAsync"/>
    ///   (an unknown session is answered as <see cref="SessionRejectionReasons.InvalidToken"/> after a dummy hash check);</item>
    ///   <item>compares the secret hash in fixed time, and only then checks
    ///   <see cref="SessionAccessRecord.UserRejectionReason"/>, <see cref="SessionAccessRecord.IsRevoked"/>,
    ///   <see cref="SessionAccessRecord.ExpiresAt"/> and the absolute cap (<see cref="SlidingSessionPolicy"/>), so a caller
    ///   without the secret learns nothing about the session or its user;</item>
    ///   <item>slides the expiry at most once per <see cref="ApiAuthOptions.SessionTouchInterval"/>
    ///   (<see cref="ISessionAccessSource.TouchAsync"/>; a failure is only logged);</item>
    ///   <item>builds the principal from CURRENT data: user id (<c>NameIdentifier</c> and
    ///   <see cref="ApiAuthOptions.UserIdClaimType"/>), session id, name, email, roles, permissions.</item>
    /// </list>
    /// A request without a bearer token gets no result (other schemes may run). A rejected token fails with a
    /// <see cref="SessionRejection"/> stored in <c>HttpContext.Features</c> (see
    /// <see cref="SessionTokenAuthenticationOptions.OnChallenge"/>). The token is never logged.
    /// </summary>
    public sealed class SessionTokenAuthenticationHandler : AuthenticationHandler<SessionTokenAuthenticationOptions>
    {
        private const string BearerPrefix = "Bearer ";
        private static readonly TimeSpan MaxTouchRetryDelay = TimeSpan.FromMinutes(1);

        private readonly ApiAuthOptions _apiOptions;
        private readonly SessionAccessCache _cache;
        private readonly SlidingSessionPolicy _policy;
        private readonly TimeProvider _clock;

        public SessionTokenAuthenticationHandler(
            IOptionsMonitor<SessionTokenAuthenticationOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IOptions<ApiAuthOptions> apiOptions,
            SessionAccessCache cache,
            SlidingSessionPolicy policy,
            TimeProvider clock)
            : base(options, logger, encoder)
        {
            ArgumentNullException.ThrowIfNull(apiOptions);
            _apiOptions = apiOptions.Value;
            _cache = cache;
            _policy = policy;
            _clock = clock;
        }

        /// <inheritdoc />
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? header = Request.Headers.Authorization;
            if (string.IsNullOrEmpty(header) || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
                return AuthenticateResult.NoResult();

            var token = header[BearerPrefix.Length..].Trim();
            if (!SessionTokenCodec.TryParse(token, out var sessionId, out var secret))
                return Reject(SessionRejectionReasons.InvalidToken, "The session token is malformed.");

            var source = Context.RequestServices.GetRequiredService<ISessionAccessSource>();
            var aborted = Context.RequestAborted;
            var access = await _cache.GetAsync(sessionId, ct => source.GetAsync(sessionId, ct), aborted).ConfigureAwait(false);
            if (access is null)
            {
                // Unknown session: spend the same hashing time as a wrong secret, answer the same way.
                SessionTokenCodec.Verify(secret, SessionTokenCodec.DummyHash);
                return Reject(SessionRejectionReasons.InvalidToken, "The session token is invalid.");
            }

            // Secret first: a wrong secret must not reveal that the user is locked or the session revoked or expired.
            if (!SessionTokenCodec.Verify(secret, access.SecretHash))
                return Reject(SessionRejectionReasons.InvalidToken, "The session token is invalid.");
            if (access.UserRejectionReason is { } userReason)
                return Reject(userReason, "The user cannot sign in (" + userReason + ").");
            if (access.IsRevoked)
                return Reject(access.RevokedReason ?? SessionRejectionReasons.Revoked, "The session was revoked.");

            var now = _clock.GetUtcNow();
            var utcNow = now.UtcDateTime;
            var createdAt = ToUtc(access.CreatedAt);
            var expiresAt = ToUtc(access.ExpiresAt);
            var lastAccessAt = access.LastAccessAt is { } last ? ToUtc(last) : (DateTime?)null;
            var nextExpiry = expiresAt <= utcNow ? null : _policy.GetNextExpiry(createdAt, utcNow, access.IsPersistent);
            if (nextExpiry is not { } next)
                return Reject(SessionRejectionReasons.Expired, "The session has expired.");

            await TouchIfDueAsync(source, sessionId, access, lastAccessAt ?? createdAt, now, next).ConfigureAwait(false);

            return AuthenticateResult.Success(new AuthenticationTicket(CreatePrincipal(sessionId, access), Scheme.Name));
        }

        /// <inheritdoc />
        protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            if (Response.HasStarted)
                return;
            // Status and challenge header are set BEFORE the hook: the web side tells an ended session from a business
            // 401 by this header, so it must be present even when the hook writes its own body and handles the response.
            // RFC 6750: no error parameter when the request carried no token.
            var rejection = Context.Features.Get<SessionRejection>();
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers[HeaderNames.WWWAuthenticate] = rejection is null ? "Bearer" : "Bearer error=\"invalid_token\"";
            if (Options.OnChallenge is { } onChallenge)
            {
                var challenge = new SessionTokenChallengeContext(Context, rejection, properties);
                await onChallenge(challenge).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Writes the sliding expiry when the last write is older than <see cref="ApiAuthOptions.SessionTouchInterval"/>
        /// and no other request of this process is writing it. Never throws for a store failure.
        /// </summary>
        private async Task TouchIfDueAsync(ISessionAccessSource source, string sessionId, SessionAccessRecord access,
            DateTime lastAccessAt, DateTimeOffset now, DateTime nextExpiry)
        {
            var interval = _apiOptions.SessionTouchInterval;
            var utcNow = now.UtcDateTime;
            if (utcNow - lastAccessAt < interval)
                return;
            var throttle = Context.RequestServices.GetRequiredService<SessionTouchThrottle>();
            if (!throttle.TryBegin(sessionId, now, interval))
                return;
            try
            {
                // Not RequestAborted: a client that disconnects right after authenticating must not abort the write.
                using var timeout = new CancellationTokenSource(_apiOptions.SessionTouchTimeout, _clock);
                await source.TouchAsync(sessionId, utcNow, nextExpiry, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The session is still valid until its stored expiry: do not fail the request.
                throttle.Failed(sessionId, now + (interval < MaxTouchRetryDelay ? interval : MaxTouchRetryDelay));
                Logger.LogWarning(ex, "Could not extend the expiry of session {SessionId}.", sessionId);
                return;
            }
            _cache.TryUpdate(sessionId, access with { LastAccessAt = utcNow, ExpiresAt = nextExpiry });
        }

        /// <summary>The record's times are UTC by contract: Unspecified is taken as UTC, Local is converted.</summary>
        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => value,
        };

        private ClaimsPrincipal CreatePrincipal(string sessionId, SessionAccessRecord access)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, access.UserId),
                new(_apiOptions.SessionIdClaimType, sessionId),
                new(ClaimTypes.Name, access.UserName),
            };
            if (_apiOptions.UserIdClaimType != ClaimTypes.NameIdentifier)
                claims.Add(new Claim(_apiOptions.UserIdClaimType, access.UserId));
            if (!string.IsNullOrEmpty(access.Email))
                claims.Add(new Claim(ClaimTypes.Email, access.Email));
            claims.AddRange(access.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
            claims.AddRange(access.Permissions.Select(permission => new Claim(_apiOptions.PermissionClaimType, permission)));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role));
        }

        private AuthenticateResult Reject(string? reason, string message)
        {
            Context.Features.Set(new SessionRejection(reason, message));
            return AuthenticateResult.Fail(message);
        }
    }
}
