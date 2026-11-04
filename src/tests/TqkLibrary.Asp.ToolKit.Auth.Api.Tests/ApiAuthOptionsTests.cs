using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Tests
{
    public class ApiAuthOptionsTests
    {
        [Fact]
        public void Defaults_AreValid() => new ApiAuthOptions().Validate();

        [Fact]
        public void ZeroCacheLifetime_IsAllowed() => new ApiAuthOptions { SessionAccessCacheLifetime = TimeSpan.Zero }.Validate();

        [Fact]
        public void NegativeCacheLifetime_Throws()
            => Assert.Throws<InvalidOperationException>(() => new ApiAuthOptions { SessionAccessCacheLifetime = TimeSpan.FromSeconds(-1) }.Validate());

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void NonPositiveTouchTimeout_Throws(int seconds)
            => Assert.Throws<InvalidOperationException>(() => new ApiAuthOptions { SessionTouchTimeout = TimeSpan.FromSeconds(seconds) }.Validate());

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void EmptyDeviceBoundClaimType_Throws(string? value)
            => Assert.Throws<InvalidOperationException>(() => new ApiAuthOptions { DeviceBoundClaimType = value! }.Validate());

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void EmptyDeviceBindingModeClaimType_Throws(string? value)
            => Assert.Throws<InvalidOperationException>(() => new ApiAuthOptions { DeviceBindingModeClaimType = value! }.Validate());
    }
}
