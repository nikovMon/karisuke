using ImagingPipeline.PipelineContracts;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace ImagingPipeline.PipelineCatalog;

public sealed class PipelineCatalogOptionsValidator(IPipelineContractRegistry contracts, IConfiguration? configuration = null)
    : IValidateOptions<PipelineCatalogOptions>
{
    public ValidateOptionsResult Validate(string? name, PipelineCatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        ValidateConnections(options.RabbitMqConnections, errors);
        if (options.Pipelines is null || options.Pipelines.Count == 0)
        {
            errors.Add("PipelineCatalog:Pipelines must contain at least one configured pipeline.");
            return ValidateOptionsResult.Fail(errors);
        }

        var pipelineIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in options.Pipelines)
        {
            var pipeline = entry.Value;
            var prefix = $"PipelineCatalog:Pipelines:{entry.Key}";
            if (!IsPipelineId(entry.Key))
            {
                errors.Add($"{prefix} has an invalid pipeline ID key. {PipelineIdRequirements}");
            }
            else if (!pipelineIds.Add(entry.Key))
            {
                errors.Add($"{prefix} duplicates another pipeline ID ignoring case.");
            }
            if (pipeline is null)
            {
                errors.Add($"{prefix} must not be null.");
                continue;
            }

            if (!IsIdentifier(pipeline.ContractId))
            {
                errors.Add($"{prefix}:ContractId must be nonempty with no surrounding whitespace or control characters.");
            }
            else if (!contracts.TryGet(pipeline.ContractId, out _))
            {
                errors.Add($"{prefix}:ContractId '{pipeline.ContractId}' is not registered.");
            }

            ValidateIndex(pipeline.RulesIndex, $"{prefix}:RulesIndex", errors);
            if (pipeline.ExtraData is null)
                errors.Add($"{prefix}:ExtraData must be a JSON object; omit it to use an empty object.");
            else if (contracts.TryGet(pipeline.ContractId, out var contract))
            {
                foreach (var error in contract.ValidateExtraData(pipeline.ExtraData.Value))
                    errors.Add($"{prefix}:ExtraData:{error.Field}: {error.Message}");
            }
            ValidateTransport(pipeline.Transport, $"{prefix}:Transport", errors);
            if (pipeline.Transport?.RabbitMq is { } rabbit &&
                IsIdentifier(rabbit.ConnectionRef) &&
                (options.RabbitMqConnections is null || !options.RabbitMqConnections.Keys.Contains(rabbit.ConnectionRef, StringComparer.Ordinal)))
            {
                errors.Add($"{prefix}:Transport:RabbitMq:ConnectionRef does not reference a configured RabbitMQ connection.");
            }
        }

        if (configuration is not null)
        {
            // Inspect original sections so malformed dictionary values cannot disappear during binding.
            var pipelines = configuration.GetSection($"{PipelineCatalogOptions.SectionName}:Pipelines");
            foreach (var pipeline in pipelines.GetChildren())
            {
                if (pipeline.GetSection("ExtraData").GetChildren().Any())
                    errors.Add($"{pipeline.Path}:ExtraData must be configured as one JSON object. Use the catalog JSON provider for files/streams, or replace the complete value with a JSON string in configuration overrides.");
                RabbitMqOptionsValidation.ValidateScalarArgumentConfiguration(
                    pipeline.GetSection("Transport:RabbitMq:Output"), errors);
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateConnections(
        IReadOnlyDictionary<string, RabbitMqConnectionOptions>? connections, ICollection<string> errors)
    {
        if (connections is null)
        {
            errors.Add("PipelineCatalog:RabbitMqConnections must not be null.");
            return;
        }

        var index = 0;
        foreach (var entry in connections)
        {
            var prefix = $"PipelineCatalog:RabbitMqConnections:entry[{index++}]";
            if (!IsIdentifier(entry.Key))
            {
                errors.Add($"{prefix} must have a nonempty connection reference without surrounding whitespace or control characters.");
            }
            var connection = entry.Value;
            if (connection is null)
            {
                errors.Add($"{prefix} must not be null.");
                continue;
            }

            if (!IsIdentifier(connection.Hostname) || Uri.CheckHostName(connection.Hostname) == UriHostNameType.Unknown)
                errors.Add($"{prefix}:Hostname must be a hostname or IP address without a scheme or path.");
            if (connection.Port is < 1 or > 65535)
                errors.Add($"{prefix}:Port must be between 1 and 65535.");
            if (string.IsNullOrWhiteSpace(connection.Username))
                errors.Add($"{prefix}:Username must not be empty.");
            if (string.IsNullOrEmpty(connection.Password))
                errors.Add($"{prefix}:Password must not be empty.");
            if (string.IsNullOrWhiteSpace(connection.VirtualHost))
                errors.Add($"{prefix}:VirtualHost must not be empty.");
        }
    }

    private static bool IsIdentifier([NotNullWhen(true)] string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    internal const string PipelineIdRequirements =
        "Pipeline IDs must contain only ASCII letters, digits, '-', '.', '_', or '~' so they can be used as one URL path segment. " +
        "They must not be empty, '.' or '..', contain '__', or end with '_' (reserved by environment-variable paths).";

    internal static bool IsPipelineId([NotNullWhen(true)] string? value) =>
        !string.IsNullOrEmpty(value) && value is not ("." or "..") &&
        !value.Contains("__", StringComparison.Ordinal) && !value.EndsWith('_') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~');

    private static void ValidateIndex(string? value, string field, List<string> errors)
    {
        // Literal references follow index-name constraints; expressions and date-math aliases are excluded.
        // https://www.elastic.co/guide/en/elasticsearch/reference/8.19/indices-create-index.html
        if (!IsIdentifier(value) || value is "." or ".." ||
            value[0] is '-' or '_' or '+' ||
            value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal) ||
            value.IndexOfAny(['*', '?', ',', '/', '\\', '#', ':', '"', '<', '>', '|']) >= 0 ||
            Encoding.UTF8.GetByteCount(value) > 255)
        {
            errors.Add($"{field} must be a lowercase literal index name or alias of at most 255 UTF-8 bytes, " +
                "without whitespace, control characters, reserved characters, or a leading '-', '_', or '+'. It cannot be '.' or '..'.");
        }
    }

    private static void ValidateTransport(PipelineTransportOptions? transport, string prefix, List<string> errors)
    {
        if (transport is null)
        {
            errors.Add($"{prefix} is required.");
            return;
        }

        switch (transport.Kind)
        {
            case "rabbitmq":
                if (transport.RabbitMq is null || transport.Http is not null)
                {
                    errors.Add($"{prefix} must contain RabbitMq settings and no Http settings for kind 'rabbitmq'.");
                    return;
                }

                if (!IsIdentifier(transport.RabbitMq.ConnectionRef))
                {
                    errors.Add($"{prefix}:RabbitMq:ConnectionRef must be a nonempty connection reference without surrounding whitespace or control characters.");
                }

                RabbitMqOptionsValidation.ValidateQueue(transport.RabbitMq.Output, $"{prefix}:RabbitMq:Output", errors);
                break;

            case "http":
                if (transport.Http is null || transport.RabbitMq is not null)
                {
                    errors.Add($"{prefix} must contain Http settings and no RabbitMq settings for kind 'http'.");
                    return;
                }

                if (!Uri.TryCreate(transport.Http.Endpoint, UriKind.Absolute, out var endpoint) ||
                    endpoint.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(endpoint.Host) ||
                    endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
                {
                    errors.Add($"{prefix}:Http:Endpoint must be an absolute HTTP(S) URL without embedded credentials or a fragment.");
                }

                if (!IsHttpMethod(transport.Http.Method))
                {
                    errors.Add($"{prefix}:Http:Method must be a valid HTTP method token.");
                }

                if (transport.Http.TimeoutSeconds is <= 0 or > HttpTransportOptions.MaximumTimerSeconds)
                {
                    errors.Add($"{prefix}:Http:TimeoutSeconds must be between 1 and {HttpTransportOptions.MaximumTimerSeconds}.");
                }
                HttpTransportValidation.ValidateHeaders(transport.Http.Headers, $"{prefix}:Http:Headers", errors);
                break;

            default:
                errors.Add($"{prefix}:Kind must be exactly 'http' or 'rabbitmq'.");
                break;
        }
    }

    private static bool IsHttpMethod(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return false;
        }

        try
        {
            _ = new HttpMethod(method);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
