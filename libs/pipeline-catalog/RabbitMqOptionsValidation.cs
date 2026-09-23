using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace ImagingPipeline.PipelineCatalog;

public static class RabbitMqOptionsValidation
{
    private static readonly HashSet<string> StringArguments = new(StringComparer.Ordinal)
    {
        "x-dead-letter-exchange", "x-dead-letter-routing-key", "x-queue-type", "x-overflow", "x-match"
    };

    public static void ValidateQueue(RabbitMqQueueOptions? queue, string path, ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (queue is null)
        {
            errors.Add($"{path} is required.");
            return;
        }

        if (!IsAmqpName(queue.QueueName, allowEmpty: false))
        {
            errors.Add($"{path}:QueueName must be a nonblank AMQP queue name of at most 255 UTF-8 bytes.");
        }
        else if (queue.QueueName.StartsWith("amq.", StringComparison.Ordinal))
        {
            // RabbitMQ reserves this prefix for broker-owned queues.
            errors.Add($"{path}:QueueName must not start with the broker-reserved prefix 'amq.'.");
        }
        ValidateArguments(queue.Arguments, $"{path}:Arguments", errors);

        var exchange = queue.ExchangeSettings;
        if (exchange is null)
        {
            errors.Add($"{path}:ExchangeSettings must not be null.");
            return;
        }

        var prefix = $"{path}:ExchangeSettings";
        if (!IsAmqpName(exchange.ExchangeName, allowEmpty: true))
        {
            errors.Add($"{prefix}:ExchangeName must be empty or a nonblank AMQP exchange name of at most 255 UTF-8 bytes.");
        }
        if (exchange.ExchangeType is not ("direct" or "topic" or "fanout" or "headers"))
        {
            errors.Add($"{prefix}:ExchangeType must be 'direct', 'topic', 'fanout', or 'headers'.");
        }
        if (exchange.RoutingKey is null || exchange.RoutingKey.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(exchange.RoutingKey) > 255)
        {
            errors.Add($"{prefix}:RoutingKey must be a string of at most 255 UTF-8 bytes without control characters.");
        }
        if (exchange.ShouldBindToExchange && string.IsNullOrWhiteSpace(exchange.ExchangeName))
        {
            errors.Add($"{prefix}:ShouldBindToExchange requires a named exchange.");
        }
        if (!exchange.ShouldBindToExchange && exchange.BindingArguments is { Count: > 0 })
        {
            errors.Add($"{prefix}:BindingArguments require ShouldBindToExchange=true.");
        }

        ValidateArguments(exchange.Arguments, $"{prefix}:Arguments", errors);
        ValidateArguments(exchange.BindingArguments, $"{prefix}:BindingArguments", errors);
    }

    public static void ValidateScalarArgumentConfiguration(IConfigurationSection queueSection, ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(queueSection);
        ArgumentNullException.ThrowIfNull(errors);
        foreach (var suffix in new[] { "Arguments", "ExchangeSettings:Arguments", "ExchangeSettings:BindingArguments" })
        {
            var section = queueSection.GetSection(suffix);
            if (section.Value is not null)
            {
                errors.Add($"{section.Path} must be a dictionary of scalar argument values.");
            }

            var index = 0;
            foreach (var argument in section.GetChildren())
            {
                if (argument.GetChildren().Any() || argument.Value is null)
                {
                    errors.Add($"{section.Path}:entry[{index}] must be a non-null scalar; nested objects and arrays are unsupported.");
                }
                index++;
            }
        }
    }

    public static Dictionary<string, object?> NormalizeArguments(
        IReadOnlyDictionary<string, object?> arguments, string path)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var errors = new List<string>();
        ValidateArguments(arguments, path, errors);
        if (errors.Count != 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(arguments));
        }

        return arguments.ToDictionary(
            argument => argument.Key,
            argument => StringArguments.Contains(argument.Key) ? argument.Value : NormalizeScalar(argument.Value!),
            StringComparer.Ordinal);
    }

    public static RabbitMqQueueOptions NormalizeQueue(RabbitMqQueueOptions queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var errors = new List<string>();
        ValidateQueue(queue, nameof(queue), errors);
        if (errors.Count != 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(queue));
        }

        return queue with
        {
            Arguments = NormalizeArguments(queue.Arguments, "Arguments"),
            ExchangeSettings = queue.ExchangeSettings with
            {
                Arguments = NormalizeArguments(queue.ExchangeSettings.Arguments, "ExchangeSettings:Arguments"),
                BindingArguments = NormalizeArguments(queue.ExchangeSettings.BindingArguments, "ExchangeSettings:BindingArguments")
            }
        };
    }

    private static void ValidateArguments(IReadOnlyDictionary<string, object?>? arguments, string path, ICollection<string> errors)
    {
        if (arguments is null)
        {
            errors.Add($"{path} must not be null.");
            return;
        }

        var index = 0;
        foreach (var argument in arguments)
        {
            var field = $"{path}:entry[{index++}]";
            if (!IsAmqpName(argument.Key, allowEmpty: false))
            {
                errors.Add($"{field} must have a nonblank argument name of at most 255 UTF-8 bytes.");
            }
            if (argument.Value is not (string or bool or int or long or double) ||
                argument.Value is double number && !double.IsFinite(number))
            {
                errors.Add($"{field} must contain a string, Boolean, Int32, Int64, or finite Double scalar.");
            }
            else if (StringArguments.Contains(argument.Key) && argument.Value is not string)
            {
                errors.Add($"{field} requires a string value for this RabbitMQ argument.");
            }
        }
    }

    private static object NormalizeScalar(object value)
    {
        if (value is not string text)
        {
            return value;
        }
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longInteger)) return longInteger;
        if (bool.TryParse(text, out var boolean)) return boolean;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return number;
        return text;
    }

    private static bool IsAmqpName(string? value, bool allowEmpty) =>
        value is not null && (allowEmpty && value.Length == 0 ||
            !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl) &&
            string.Equals(value, value.Trim(), StringComparison.Ordinal)) &&
        Encoding.UTF8.GetByteCount(value) <= 255;
}
