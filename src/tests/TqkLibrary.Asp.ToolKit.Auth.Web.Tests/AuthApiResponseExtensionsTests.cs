using System.Net;
using TqkLibrary.Asp.ToolKit.Auth.Web.Extensions;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Tests
{
    /// <summary><see cref="AuthApiResponseExtensions.ThrowIfSessionRejected"/>: any 401 ends the session, a 403 does not.</summary>
    public class AuthApiResponseExtensionsTests
    {
        private static HttpResponseMessage Response(HttpStatusCode status, string? challenge = null)
        {
            var response = new HttpResponseMessage(status);
            if (challenge is not null)
                response.Headers.TryAddWithoutValidation("WWW-Authenticate", challenge);
            return response;
        }

        [Fact]
        public void Unauthorized_WithBearerChallenge_Throws()
        {
            using var response = Response(HttpStatusCode.Unauthorized, "Bearer error=\"invalid_token\"");
            Assert.Throws<SessionExpiredException>(() => response.ThrowIfSessionRejected());
        }

        /// <summary>A proxy may strip WWW-Authenticate; <c>me</c> has no business 401, so any 401 ends the session.</summary>
        [Fact]
        public void Unauthorized_WithoutChallenge_Throws()
        {
            using var response = Response(HttpStatusCode.Unauthorized);
            Assert.Throws<SessionExpiredException>(() => response.ThrowIfSessionRejected());
        }

        [Fact]
        public void Forbidden_DoesNotThrow()
        {
            using var response = Response(HttpStatusCode.Forbidden, "Bearer");
            response.ThrowIfSessionRejected();
        }
    }
}
