namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqClientOptionsTests
{
    [Fact]
    public void DefaultsRequireExplicitQueueConfiguration()
    {
        var options = new RabbitMqClientOptions();

        Assert.Equal("localhost", options.Host);
        Assert.Equal(5672, options.Port);
        Assert.Equal("admin", options.Username);
        Assert.Equal("admin", options.Password);
        Assert.Equal("/", options.VirtualHost);
        Assert.Equal(string.Empty, options.InputQueue);
        Assert.Equal(string.Empty, options.OutputQueue);
        Assert.Equal(string.Empty, options.DeadLetterQueue);
        Assert.Equal(string.Empty, options.RetryQueue);
        Assert.Equal(string.Empty, options.DeadLetterExchange);
        Assert.Equal((ushort)1, options.PrefetchCount);
        Assert.Equal((ushort)1, options.ConsumerConcurrency);
        Assert.Equal(4, options.OutputPublishConcurrency);
        Assert.Equal(10000, options.RetryDelayMilliseconds);
        Assert.Equal(3, options.MaxRetryAttempts);
        Assert.Equal("x-retry-count", options.RetryCountHeader);
        Assert.Null(options.ForwardedInputStage);
    }

    [Fact]
    public void EffectiveRoutingKeysFallbackToQueueNames()
    {
        var options = new RabbitMqClientOptions
        {
            InputQueue = "input",
            OutputQueue = "output",
            DeadLetterQueue = "dlq",
            RetryQueue = "retry"
        };

        Assert.Equal("input", options.EffectiveInputRoutingKey);
        Assert.Equal("output", options.EffectiveOutputRoutingKey);
        Assert.Equal("retry", options.EffectiveRetryRoutingKey);
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
            RetryRoutingKey = "retry.key",
            DeadLetterRoutingKey = "dlq.key"
        };

        Assert.Equal("input.key", options.EffectiveInputRoutingKey);
        Assert.Equal("output.key", options.EffectiveOutputRoutingKey);
        Assert.Equal("retry.key", options.EffectiveRetryRoutingKey);
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
            RetryQueue = "retry",
            InputRoutingKey = " ",
            OutputRoutingKey = "\t",
            RetryRoutingKey = " ",
            DeadLetterRoutingKey = "\r\n"
        };

        Assert.Equal("input", options.EffectiveInputRoutingKey);
        Assert.Equal("output", options.EffectiveOutputRoutingKey);
        Assert.Equal("retry", options.EffectiveRetryRoutingKey);
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
            InputQueue = "input",
            PublisherChannelPoolSize = publisherChannelPoolSize,
            OutputPublishConcurrency = outputPublishConcurrency,
            ReconnectDelaySeconds = reconnectDelaySeconds
        };

        Assert.False(options.IsPublisherValid(out var error));
        Assert.Equal("RabbitMq publisher channel pool, output publish concurrency, and reconnect settings are outside their valid ranges.", error);
    }

    [Fact]
    public void PublisherValidationAcceptsExplicitInputQueue()
    {
        var options = new RabbitMqClientOptions
        {
            InputQueue = "input"
        };

        Assert.True(options.IsPublisherValid(out var error));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void ConsumerValidationRejectsEmptyOutputQueue()
    {
        var options = new RabbitMqClientOptions
        {
            InputQueue = "input",
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
            InputQueue = "input",
            OutputQueue = "output",
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
    public void ConsumerValidationRejectsEmptyLegacyRetryQueue()
    {
        var options = new RabbitMqClientOptions
        {
            InputQueue = "input",
            OutputQueue = "output",
            DeadLetterQueue = "dlq",
            RetryQueue = ""
        };

        Assert.False(options.IsConsumerValid(out var error));
        Assert.Equal("RabbitMq RetryQueue must not be empty when no attempt-specific RetryQueues are configured.", error);
    }

    [Fact]
    public void ConsumerValidationRejectsHeadersExchangeWithoutAttemptSpecificRetryQueues()
    {
        var options = new RabbitMqClientOptions
        {
            InputQueue = "input",
            OutputQueue = "output",
            DeadLetterQueue = "dlq",
            RetryQueue = "retry",
            RetryExchangeType = RabbitMQ.Client.ExchangeType.Headers
        };

        Assert.False(options.IsConsumerValid(out var error));
        Assert.Equal("RabbitMq RetryExchangeType cannot be headers unless attempt-specific RetryQueues are configured.", error);
    }

    [Fact]
    public void ConsumerValidationRejectsZeroPrefetch()
    {
        var options = new RabbitMqClientOptions
        {
            InputQueue = "input",
            OutputQueue = "output",
            DeadLetterQueue = "dlq",
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
            InputQueue = "input",
            OutputQueue = "output",
            DeadLetterQueue = "dlq",
            ConsumerConcurrency = 0
        };

        Assert.False(options.IsConsumerValid(out var error));
        Assert.Equal("RabbitMq PrefetchCount and ConsumerConcurrency must be greater than zero for consumers.", error);
    }

    [Theory]
    [InlineData(0, 3, "x-retry-count")]
    [InlineData(1000, -1, "x-retry-count")]
    [InlineData(1000, 3, " ")]
    public void ConsumerValidationRejectsInvalidRetryPolicy(
        int retryDelayMilliseconds,
        int maxRetryAttempts,
        string retryCountHeader)
    {
        var options = new RabbitMqClientOptions
        {
            InputQueue = "input",
            OutputQueue = "output",
            DeadLetterQueue = "dlq",
            RetryDelayMilliseconds = retryDelayMilliseconds,
            MaxRetryAttempts = maxRetryAttempts,
            RetryCountHeader = retryCountHeader
        };

        Assert.False(options.IsConsumerValid(out var error));
        Assert.Equal("RabbitMq retry delay, retry attempts, and retry count header are outside their valid ranges.", error);
    }

    [Fact]
    public void ConsumerValidationAcceptsAttemptSpecificRetryQueues()
    {
        var options = new RabbitMqClientOptions
        {
            InputQueue = "input",
            OutputQueue = "output",
            DeadLetterQueue = "dlq",
            RetryExchange = "retry.exchange",
            RetryExchangeType = RabbitMQ.Client.ExchangeType.Headers,
            RetryRoutingKey = "retry",
            MaxRetryAttempts = 2,
            RetryQueues =
            [
                new RabbitMqRetryQueueOptions
                {
                    RetryCount = 1,
                    Queue = "retry.1",
                    DelayMilliseconds = 1000
                },
                new RabbitMqRetryQueueOptions
                {
                    RetryCount = 2,
                    Queue = "retry.2",
                    DelayMilliseconds = 2000
                }
            ]
        };

        Assert.True(options.IsConsumerValid(out var error));
        Assert.Equal(string.Empty, error);
        Assert.Equal("retry", options.EffectiveRetryRoutingKey);
    }

    [Fact]
    public void ConsumerValidationRejectsMissingAttemptSpecificRetryQueue()
    {
        var options = new RabbitMqClientOptions
        {
            InputQueue = "input",
            OutputQueue = "output",
            DeadLetterQueue = "dlq",
            RetryExchange = "retry.exchange",
            RetryExchangeType = RabbitMQ.Client.ExchangeType.Headers,
            RetryRoutingKey = "retry",
            MaxRetryAttempts = 2,
            RetryQueues =
            [
                new RabbitMqRetryQueueOptions
                {
                    RetryCount = 1,
                    Queue = "retry.1",
                    RoutingKey = "retry.1.key",
                    DelayMilliseconds = 1000
                }
            ]
        };

        Assert.False(options.IsConsumerValid(out var error));
        Assert.Equal("RabbitMq RetryQueues must contain one valid queue per retry attempt.", error);
    }

    [Fact]
    public void ConsumerValidationRejectsAttemptSpecificRetryQueuesWithoutHeadersExchange()
    {
        var options = new RabbitMqClientOptions
        {
            InputQueue = "input",
            OutputQueue = "output",
            DeadLetterQueue = "dlq",
            RetryExchange = "retry.exchange",
            RetryExchangeType = RabbitMQ.Client.ExchangeType.Direct,
            MaxRetryAttempts = 1,
            RetryQueues =
            [
                new RabbitMqRetryQueueOptions
                {
                    RetryCount = 1,
                    Queue = "retry.1",
                    DelayMilliseconds = 1000
                }
            ]
        };

        Assert.False(options.IsConsumerValid(out var error));
        Assert.Equal("RabbitMq RetryQueues require a headers RetryExchange and a non-empty retry publish routing key.", error);
    }

    [Fact]
    public void ConsumerValidationAcceptsDefaultsWithExplicitDeadLetterAndRetryQueue()
    {
        var options = new RabbitMqClientOptions
        {
            InputQueue = "input",
            OutputQueue = "output",
            DeadLetterQueue = "dlq",
            RetryQueue = "retry"
        };

        Assert.True(options.IsConsumerValid(out var error));
        Assert.Equal(string.Empty, error);
    }
}
