using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Xunit;

namespace BBT.Aether.Events;

public sealed class TopicNameFormatterTests
{
    [EventName("order.created", version: 2)]
    private sealed class OrderCreated;

    [EventName("order.shipped", topic: "orders.shipped")]
    private sealed class OrderShipped;

    [Fact]
    public void Topic_prefix_replaces_environment_prefix()
    {
        var options = new AetherEventBusOptions { PrefixEnvironmentToTopic = true, TopicPrefix = "vnext.core.production" };

        Assert.Equal(
            "vnext.core.production.order.created.v2",
            TopicNameFormatter.Format("order.created", 2, null, options, "Production"));
    }

    [Fact]
    public void Topic_prefix_trailing_dot_is_optional()
    {
        var options = new AetherEventBusOptions { TopicPrefix = "vnext.core.production." };

        Assert.Equal(
            "vnext.core.production.order.created.v2",
            TopicNameFormatter.Format("order.created", 2, null, options, "Production"));
    }

    [Fact]
    public void Topic_prefix_applies_to_explicit_topic()
    {
        var options = new AetherEventBusOptions { TopicPrefix = "vnext.core.production" };

        Assert.Equal(
            "vnext.core.production.orders.shipped",
            TopicNameFormatter.Format("order.shipped", 1, "orders.shipped", options, "Production"));
    }

    [Fact]
    public void Without_topic_prefix_environment_prefix_is_unchanged()
    {
        var options = new AetherEventBusOptions { PrefixEnvironmentToTopic = true };

        Assert.Equal(
            "production.order.created.v2",
            TopicNameFormatter.Format("order.created", 2, null, options, "Production"));
    }

    [Fact]
    public void Without_any_prefix_topic_is_bare()
    {
        var options = new AetherEventBusOptions { PrefixEnvironmentToTopic = false };

        Assert.Equal("order.created.v2", TopicNameFormatter.Format("order.created", 2, null, options, "Production"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("vnext.core.production")]
    public void Publisher_strategy_matches_formatter(string? topicPrefix)
    {
        var options = new AetherEventBusOptions { PrefixEnvironmentToTopic = true, TopicPrefix = topicPrefix };
        var strategy = new DefaultTopicNameStrategy(
            new HostingEnvironment { EnvironmentName = "Production" },
            options);

        Assert.Equal(
            TopicNameFormatter.Format("order.created", 2, null, options, "Production"),
            strategy.GetTopicName(typeof(OrderCreated)));
        Assert.Equal(
            TopicNameFormatter.Format("order.shipped", 1, "orders.shipped", options, "Production"),
            strategy.GetTopicName(typeof(OrderShipped)));
    }

    [Fact]
    public void Registered_strategy_sees_the_options_passed_to_AddAetherEventBus()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = "Development" });
        services.AddAetherEventBus(o => o.TopicPrefix = "vnext.core.development");

        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            "vnext.core.development.order.created.v2",
            provider.GetRequiredService<ITopicNameStrategy>().GetTopicName(typeof(OrderCreated)));
    }
}
