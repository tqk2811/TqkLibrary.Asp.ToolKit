using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TqkLibrary.Asp.ToolKit.Auth.Web.Interfaces;

namespace TqkLibrary.Asp.ToolKit.Auth.Web
{
    /// <summary>
    /// Answers the two DBSC endpoints and short-circuits them (inserted by <c>UseTqkWebSession</c> only when
    /// <see cref="DeviceBoundSessionOptions.Enabled"/>, after <see cref="SessionExpiredMiddleware"/> and before
    /// <see cref="CurrentUserMiddleware"/>):
    /// <list type="bullet">
    ///   <item><see cref="DeviceBoundSessionOptions.RegistrationPath"/>: verifies the registration proof (challenge
    ///   issued at sign-in, signed by the key it carries), stores the key on the API
    ///   (<see cref="IDeviceKeyApiClient"/>), reissues the main cookie with the key, sets the proof cookie and returns
    ///   the session config. 400 on a bad request or proof, 403 when already bound or the API refuses, 503 when the API
    ///   fails.</item>
    ///   <item><see cref="DeviceBoundSessionOptions.RefreshPath"/>: unknown/unbound session → 200
    ///   <c>{"continue":false}</c>; no proof, expired or used challenge → 403 + <c>Secure-Session-Challenge</c>; bad
    ///   proof → 401 (the browser ends its DBSC session); valid → new proof cookie + 200 session config.</item>
    /// </list>
    /// Enforcement of the proof on ordinary requests is done by <see cref="CurrentUserMiddleware"/>.
    /// </summary>
    public sealed class DeviceBoundSessionMiddleware
    {
        internal const string RegistrationHeader = "Secure-Session-Registration";
        internal const string ChallengeHeader = "Secure-Session-Challenge";
        internal const string ResponseHeader = "Secure-Session-Response";
        internal const string SessionIdHeader = "Sec-Secure-Session-Id";
        internal const string SkippedHeader = "Secure-Session-Skipped";

        private readonly RequestDelegate _next;
        private readonly ILogger<DeviceBoundSessionMiddleware> _logger;

        public DeviceBoundSessionMiddleware(RequestDelegate next, ILogger<DeviceBoundSessionMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public Task InvokeAsync(HttpContext context)
        {
            var options = context.RequestServices.GetRequiredService<IOptions<WebAuthOptions>>().Value;
            var dbsc = options.DeviceBound;
            if (dbsc.Enabled)
            {
                var path = context.Request.Path;
                if (path.Equals(dbsc.RegistrationPath, StringComparison.OrdinalIgnoreCase))
                    return HandleAsync(context, options, registration: true);
                if (path.Equals(dbsc.RefreshPath, StringComparison.OrdinalIgnoreCase))
                    return HandleAsync(context, options, registration: false);
            }
            return _next(context);
        }

        private async Task HandleAsync(HttpContext context, WebAuthOptions options, bool registration)
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.Headers.Allow = "POST";
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }
            if (registration)
                await RegisterAsync(context, options).ConfigureAwait(false);
            else
                await RefreshAsync(context, options).ConfigureAwait(false);
        }

        private async Task RegisterAsync(HttpContext context, WebAuthOptions options)
        {
            var services = context.RequestServices;
            if (!context.Request.IsHttps)
            {
                await FailAsync(context, StatusCodes.Status400BadRequest, "not HTTPS").ConfigureAwait(false);
                return;
            }
            var ticket = await context.AuthenticateAsync(options.Scheme).ConfigureAwait(false);
            var sessionId = ticket.Succeeded ? ticket.Principal?.FindFirst(options.SessionIdClaimType)?.Value : null;
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(ticket.Properties?.GetTokenValue(WebSessionTicket.SessionTokenName)))
            {
                await FailAsync(context, StatusCodes.Status400BadRequest, "no session").ConfigureAwait(false);
                return;
            }
            if (!string.IsNullOrEmpty(ticket.Properties.GetTokenValue(WebSessionTicket.DeviceJwkName)))
            {
                await FailAsync(context, StatusCodes.Status403Forbidden, "session already bound").ConfigureAwait(false);
                return;
            }

