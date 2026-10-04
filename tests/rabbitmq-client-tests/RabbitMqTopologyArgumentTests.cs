using System.Reflection;
using RabbitMQ.Client;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqTopologyArgumentTests
{
    [Fact]
    public async Task QueueExchangeAndBindingCallsUseKeyAwareLegacyConversion()
    {
        var bytes = new byte[] { 1, 2 };
        var nested = new Dictionary<string, object?> { ["nested-value"] = "123" };
        Dictionary<string, object?> Arguments() => new()
        {
            ["x-dead-letter-exchange"] = "123", ["x-dead-letter-routing-key"] = "true",
            ["alternate-exchange"] = "456", ["x-match"] = "all", ["x-message-ttl"] = "1000",
            ["custom-boolean"] = "true", ["custom-large"] = "2147483648", ["custom-fraction"] = "1.5",
            ["bytes"] = bytes, ["nested"] = nested, ["null"] = null, ["typed-large"] = 2147483648L
        };
        var options = new RabbitMqClientOptions
        {
            OutputQueue = "out", OutputExchange = "out-x", OutputExchangeType = "headers",
            OutputQueueHeaders = Arguments(), OutputExchangeHeaders = Arguments(), OutputBindingArguments = Arguments()
        };
        var channel = DispatchProxy.Create<IChannel, RecordingChannel>();
        var recorder = (RecordingChannel)channel;

        await RabbitMqTopology.DeclareOutputAsync(channel, options, CancellationToken.None);

        Assert.Equal(new[] { nameof(IChannel.ExchangeDeclareAsync), nameof(IChannel.QueueDeclareAsync), nameof(IChannel.QueueBindAsync) },
            recorder.Calls.Select(call => call.Method));
        foreach (var call in recorder.Calls)
        {
            var arguments = Assert.IsType<Dictionary<string, object?>>(call.Values["arguments"]);
            Assert.Equal("123", Assert.IsType<string>(arguments["x-dead-letter-exchange"]));
            Assert.Equal("true", Assert.IsType<string>(arguments["x-dead-letter-routing-key"]));
            Assert.Equal("456", Assert.IsType<string>(arguments["alternate-exchange"]));
            Assert.Equal("all", arguments["x-match"]);
            Assert.Equal(1000, Assert.IsType<int>(arguments["x-message-ttl"]));
            Assert.True(Assert.IsType<bool>(arguments["custom-boolean"]));
            Assert.Equal("2147483648", Assert.IsType<string>(arguments["custom-large"]));
            Assert.Equal("1.5", Assert.IsType<string>(arguments["custom-fraction"]));
            Assert.Equal(2147483648L, Assert.IsType<long>(arguments["typed-large"]));
            Assert.Same(bytes, arguments["bytes"]);
            Assert.Same(nested, arguments["nested"]);
            Assert.Null(arguments["null"]);
            arguments.Clear();
        }
        foreach (var original in new[] { options.OutputQueueHeaders, options.OutputExchangeHeaders, options.OutputBindingArguments })
        {
            Assert.Equal(12, original.Count);
            Assert.Equal("1000", original["x-message-ttl"]);
        }
        Assert.Equal("123", nested["nested-value"]);
    }

    [Fact]
    public async Task InputAndRetryGeneratedDeadLetterNamesRemainStrings()
    {
        var options = new RabbitMqClientOptions
        {
            InputQueue = "in", InputExchange = "123", InputRoutingKey = "true",
            DeadLetterQueue = "dead", DeadLetterExchange = "456", DeadLetterRoutingKey = "false",
            RetryQueue = "retry", RetryExchange = "retry-x", RetryDelayMilliseconds = 2500
        };
        var channel = DispatchProxy.Create<IChannel, RecordingChannel>();
        var recorder = (RecordingChannel)channel;

        await RabbitMqTopology.DeclareInputAsync(channel, options, CancellationToken.None);

        var input = QueueArguments("in");
        Assert.Equal("456", Assert.IsType<string>(input["x-dead-letter-exchange"]));
        Assert.Equal("false", Assert.IsType<string>(input["x-dead-letter-routing-key"]));
        var retry = QueueArguments("retry");
        Assert.Equal("123", Assert.IsType<string>(retry["x-dead-letter-exchange"]));
        Assert.Equal("true", Assert.IsType<string>(retry["x-dead-letter-routing-key"]));
        Assert.Equal(2500, Assert.IsType<int>(retry["x-message-ttl"]));
        Assert.Empty(options.HeadersArguments);
        Assert.Empty(options.RetryQueueHeaders);

        Dictionary<string, object?> QueueArguments(string queue) =>
            Assert.IsType<Dictionary<string, object?>>(recorder.Calls.Single(call =>
                call.Method == nameof(IChannel.QueueDeclareAsync) && Equals(call.Values["queue"], queue)).Values["arguments"]);
    }

    [Fact]
    public async Task EmptyRuntimeArgumentsArePassedAsNullToAllTopologyCalls()
    {
        var channel = DispatchProxy.Create<IChannel, RecordingChannel>();
        var options = new RabbitMqClientOptions { OutputQueue = "out", OutputExchange = "out-x" };

        await RabbitMqTopology.DeclareOutputAsync(channel, options, CancellationToken.None);

        var calls = ((RecordingChannel)channel).Calls;
        Assert.Equal(3, calls.Count);
        Assert.All(calls, call => Assert.Null(call.Values["arguments"]));
    }

    public sealed record ChannelCall(string Method, Dictionary<string, object?> Values);

    public class RecordingChannel : DispatchProxy
    {
        public List<ChannelCall> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            ArgumentNullException.ThrowIfNull(args);
            var values = targetMethod.GetParameters().Select((parameter, index) =>
                new KeyValuePair<string, object?>(parameter.Name!, args[index])).ToDictionary();
            Calls.Add(new(targetMethod.Name, values));
            return targetMethod.Name switch
            {
                nameof(IChannel.ExchangeDeclareAsync) or nameof(IChannel.QueueBindAsync) => Task.CompletedTask,
                nameof(IChannel.QueueDeclareAsync) => Task.FromResult(new QueueDeclareOk((string)values["queue"]!, 0, 0)),
                _ => throw new NotSupportedException($"Unexpected channel call {targetMethod.Name}.")
            };
        }
    }
}
