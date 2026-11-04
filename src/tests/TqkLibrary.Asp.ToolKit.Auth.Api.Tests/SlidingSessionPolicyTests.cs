using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TqkLibrary.Asp.ToolKit.Auth.Api.Extensions;
using Xunit;

namespace TqkLibrary.Asp.ToolKit.Auth.Api.Tests
{
    /// <summary><see cref="SlidingSessionPolicy"/> and <see cref="ApiAuthOptions.Validate"/>.</summary>
    public class SlidingSessionPolicyTests
    {
        private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        internal static ApiAuthOptions NewOptions(Action<ApiAuthOptions>? configure = null)
        {
            var options = new ApiAuthOptions();
            configure?.Invoke(options);
            return options;
        }

        [Fact]
        public void Initial_IsNowPlusRefreshLifetime()
        {
            var policy = new SlidingSessionPolicy(Options.Create(NewOptions(o => o.SessionLifetimes = new SessionLifetimes(TimeSpan.FromDays(7)))));
            Assert.Equal(Now.AddDays(7), policy.GetInitialExpiry(Now));
        }

        [Fact]
        public void Initial_MaxSessionShorterThanRefresh_IsCapped()
        {
            var policy = new SlidingSessionPolicy(Options.Create(NewOptions(o =>
            {
                o.SessionLifetimes = new SessionLifetimes(TimeSpan.FromDays(7));
                o.MaxSessionLifetime = TimeSpan.FromDays(2);
            })));
            Assert.Equal(Now.AddDays(2), policy.GetInitialExpiry(Now));
        }

        [Fact]
        public void Next_WithoutCap_SlidesFromNow()
        {
            var policy = new SlidingSessionPolicy(Options.Create(NewOptions(o => o.SessionLifetimes = new SessionLifetimes(TimeSpan.FromDays(7)))));
            Assert.Equal(Now.AddDays(107), policy.GetNextExpiry(Now, Now.AddDays(100)));
        }

        [Fact]
        public void Next_SlidesUntilCap_ThenClamped_ThenNull()
        {
            var policy = new SlidingSessionPolicy(Options.Create(NewOptions(o =>
            {
                o.SessionLifetimes = new SessionLifetimes(TimeSpan.FromDays(7));
                o.MaxSessionLifetime = TimeSpan.FromDays(30);
            })));

            Assert.Equal(Now.AddDays(17), policy.GetNextExpiry(Now, Now.AddDays(10)));
            Assert.Equal(Now.AddDays(30), policy.GetNextExpiry(Now, Now.AddDays(25)));
            Assert.Null(policy.GetNextExpiry(Now, Now.AddDays(30)));
            Assert.Null(policy.GetNextExpiry(Now, Now.AddDays(31)));
        }

        [Fact]
        public void Persistent_UsesPersistentLifetime_NullFallsBackToNormal()
        {
            var policy = new SlidingSessionPolicy(Options.Create(NewOptions(o =>
                o.SessionLifetimes = new SessionLifetimes(TimeSpan.FromDays(7), TimeSpan.FromDays(30)))));
            Assert.Equal(Now.AddDays(7), policy.GetInitialExpiry(Now));
            Assert.Equal(Now.AddDays(30), policy.GetInitialExpiry(Now, persistent: true));
            Assert.Equal(Now.AddDays(40), policy.GetNextExpiry(Now, Now.AddDays(10), persistent: true));

            var noPersistent = new SlidingSessionPolicy(Options.Create(NewOptions(o =>
                o.SessionLifetimes = new SessionLifetimes(TimeSpan.FromDays(7)))));
            Assert.Equal(Now.AddDays(7), noPersistent.GetInitialExpiry(Now, persistent: true));
        }

        [Fact]
        public void Persistent_IsCappedByMaxSessionLifetime()
        {
            var policy = new SlidingSessionPolicy(Options.Create(NewOptions(o =>
            {
                o.SessionLifetimes = new SessionLifetimes(TimeSpan.FromDays(7), TimeSpan.FromDays(30));
                o.MaxSessionLifetime = TimeSpan.FromDays(20);
            })));
            Assert.Equal(Now.AddDays(20), policy.GetInitialExpiry(Now, persistent: true));
            Assert.Null(policy.GetNextExpiry(Now, Now.AddDays(20), persistent: true));
        }

