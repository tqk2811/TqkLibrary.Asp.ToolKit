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
        [InlineData(0)]
        [InlineData(-1)]
        public void NonPositiveDeviceKeyRegistrationWindow_Throws(int minutes)
            => Assert.Throws<InvalidOperationException>(() => new ApiAuthOptions { DeviceKeyRegistrationWindow = TimeSpan.FromMinutes(minutes) }.Validate());

        [Fact]
        public void NullDeviceKeyRegistrationWindow_IsAllowed() => new ApiAuthOptions { DeviceKeyRegistrationWindow = null }.Validate();

        [Fact]
        public void DefaultDeviceKeyRegistrationWindow_IsTenMinutes()
            => Assert.Equal(TimeSpan.FromMinutes(10), new ApiAuthOptions().DeviceKeyRegistrationWindow);

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

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void EmptyDeviceKeyJwkClaimType_Throws(string? value)
            => Assert.Throws<InvalidOperationException>(() => new ApiAuthOptions { DeviceKeyJwkClaimType = value! }.Validate());

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void EmptyDeviceKeyAlgorithmClaimType_Throws(string? value)
            => Assert.Throws<InvalidOperationException>(() => new ApiAuthOptions { DeviceKeyAlgorithmClaimType = value! }.Validate());

        [Fact]
        public void DeviceKeyClaimTypes_Defaults()
        {
            var options = new ApiAuthOptions();
            Assert.Equal("device_key_jwk", options.DeviceKeyJwkClaimType);
            Assert.Equal("device_key_alg", options.DeviceKeyAlgorithmClaimType);
        }
    }
}
