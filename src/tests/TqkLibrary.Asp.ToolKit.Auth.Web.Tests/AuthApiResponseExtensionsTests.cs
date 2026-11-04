using System.Net;
using TqkLibrary.Asp.ToolKit.Auth.Web.Extensions;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Tests
{
    /// <summary><see cref="AuthApiResponseExtensions.ThrowIfSessionRejected"/>: only a 401 with a Bearer challenge ends the session.</summary>
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

        [Fact]
        public void Unauthorized_WithoutChallenge_DoesNotThrow()
        {
            using var response = Response(HttpStatusCode.Unauthorized);
            response.ThrowIfSessionRejected();
        }

        [Fact]
        public void Forbidden_DoesNotThrow()
        {
            using var response = Response(HttpStatusCode.Forbidden, "Bearer");
            response.ThrowIfSessionRejected();
        }

        [Fact]
        public void TreatAnyUnauthorized_True_UnauthorizedWithoutChallenge_Throws()
        {
            using var response = Response(HttpStatusCode.Unauthorized);
            Assert.Throws<SessionExpiredException>(() => response.ThrowIfSessionRejected(treatAnyUnauthorizedAsSessionExpired: true));
        }

        [Fact]
        public void TreatAnyUnauthorized_True_Forbidden_StillDoesNotThrow()
        {
            using var response = Response(HttpStatusCode.Forbidden);
            response.ThrowIfSessionRejected(treatAnyUnauthorizedAsSessionExpired: true);
        }
    }
}