            var proof = services.GetRequiredService<DeviceProofJwtVerifier>().VerifyRegistration(GetSingleHeader(context, ResponseHeader));
            if (!proof.IsValid)
            {
                await FailAsync(context, StatusCodes.Status400BadRequest, "proof " + proof.Error).ConfigureAwait(false);
                return;
            }
            // Signature already verified: only now may the challenge be consumed.
            var challenge = services.GetRequiredService<DeviceChallengeService>()
                .Validate(proof.Jti, DeviceChallengeKind.Registration, sessionId, consume: true);
            if (challenge != DeviceChallengeStatus.Valid)
            {
                await FailAsync(context, StatusCodes.Status400BadRequest, "challenge " + challenge).ConfigureAwait(false);
                return;
            }

            bool stored;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
            {
                timeout.CancelAfter(options.CurrentUserTimeout);
                try
                {
                    stored = await services.GetRequiredService<IDeviceKeyApiClient>()
                        .RegisterDeviceKeyAsync(proof.Key!.Jwk, proof.Key.Algorithm, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not SessionExpiredException && !context.RequestAborted.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "DBSC: could not store the device key of session {SessionId} on the API.", sessionId);
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }
            }
            if (!stored)
            {
                await FailAsync(context, StatusCodes.Status403Forbidden, "API refused the key").ConfigureAwait(false);
                return;
            }