        [Fact]
        public void AbsoluteExpiry_IsCreatedPlusCap_NullWithoutCap()
        {
            var capped = new SlidingSessionPolicy(Options.Create(NewOptions(o => o.MaxSessionLifetime = TimeSpan.FromDays(90))));
            Assert.Equal(Now.AddDays(90), capped.GetAbsoluteExpiry(Now));
            Assert.Null(new SlidingSessionPolicy(Options.Create(NewOptions())).GetAbsoluteExpiry(Now));
        }

        [Theory]
        [InlineData(-1, 7, null)]
        [InlineData(7 * 24, 7, null)]
        [InlineData(3 * 24, 7, 2)]
        public void Validate_TouchIntervalNegativeOrNotShorterThanLifetime_Throws(int intervalHours, int normalDays, int? persistentDays)
        {
            var options = NewOptions(o =>
            {
                o.SessionTouchInterval = TimeSpan.FromHours(intervalHours);
                o.SessionLifetimes = new SessionLifetimes(TimeSpan.FromDays(normalDays),
                    persistentDays is { } p ? TimeSpan.FromDays(p) : null);
            });
            Assert.Throws<InvalidOperationException>(options.Validate);
        }

        [Fact]
        public void Validate_Defaults_Pass_TouchIntervalOneHour()
        {
            var options = NewOptions();
            options.Validate();
            Assert.Equal(TimeSpan.FromHours(1), options.SessionTouchInterval);
        }

        [Fact]
        public void DefaultLifetimes_AreThirtyDays()
        {
            var policy = new SlidingSessionPolicy(Options.Create(NewOptions()));
            Assert.Equal(Now.AddDays(30), policy.GetInitialExpiry(Now));
            Assert.Equal(Now.AddDays(30), policy.GetInitialExpiry(Now, persistent: true));
        }

        [Theory]
        [InlineData(0, null)]
        [InlineData(-1, null)]
        [InlineData(7, 0)]
        [InlineData(7, -1)]
        public void Validate_NonPositiveSessionLifetimes_Throws(int normalDays, int? persistentDays)
        {
            var options = NewOptions(o => o.SessionLifetimes = new SessionLifetimes(TimeSpan.FromDays(normalDays),
                persistentDays is { } days ? TimeSpan.FromDays(days) : null));
            Assert.Throws<InvalidOperationException>(options.Validate);
        }

        [Theory]
        [InlineData(3651, null, null)]
        [InlineData(7, 3651, null)]
        [InlineData(7, null, 3651)]
        public void Validate_LifetimeOverTenYears_Throws(int normalDays, int? persistentDays, int? maxDays)
        {
            var options = NewOptions(o =>
            {
                o.SessionLifetimes = new SessionLifetimes(TimeSpan.FromDays(normalDays),
                    persistentDays is { } p ? TimeSpan.FromDays(p) : null);
                o.MaxSessionLifetime = maxDays is { } m ? TimeSpan.FromDays(m) : null;
            });
            Assert.Throws<InvalidOperationException>(options.Validate);
        }

        [Fact]
        public void Validate_LifetimeAtTenYearLimit_Passes()
        {
            var options = NewOptions(o =>
            {
                o.SessionLifetimes = new SessionLifetimes(TimeSpan.FromDays(3650), TimeSpan.FromDays(3650));
                o.MaxSessionLifetime = TimeSpan.FromDays(3650);
            });
            options.Validate();
        }

        private sealed class FixedPolicy(IOptions<ApiAuthOptions> options) : SlidingSessionPolicy(options)
        {
            public override DateTime? GetNextExpiry(DateTime createdAtUtc, DateTime utcNow, bool persistent = false)
                => utcNow.AddHours(persistent ? 2 : 1);
        }

        [Fact]
        public void Subclass_RegisteredBeforeAddTqkApiAuth_Wins()
        {
            var services = new ServiceCollection();
            services.AddSingleton<SlidingSessionPolicy, FixedPolicy>();
            services.AddTqkApiAuth(_ => { });
            using var provider = services.BuildServiceProvider();
            var policy = provider.GetRequiredService<SlidingSessionPolicy>();
            Assert.IsType<FixedPolicy>(policy);
            Assert.Equal(Now.AddHours(2), policy.GetInitialExpiry(Now, persistent: true));
        }
    }
}
