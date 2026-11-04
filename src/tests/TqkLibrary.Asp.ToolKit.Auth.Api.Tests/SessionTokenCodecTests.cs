using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Tests
{
    public class SessionTokenCodecTests
    {
        [Fact]
        public void Create_FormatIsSessionDotSecret_AndHashMatchesSecret()
        {
            var issued = SessionTokenCodec.Create("s-1");

            Assert.StartsWith("s-1.", issued.Token);
            Assert.True(SessionTokenCodec.TryParse(issued.Token, out var sessionId, out var secret));
            Assert.Equal("s-1", sessionId);
            Assert.DoesNotContain('.', secret);
            Assert.True(secret.Length >= 43); // 32 random bytes, base64url
            Assert.Equal(SessionTokenCodec.Hash(secret), issued.SecretHash);
            Assert.True(SessionTokenCodec.Verify(secret, issued.SecretHash));
        }

        [Fact]
        public void Create_TwoTokensForSameSession_HaveDifferentSecrets()
            => Assert.NotEqual(SessionTokenCodec.Create("s-1").Token, SessionTokenCodec.Create("s-1").Token);

        [Theory]
        [InlineData("")]
        [InlineData("a.b")]
        public void Create_InvalidSessionId_Throws(string sessionId)
            => Assert.ThrowsAny<ArgumentException>(() => SessionTokenCodec.Create(sessionId));

        [Fact]
        public void Verify_TamperedSecret_False()
        {
            var issued = SessionTokenCodec.Create("s-1");
            SessionTokenCodec.TryParse(issued.Token, out _, out var secret);
            var tampered = (secret[0] == 'A' ? 'B' : 'A') + secret[1..];

            Assert.False(SessionTokenCodec.Verify(tampered, issued.SecretHash));
            Assert.False(SessionTokenCodec.Verify(secret, null));
            Assert.False(SessionTokenCodec.Verify(secret, issued.SecretHash[..^1]));
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
            Assert.False(SessionTokenCodec.TryParse(token, out var sessionId, out var secret));
            Assert.Equal(string.Empty, sessionId);
            Assert.Equal(string.Empty, secret);
        }

        [Fact]
        public void TryParse_TooLong_False()
            => Assert.False(SessionTokenCodec.TryParse("s." + new string('a', SessionTokenCodec.MaxTokenLength), out _, out _));

        [Fact]
        public void TryParse_SplitsAtFirstDot()
        {
            Assert.True(SessionTokenCodec.TryParse("s.a.b", out var sessionId, out var secret));
            Assert.Equal("s", sessionId);
            Assert.Equal("a.b", secret);
        }

        [Fact]
        public void HashEquals_SameSecretTrue_DifferentOrNullFalse()
        {
            var hash = SessionTokenCodec.Hash("secret");
            Assert.True(SessionTokenCodec.HashEquals(hash, SessionTokenCodec.Hash("secret")));
            Assert.False(SessionTokenCodec.HashEquals(hash, SessionTokenCodec.Hash("other-secret")));
            Assert.False(SessionTokenCodec.HashEquals(hash, hash[..^1]));
            Assert.False(SessionTokenCodec.HashEquals(hash, null));
            Assert.False(SessionTokenCodec.HashEquals(null, null));
        }
    }
}
