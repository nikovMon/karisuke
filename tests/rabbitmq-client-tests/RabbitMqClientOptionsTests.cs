namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqClientOptionsTests
{
    [Fact]
    public void DefaultsMatchGatewayRabbitConfiguration()
    {
        var options = new RabbitMqClientOptions();

        Assert.Equal("localhost", options.Host);
        Assert.Equal(5672, options.Port);
        Assert.Equal("admin", options.Username);
        Assert.Equal("admin", options.Password);
        Assert.Equal("/", options.VirtualHost);
        Assert.Equal("int.algo.gateway_rules", options.InputQueue);
        Assert.Equal("int.algo.gateway_rules.output", options.OutputQueue);
        Assert.Equal("int.algo.gateway_rules.dlq", options.DeadLetterQueue);
        Assert.Equal(string.Empty, options.DeadLetterExchange);
        Assert.Equal((ushort)1, options.PrefetchCount);
        Assert.Equal((ushort)1, options.ConsumerConcurrency);
        Assert.Equal(4, options.OutputPublishConcurrency);
    }

    [Fact]
    public void EffectiveRoutingKeysFallbackToQueueNames()
    {
        var options = new RabbitMqClientOptions
        {
            InputQueue = "input",
            OutputQueue = "output",
            DeadLetterQueue = "dlq"
        };

        Assert.Equal("input", options.EffectiveInputRoutingKey);
        Assert.Equal("output", options.EffectiveOutputRoutingKey);
        Assert.Equal("dlq", options.EffectiveDeadLetterRoutingKey);
    }

    [Fact]
    public void ExplicitRoutingKeysWinOverQueueNames()
    {
        var options = new RabbitMqClientOptions
        {
            InputQueue = "input",
            OutputQueue = "output",
            DeadLetterQueue = "dlq",
            InputRoutingKey = "input.key",
            OutputRoutingKey = "output.key",
            DeadLetterRoutingKey = "dlq.key"
        };

        Assert.Equal("input.key", options.EffectiveInputRoutingKey);
        Assert.Equal("output.key", options.EffectiveOutputRoutingKey);
        Assert.Equal("dlq.key", options.EffectiveDeadLetterRoutingKey);
    }

    [Fact]
    public void WhitespaceRoutingKeysFallbackToQueueNames()
    {
        var options = new RabbitMqClientOptions
        {
            InputQueue = "input",
            OutputQueue = "output",
            DeadLetterQueue = "dlq",
            InputRoutingKey = " ",
            OutputRoutingKey = "\t",
            DeadLetterRoutingKey = "\r\n"
        };

        Assert.Equal("input", options.EffectiveInputRoutingKey);
        Assert.Equal("output", options.EffectiveOutputRoutingKey);
        Assert.Equal("dlq", options.EffectiveDeadLetterRoutingKey);
    }

    [Fact]
    public void HeaderArgumentsOverrideDeadLetterExchangeAndRoutingKey()
    {
        var options = new RabbitMqClientOptions
        {
            DeadLetterExchange = "configured.dlx",
            DeadLetterQueue = "configured.dlq",
            HeadersArguments =
            {
                ["x-dead-letter-exchange"] = "header.dlx",
                ["x-dead-letter-routing-key"] = "header.dlq"
            }
        };

        Assert.Equal("header.dlx", options.EffectiveDeadLetterExchange);
        Assert.Equal("header.dlq", options.EffectiveDeadLetterRoutingKey);
        Assert.Equal("configured.dlq", options.EffectiveDeadLetterQueue);
    }

    [Fact]
    public void HeaderArgumentsDecodeByteArrayDeadLetterSettings()
    {
        var options = new RabbitMqClientOptions
        {
            HeadersArguments =
            {
                ["x-dead-letter-exchange"] = System.Text.Encoding.UTF8.GetBytes("bytes.dlx"),
                ["x-dead-letter-routing-key"] = System.Text.Encoding.UTF8.GetBytes("bytes.dlq")
            }
        };

        Assert.Equal("bytes.dlx", options.EffectiveDeadLetterExchange);
        Assert.Equal("bytes.dlq", options.EffectiveDeadLetterRoutingKey);
        Assert.Equal("bytes.dlq", options.EffectiveDeadLetterQueue);
    }

    [Fact]
    public void ExplicitDeadLetterRoutingKeyWinsOverHeaderRoutingKey()
    {
        var options = new RabbitMqClientOptions
        {
            DeadLetterRoutingKey = "explicit.dlq",
            HeadersArguments =
            {
                ["x-dead-letter-routing-key"] = "header.dlq"
            }
        };

        Assert.Equal("explicit.dlq", options.EffectiveDeadLetterRoutingKey);
        Assert.Equal("explicit.dlq", options.EffectiveDeadLetterQueue);
    }

    [Fact]
    public void DefaultDeadLetterQueueTracksDeadLetterRoutingKey()
    {
        var options = new RabbitMqClientOptions
        {
            DeadLetterRoutingKey = "custom.dlq"
        };

        Assert.Equal("custom.dlq", options.EffectiveDeadLetterQueue);
    }

    [Fact]
    public void NonDefaultDeadLetterQueueDoesNotTrackRoutingKey()
    {
        var options = new RabbitMqClientOptions
        {
            DeadLetterQueue = "physical.dlq",
            DeadLetterRoutingKey = "route.dlq"
        };

        Assert.Equal("route.dlq", options.EffectiveDeadLetterRoutingKey);
        Assert.Equal("physical.dlq", options.EffectiveDeadLetterQueue);
    }

    [Theory]
    [InlineData("", 5672)]
    [InlineData("localhost", 0)]
    [InlineData("localhost", 65536)]
    public void PublisherValidationRejectsInvalidHostOrPort(string host, int port)
    {
        var options = new RabbitMqClientOptions
        {
            Host = host,
            Port = port
        };

        Assert.False(options.IsPublisherValid(out var error));
        Assert.Equal("RabbitMq Host and Port must be valid.", error);
    }

    [Fact]
    public void PublisherValidationRejectsEmptyInputQueue()
    {
        var options = new RabbitMqClientOptions
        {
            InputQueue = ""
        };

        Assert.False(options.IsPublisherValid(out var error));
        Assert.Equal("RabbitMq InputQueue must not be empty.", error);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    public void PublisherValidationRejectsInvalidPoolOrReconnectSettings(
        int publisherChannelPoolSize,
        int outputPublishConcurrency,
        int reconnectDelaySeconds)
    {
        var options = new RabbitMqClientOptions
        {
            PublisherChannelPoolSize = publisherChannelPoolSize,
            OutputPublishConcurrency = outputPublishConcurrency,
            ReconnectDelaySeconds = reconnectDelaySeconds
        };

        Assert.False(options.IsPublisherValid(out var error));
        Assert.Equal("RabbitMq publisher channel pool, output publish concurrency, and reconnect settings are outside their valid ranges.", error);
    }

    [Fact]
    public void PublisherValidationAcceptsDefaults()
    {
        var options = new RabbitMqClientOptions();

        Assert.True(options.IsPublisherValid(out var error));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void ConsumerValidationRejectsEmptyOutputQueue()
    {
        var options = new RabbitMqClientOptions
        {
            OutputQueue = ""
        };

        Assert.False(options.IsConsumerValid(out var error));
        Assert.Equal("RabbitMq OutputQueue and DeadLetterQueue must not be empty for consumers.", error);
    }

    [Fact]
    public void ConsumerValidationRejectsEmptyEffectiveDeadLetterQueue()
    {
        var options = new RabbitMqClientOptions
        {
            DeadLetterQueue = "",
            HeadersArguments =
            {
                ["x-dead-letter-routing-key"] = ""
            }
        };

        Assert.False(options.IsConsumerValid(out var error));
        Assert.Equal("RabbitMq OutputQueue and DeadLetterQueue must not be empty for consumers.", error);
    }

    [Fact]
    public void ConsumerValidationRejectsZeroPrefetch()
    {
        var options = new RabbitMqClientOptions
        {
            PrefetchCount = 0
        };

        Assert.False(options.IsConsumerValid(out var error));
        Assert.Equal("RabbitMq PrefetchCount and ConsumerConcurrency must be greater than zero for consumers.", error);
    }

    [Fact]
    public void ConsumerValidationRejectsZeroConsumerConcurrency()
    {
        var options = new RabbitMqClientOptions
        {
            ConsumerConcurrency = 0
        };

        Assert.False(options.IsConsumerValid(out var error));
        Assert.Equal("RabbitMq PrefetchCount and ConsumerConcurrency must be greater than zero for consumers.", error);
    }

    [Fact]
    public void ConsumerValidationAcceptsDefaults()
    {
        var options = new RabbitMqClientOptions();

        Assert.True(options.IsConsumerValid(out var error));
        Assert.Equal(string.Empty, error);
    }
}
