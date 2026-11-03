using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Tests
{
    public class RefreshTokenCodecTests
    {
        [Fact]
        public void Create_FormatIsSessionDotSecret_AndHashMatchesSecret()
        {
            var issued = RefreshTokenCodec.Create("s-1");

            Assert.StartsWith("s-1.", issued.Token);
            Assert.True(RefreshTokenCodec.TryParse(issued.Token, out var sessionId, out var secret));
            Assert.Equal("s-1", sessionId);
            Assert.DoesNotContain('.', secret);
            Assert.True(secret.Length >= 43); // 32 random bytes, base64url
            Assert.Equal(RefreshTokenCodec.Hash(secret), issued.SecretHash);
            Assert.True(RefreshTokenCodec.Verify(secret, issued.SecretHash));
        }

        [Fact]
        public void Create_TwoTokensForSameSession_HaveDifferentSecrets()
            => Assert.NotEqual(RefreshTokenCodec.Create("s-1").Token, RefreshTokenCodec.Create("s-1").Token);

        [Theory]
        [InlineData("")]
        [InlineData("a.b")]
        public void Create_InvalidSessionId_Throws(string sessionId)
            => Assert.ThrowsAny<ArgumentException>(() => RefreshTokenCodec.Create(sessionId));

        [Fact]
        public void Verify_TamperedSecret_False()
        {
            var issued = RefreshTokenCodec.Create("s-1");
            RefreshTokenCodec.TryParse(issued.Token, out _, out var secret);
            var tampered = (secret[0] == 'A' ? 'B' : 'A') + secret[1..];

            Assert.False(RefreshTokenCodec.Verify(tampered, issued.SecretHash));
            Assert.False(RefreshTokenCodec.Verify(secret, null));
            Assert.False(RefreshTokenCodec.Verify(secret, issued.SecretHash[..^1]));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("nodot")]
        [InlineData(".secret")]
        [InlineData("session.")]
        public void TryParse_Malformed_False(string? token)
        {
            Assert.False(RefreshTokenCodec.TryParse(token, out var sessionId, out var secret));
            Assert.Equal(string.Empty, sessionId);
            Assert.Equal(string.Empty, secret);
        }

        [Fact]
        public void TryParse_SplitsAtFirstDot()
        {
            Assert.True(RefreshTokenCodec.TryParse("s.a.b", out var sessionId, out var secret));
            Assert.Equal("s", sessionId);
            Assert.Equal("a.b", secret);
        }

        [Fact]
        public void HashEquals_SameSecretTrue_DifferentOrNullFalse()
        {
            var hash = RefreshTokenCodec.Hash("secret");
            Assert.True(RefreshTokenCodec.HashEquals(hash, RefreshTokenCodec.Hash("secret")));
            Assert.False(RefreshTokenCodec.HashEquals(hash, RefreshTokenCodec.Hash("other-secret")));
            Assert.False(RefreshTokenCodec.HashEquals(hash, hash[..^1]));
            Assert.False(RefreshTokenCodec.HashEquals(hash, null));
            Assert.False(RefreshTokenCodec.HashEquals(null, null));
        }
    }
}
