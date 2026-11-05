using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TqkLibrary.Asp.ToolKit.Caching.Extensions;
using TqkLibrary.Asp.ToolKit.Caching.Remote;

using Xunit;

namespace TqkLibrary.Asp.ToolKit.Caching.Tests;

public sealed class CachingServiceCollectionExtensionsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static async Task<ListLogger> RunSenderAsync(Action<IServiceCollection> register)
    {
        var logger = new ListLogger();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logger));
        register(services);
        await using var provider = services.BuildServiceProvider();
        var sender = provider.GetServices<IHostedService>().OfType<HttpCacheInvalidationSender<TestGroup>>().Single();
        await sender.StartAsync(CancellationToken.None);
        await sender.ExecuteTask!.WaitAsync(Timeout);
        await sender.StopAsync(CancellationToken.None);
        return logger;
    }

    private static IConfigurationRoot NewConfiguration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public async Task Sender_Disabled_LogsWarningOnce()
    {
        var logger = await RunSenderAsync(s => s.AddTqkCacheInvalidationSender<TestGroup>(_ => { }));

        var disabled = logger.Entries.Where(e => e.Message.Contains("disabled", StringComparison.Ordinal)).ToArray();
        Assert.Single(disabled);
        Assert.Equal(LogLevel.Warning, disabled[0].Level);
    }

    [Fact]
    public async Task Sender_Disabled_LogLevelNone_LogsNothing()
    {
        var logger = await RunSenderAsync(s => s.AddTqkCacheInvalidationSender<TestGroup>(o => o.DisabledLogLevel = LogLevel.None));

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sender_FromSection_DisabledLogLevelIsBound()
    {
        var configuration = NewConfiguration(new() { ["Caching:Sender:DisabledLogLevel"] = "Information" });

        var logger = await RunSenderAsync(s => s.AddTqkCacheInvalidationSender<TestGroup>(configuration.GetSection("Caching:Sender")));

        var disabled = logger.Entries.Single(e => e.Message.Contains("disabled", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, disabled.Level);
    }

    [Fact]
    public void Sender_FromSection_BindsAndReloadsOnConfigurationChange()
    {
        var configuration = NewConfiguration(new()
        {
            ["Caching:Sender:TargetUrl"] = "http://localhost:5000/_internal/cache/invalidate",
            ["Caching:Sender:Key"] = "key-1",
            ["Caching:Sender:Debounce"] = "00:00:01",
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTqkCacheInvalidationSender<TestGroup>(configuration.GetSection("Caching:Sender"));
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<CacheInvalidationSenderOptions>>();

        Assert.Equal("key-1", monitor.CurrentValue.Key);
        Assert.Equal(TimeSpan.FromSeconds(1), monitor.CurrentValue.Debounce);
        Assert.True(monitor.CurrentValue.IsEnabled);

        configuration["Caching:Sender:Key"] = "key-2";
        configuration.Reload();

        Assert.Equal("key-2", monitor.CurrentValue.Key);
    }

    [Fact]
    public void Endpoint_FromSection_BindsAndReloadsOnConfigurationChange()
    {
        var configuration = NewConfiguration(new()
        {
            ["Caching:Endpoint:Key"] = "key-1",
            ["Caching:Endpoint:Path"] = "/custom/invalidate",
            ["Caching:Endpoint:RequireLoopback"] = "false",
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTqkCacheInvalidationEndpoint<TestGroup>(configuration.GetSection("Caching:Endpoint"));
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<CacheInvalidationEndpointOptions>>();

        Assert.Equal("key-1", monitor.CurrentValue.Key);
        Assert.Equal("/custom/invalidate", monitor.CurrentValue.Path);
        Assert.False(monitor.CurrentValue.RequireLoopback);
        Assert.NotNull(provider.GetService<CacheInvalidationEndpoint<TestGroup>>());

        configuration["Caching:Endpoint:Key"] = "key-2";
        configuration.Reload();

        Assert.Equal("key-2", monitor.CurrentValue.Key);
    }

    // ---------- list options: a configured list REPLACES the defaults ----------
    // The binder appends to a non-null collection, so a pre-filled default would only grow. Keys of the form
    // "Section:List:0" are exactly what the JSON provider produces for an array.

    private static TOptions BindOptions<TOptions>(Dictionary<string, string?> values, Action<IServiceCollection, IConfigurationSection> register)
        where TOptions : class
    {
        var configuration = NewConfiguration(values);
        var services = new ServiceCollection();
        services.AddLogging();
        register(services, configuration.GetSection("Caching"));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<TOptions>>().CurrentValue;
    }

    [Fact]
    public void Sender_FromSection_RetryDelaysListReplacesDefaults()
    {
        var options = BindOptions<CacheInvalidationSenderOptions>(
            new() { ["Caching:RetryDelays:0"] = "00:00:03" },
            (s, section) => s.AddTqkCacheInvalidationSender<TestGroup>(section));

        Assert.Equal([TimeSpan.FromSeconds(3)], options.EffectiveRetryDelays);
    }

    [Fact]
    public void Sender_FromSection_RetryDelaysNotConfigured_UsesDefaults()
    {
        var options = BindOptions<CacheInvalidationSenderOptions>(
            new() { ["Caching:Key"] = "k" },
            (s, section) => s.AddTqkCacheInvalidationSender<TestGroup>(section));

        Assert.Null(options.RetryDelays);
        Assert.Equal(CacheInvalidationSenderOptions.DefaultRetryDelays, options.EffectiveRetryDelays);
    }

    [Fact]
    public void Endpoint_FromSection_ForwardingHeadersListReplacesDefaults()
    {
        var options = BindOptions<CacheInvalidationEndpointOptions>(
            new() { ["Caching:ForwardingHeaders:0"] = "X-Custom-Proxy" },
            (s, section) => s.AddTqkCacheInvalidationEndpoint<TestGroup>(section));

        Assert.Equal(["X-Custom-Proxy"], options.EffectiveForwardingHeaders);
    }

    [Fact]
    public void Endpoint_FromSection_ForwardingHeadersNotConfigured_UsesDefaults()
    {
        var options = BindOptions<CacheInvalidationEndpointOptions>(
            new() { ["Caching:Key"] = "k" },
            (s, section) => s.AddTqkCacheInvalidationEndpoint<TestGroup>(section));

        Assert.Null(options.ForwardingHeaders);
        Assert.Equal(CacheInvalidationEndpointOptions.DefaultForwardingHeaders, options.EffectiveForwardingHeaders);
    }
}
