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
            Assert.Equal(SessionTokenCodec.SecretLength, secret.Length); // 32 random bytes, base64url
            Assert.Equal(SessionTokenCodec.Hash(secret), issued.SecretHash);
            Assert.True(SessionTokenCodec.Verify(secret, issued.SecretHash));
        }

        [Fact]
        public void Create_TwoTokensForSameSession_HaveDifferentSecrets()
            => Assert.NotEqual(SessionTokenCodec.Create("s-1").Token, SessionTokenCodec.Create("s-1").Token);

        [Theory]
        [InlineData("")]
        [InlineData("a.b")]
        [InlineData("has space")]
        [InlineData("s/1")]
        [InlineData("s+1")]
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

        private static readonly string ValidSecret = new('a', SessionTokenCodec.SecretLength);

        [Fact]
        public void TryParse_ValidToken_True()
        {
            Assert.True(SessionTokenCodec.TryParse("s-1_X." + ValidSecret, out var sessionId, out var secret));
            Assert.Equal("s-1_X", sessionId);
            Assert.Equal(ValidSecret, secret);
        }

        [Fact]
        public void TryParse_SecretWithDot_False()
            => Assert.False(SessionTokenCodec.TryParse("s." + ValidSecret[..^2] + ".b", out _, out _));

        [Fact]
        public void TryParse_SessionIdTooLong_False()
        {
            Assert.True(SessionTokenCodec.TryParse(new string('s', SessionTokenCodec.MaxSessionIdLength) + "." + ValidSecret, out _, out _));
            Assert.False(SessionTokenCodec.TryParse(new string('s', SessionTokenCodec.MaxSessionIdLength + 1) + "." + ValidSecret, out _, out _));
        }

        [Theory]
        [InlineData("s 1")]
        [InlineData("s+1")]
        [InlineData("s/1")]
        [InlineData("s=1")]
        [InlineData("s\u00e9")]
        public void TryParse_SessionIdWithForbiddenChar_False(string sessionId)
            => Assert.False(SessionTokenCodec.TryParse(sessionId + "." + ValidSecret, out _, out _));

        [Theory]
        [InlineData(42)]
        [InlineData(44)]
        [InlineData(1)]
        public void TryParse_SecretOfWrongLength_False(int length)
            => Assert.False(SessionTokenCodec.TryParse("s." + new string('a', length), out _, out _));

        [Theory]
        [InlineData('+')]
        [InlineData('/')]
        [InlineData('=')]
        [InlineData(' ')]
        public void TryParse_SecretWithNonBase64UrlChar_False(char bad)
            => Assert.False(SessionTokenCodec.TryParse("s." + ValidSecret[..^1] + bad, out _, out _));

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
