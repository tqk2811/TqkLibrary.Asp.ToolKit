using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Tests
{
    /// <summary><see cref="SlidingSessionPolicy"/> and <see cref="AccessTokenFactory"/>.</summary>
    public class TokenIssuingTests
    {
        internal const string SigningKey = "test-signing-key-0123456789-abcdefghij";
        private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        internal static ApiAuthOptions NewOptions(Action<ApiAuthOptions>? configure = null)
        {
            var options = new ApiAuthOptions { Issuer = "test-issuer", SigningKey = SigningKey };
            configure?.Invoke(options);
            return options;
        }

        // ---------- SlidingSessionPolicy ----------

        [Fact]
        public void Initial_IsNowPlusRefreshLifetime()
        {
            var policy = new SlidingSessionPolicy(Options.Create(NewOptions(o => o.RefreshTokenLifetime = TimeSpan.FromDays(7))));
            Assert.Equal(Now.AddDays(7), policy.GetInitialExpiry(Now));
        }

        [Fact]
        public void Initial_MaxSessionShorterThanRefresh_IsCapped()
        {
            var policy = new SlidingSessionPolicy(Options.Create(NewOptions(o =>
            {
                o.RefreshTokenLifetime = TimeSpan.FromDays(7);
                o.MaxSessionLifetime = TimeSpan.FromDays(2);
            })));
            Assert.Equal(Now.AddDays(2), policy.GetInitialExpiry(Now));
        }

        [Fact]
        public void Next_WithoutCap_SlidesFromNow()
        {
            var policy = new SlidingSessionPolicy(Options.Create(NewOptions(o => o.RefreshTokenLifetime = TimeSpan.FromDays(7))));
            Assert.Equal(Now.AddDays(107), policy.GetNextExpiry(Now, Now.AddDays(100)));
        }

        [Fact]
        public void Next_SlidesUntilCap_ThenClamped_ThenNull()
        {
            var policy = new SlidingSessionPolicy(Options.Create(NewOptions(o =>
            {
                o.RefreshTokenLifetime = TimeSpan.FromDays(7);
                o.MaxSessionLifetime = TimeSpan.FromDays(30);
            })));

            Assert.Equal(Now.AddDays(17), policy.GetNextExpiry(Now, Now.AddDays(10)));
            Assert.Equal(Now.AddDays(30), policy.GetNextExpiry(Now, Now.AddDays(25)));
            Assert.Null(policy.GetNextExpiry(Now, Now.AddDays(30)));
            Assert.Null(policy.GetNextExpiry(Now, Now.AddDays(31)));
        }

        // ---------- AccessTokenFactory ----------

        [Fact]
        public async Task AccessToken_DefaultClaimTypes_ValidWithOwnValidationParameters()
        {
            var options = NewOptions(o => o.AccessTokenLifetime = TimeSpan.FromMinutes(5));
            var issuedAt = DateTime.UtcNow;
            var issued = new AccessTokenFactory(Options.Create(options)).Create("u-1", "s-1", issuedAt);

            Assert.Equal(issuedAt.AddMinutes(5), issued.ExpiresAt);
            var result = await new JsonWebTokenHandler().ValidateTokenAsync(issued.Token, options.CreateTokenValidationParameters());
            Assert.True(result.IsValid, result.Exception?.Message);

            var jwt = new JsonWebToken(issued.Token);
            Assert.Equal("u-1", jwt.GetClaim("sub").Value);
            Assert.Equal("s-1", jwt.GetClaim("sid").Value);
            Assert.False(string.IsNullOrEmpty(jwt.Id));
            Assert.Equal("test-issuer", jwt.Issuer);
            Assert.Equal(["test-issuer"], jwt.Audiences); // Audience defaults to Issuer
            Assert.Equal("HS256", jwt.Alg);
        }

        [Fact]
        public void AccessToken_ConfiguredClaimTypesAndAudience()
        {
            var options = NewOptions(o =>
            {
                o.UserIdClaimType = "uid";
                o.SessionIdClaimType = "app_sid";
                o.Audience = "test-audience";
            });
            var issued = new AccessTokenFactory(Options.Create(options)).Create("u-1", "s-1", Now);

            var jwt = new JsonWebToken(issued.Token);
            Assert.Equal("u-1", jwt.GetClaim("uid").Value);
            Assert.Equal("s-1", jwt.GetClaim("app_sid").Value);
            Assert.False(jwt.TryGetClaim("sub", out _));
            Assert.False(jwt.TryGetClaim("sid", out _));
            Assert.Equal(["test-audience"], jwt.Audiences);
            Assert.Equal(Now, jwt.IssuedAt);
            Assert.Equal(Now, jwt.ValidFrom);
            Assert.Equal(Now.AddMinutes(15), jwt.ValidTo);
        }

        [Fact]
        public void AccessToken_EachTokenHasOwnJti()
        {
            var factory = new AccessTokenFactory(Options.Create(NewOptions()));
            Assert.NotEqual(new JsonWebToken(factory.Create("u", "s", Now).Token).Id, new JsonWebToken(factory.Create("u", "s", Now).Token).Id);
        }

        [Fact]
        public void AccessToken_ShortKey_RejectedAtConstruction()
            => Assert.Throws<InvalidOperationException>(() => new AccessTokenFactory(Options.Create(NewOptions(o => o.SigningKey = "short"))));
    }
}