            await services.GetRequiredService<WebSessionManager>().BindDeviceKeyAsync(context, ticket, sessionId, proof.Key)
                .ConfigureAwait(false);
            services.GetRequiredService<DeviceProofCookie>().Append(context, sessionId);
            _logger.LogInformation("DBSC: session {SessionId} bound to a device key ({Algorithm}).", sessionId, proof.Key.Algorithm);
            await WriteConfigAsync(context, options, sessionId).ConfigureAwait(false);
        }

        private async Task RefreshAsync(HttpContext context, WebAuthOptions options)
        {
            var services = context.RequestServices;
            var skipped = GetSingleHeader(context, SkippedHeader);
            if (!string.IsNullOrEmpty(skipped))
                _logger.LogInformation("DBSC: browser reported a skipped refresh: {Skipped}.", DeviceProofEnforcer.Sanitize(skipped));

            var ticket = await context.AuthenticateAsync(options.Scheme).ConfigureAwait(false);
            var sessionId = ticket.Succeeded ? ticket.Principal?.FindFirst(options.SessionIdClaimType)?.Value : null;
            var key = string.IsNullOrEmpty(sessionId) ? null : DeviceKey.TryParse(
                ticket.Properties?.GetTokenValue(WebSessionTicket.DeviceJwkName),
                ticket.Properties?.GetTokenValue(WebSessionTicket.DeviceAlgorithmName),
                options.DeviceBound.MinRsaKeySize);
            var browserSessionId = Unquote(GetSingleHeader(context, SessionIdHeader));
            if (key is null || string.IsNullOrEmpty(sessionId))
            {
                // Signed out, expired, or never bound: end the browser's DBSC session.
                services.GetRequiredService<DeviceProofCookie>().Delete(context);
                await WriteEndAsync(context, browserSessionId).ConfigureAwait(false);
                return;
            }
            var identifier = SessionIdentifier(sessionId);
            if (!string.Equals(browserSessionId, identifier, StringComparison.Ordinal))
            {
                // A DBSC session of an older web session: end it, leave the current proof cookie alone.
                await WriteEndAsync(context, browserSessionId).ConfigureAwait(false);
                return;
            }

            var challenges = services.GetRequiredService<DeviceChallengeService>();
            var jwt = GetSingleHeader(context, ResponseHeader);
            if (string.IsNullOrEmpty(jwt))
            {
                WriteChallenge(context, challenges, sessionId, identifier);
                return;
            }
            var proof = services.GetRequiredService<DeviceProofJwtVerifier>().VerifyRefresh(jwt, key);
            if (!proof.IsValid)
            {
                await FailAsync(context, StatusCodes.Status401Unauthorized, "refresh proof " + proof.Error).ConfigureAwait(false);
                return;
            }
            var challenge = challenges.Validate(proof.Jti, DeviceChallengeKind.Refresh, sessionId, consume: true);
            if (challenge != DeviceChallengeStatus.Valid)
            {
                // Genuine key, stale challenge (expired, used, other instance key ring...): let the browser retry.
                _logger.LogDebug("DBSC: refresh challenge of session {SessionId} is {Status}; sending a new one.", sessionId, challenge);
                WriteChallenge(context, challenges, sessionId, identifier);
                return;
            }
            services.GetRequiredService<DeviceProofCookie>().Append(context, sessionId);
            await WriteConfigAsync(context, options, sessionId).ConfigureAwait(false);
        }

        private Task FailAsync(HttpContext context, int status, string reason)
        {
            _logger.LogInformation("DBSC {Path} rejected ({Status}): {Reason}.", context.Request.Path, status, reason);
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        }

        private static void WriteChallenge(HttpContext context, DeviceChallengeService challenges, string sessionId, string identifier)
        {
            var challenge = challenges.Issue(DeviceChallengeKind.Refresh, sessionId);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.Headers[ChallengeHeader] = SfString(challenge) + ";id=" + SfString(identifier);
        }

        private static Task WriteEndAsync(HttpContext context, string? browserSessionId)
        {
            var body = new JsonObject();
            if (!string.IsNullOrEmpty(browserSessionId) && browserSessionId.Length <= 128 && browserSessionId.All(IsBase64UrlChar))
                body["session_identifier"] = browserSessionId;
            body["continue"] = false;
            return WriteJsonAsync(context, body);
        }

        private static Task WriteConfigAsync(HttpContext context, WebAuthOptions options, string sessionId)
        {
            var dbsc = options.DeviceBound;
            var request = context.Request;
            var scopeRules = new JsonArray();
            foreach (var excluded in dbsc.ExcludedPaths)
                scopeRules.Add(new JsonObject { ["type"] = "exclude", ["path"] = request.PathBase.Add(excluded).ToUriComponent() });
            var proofCookie = context.RequestServices.GetRequiredService<DeviceProofCookie>();
            var config = new JsonObject
            {
                ["session_identifier"] = SessionIdentifier(sessionId),
                ["refresh_url"] = request.PathBase.Add(dbsc.RefreshPath).ToUriComponent(),
                ["scope"] = new JsonObject
                {
                    ["origin"] = request.Scheme + "://" + request.Host.ToUriComponent(),
                    ["include_site"] = false,
                    ["scope_specification"] = scopeRules,
                },
                ["credentials"] = new JsonArray
                {
                    new JsonObject { ["type"] = "cookie", ["name"] = proofCookie.Name, ["attributes"] = DeviceProofCookie.Attributes },
                },
            };
            return WriteJsonAsync(context, config);
        }

        private static Task WriteJsonAsync(HttpContext context, JsonNode body)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            return context.Response.WriteAsync(body.ToJsonString());
        }

        /// <summary>
        /// The DBSC <c>session_identifier</c> of a web session: a hash of the API session id, so the id itself never
        /// travels in clear headers. Stable for the life of the session.
        /// </summary>
        internal static string SessionIdentifier(string sessionId)
            => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes("tqk-dbsc|" + sessionId)));

        /// <summary>A structured-field string (RFC 8941): quoted, with <c>\</c> and <c>"</c> escaped.</summary>
        internal static string SfString(string value)
            => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

        /// <summary>The header value when it is present exactly once; otherwise null.</summary>
        private static string? GetSingleHeader(HttpContext context, string name)
        {
            var values = context.Request.Headers[name];
            return values.Count == 1 ? values[0] : null;
        }

        /// <summary>Accepts a structured-field string (<c>"id"</c>) or a bare value.</summary>
        private static string? Unquote(string? value)
        {
            if (value is null)
                return null;
            value = value.Trim();
            return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
        }

        private static bool IsBase64UrlChar(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
    }
}
