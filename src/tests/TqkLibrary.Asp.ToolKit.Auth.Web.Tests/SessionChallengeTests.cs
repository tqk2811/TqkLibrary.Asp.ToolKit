using System.Net;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Web.Tests
{
    /// <summary><see cref="SessionChallenge"/> is public for proxies / hand-built clients and keeps the handler's rule.</summary>
    public class SessionChallengeTests
    {
        private static HttpResponseMessage Response(HttpStatusCode status, string? challenge = null)
        {
            var response = new HttpResponseMessage(status);
            if (challenge is not null)
                response.Headers.TryAddWithoutValidation("WWW-Authenticate", challenge);
            return response;
        }

        [Fact]
        public void IsPublicApi()
        {
            Assert.True(typeof(SessionChallenge).IsPublic);
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized, "Bearer error=\"invalid_token\"", false, true)]
        [InlineData(HttpStatusCode.Unauthorized, "bearer", false, true)]
        [InlineData(HttpStatusCode.Unauthorized, null, false, false)]
        [InlineData(HttpStatusCode.Unauthorized, "Basic realm=\"x\"", false, false)]
        [InlineData(HttpStatusCode.Unauthorized, null, true, true)]
        [InlineData(HttpStatusCode.Forbidden, "Bearer", false, false)]
        [InlineData(HttpStatusCode.Forbidden, null, true, false)]
        [InlineData(HttpStatusCode.OK, "Bearer", true, false)]
        public void EndsSession_FollowsTheHandlerRule(HttpStatusCode status, string? challenge, bool anyUnauthorized, bool expected)
        {
            using var response = Response(status, challenge);
            Assert.Equal(expected, SessionChallenge.EndsSession(response, anyUnauthorized));
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized, null, false, false)]
        [InlineData(HttpStatusCode.Unauthorized, null, true, true)]
        [InlineData(HttpStatusCode.Unauthorized, "Bearer", false, true)]
        [InlineData(HttpStatusCode.Forbidden, null, true, false)]
        public void EndsSession_Options_UsesTreatAnyUnauthorizedAsSessionExpired(HttpStatusCode status, string? challenge, bool anyUnauthorized, bool expected)
        {
            using var response = Response(status, challenge);
            var options = new WebAuthOptions { TreatAnyUnauthorizedAsSessionExpired = anyUnauthorized };
            Assert.Equal(expected, SessionChallenge.EndsSession(response, options));
        }

        [Fact]
        public void EndsSession_Options_NullArguments_Throw()
        {
            using var response = Response(HttpStatusCode.Unauthorized);
            Assert.Throws<ArgumentNullException>(() => SessionChallenge.EndsSession(response, (WebAuthOptions)null!));
            Assert.Throws<ArgumentNullException>(() => SessionChallenge.EndsSession(null!, new WebAuthOptions()));
        }

        [Fact]
        public void EndsSession_DefaultsToBearerChallengeOnly()
        {
            using var plain = Response(HttpStatusCode.Unauthorized);
            using var bearer = Response(HttpStatusCode.Unauthorized, "Bearer");
            Assert.False(SessionChallenge.EndsSession(plain));
            Assert.True(SessionChallenge.EndsSession(bearer));
        }

        [Fact]
        public void NullResponse_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => SessionChallenge.EndsSession(null!));
            Assert.Throws<ArgumentNullException>(() => SessionChallenge.IsSessionChallenge(null!));
        }
    }
}
