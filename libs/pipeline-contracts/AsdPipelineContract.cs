using System.Buffers;
using System.Collections.Frozen;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Messaging;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.PipelineContracts;

public sealed class AsdPipelineContract : IPipelineContract
{
    // This marker belongs to the existing downstream AMQP wire protocol. It is independent
    // of the gateway's unversioned pipeline and contract IDs and must remain an AMQP integer.
    private static readonly FrozenDictionary<string, object?> RabbitMqAttributes =
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["findair-contract-version"] = 1
        }.ToFrozenDictionary(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly FrozenSet<string> RunParamFields =
        new[] { "tenantId", "algorithmNames", "tilingConfigs" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> TilingFields =
        new[] { "tileSizeWidth", "tileSizeHeight", "tileOverlapWidth", "tileOverlapHeight" }
            .ToFrozenSet(StringComparer.Ordinal);

    public string ContractId => "asd";

    public IReadOnlyList<ContractValidationError> ValidateRunParams(JsonElement runParams)
    {
        var errors = new List<ContractValidationError>();
        ParseRunParams(runParams, errors);
        return errors.AsReadOnly();
    }

    public PipelinePayload BuildPayload(PipelineDispatchContext context, JsonElement runParams, JsonElement extraData = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (extraData.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
        {
            throw new ArgumentException("Extra data must be a JSON object when provided.", nameof(extraData));
        }
        var errors = new List<ContractValidationError>();
        var parameters = ParseRunParams(runParams, errors);
        if (errors.Count != 0)
        {
            throw new ArgumentException(
                string.Join("; ", errors.Select(error => $"{error.Field}: {error.Message}")),
                nameof(runParams));
        }

        if (context.RoiFootprint.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("roiFootprint must be a GeoJSON object.", nameof(context));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("taskId", context.TaskId);
        writer.WriteString("ruleId", context.RuleId);
        writer.WriteStartArray("algorithmName");
        foreach (var algorithmName in parameters.AlgorithmNames)
        {
            writer.WriteStringValue(algorithmName.ToString());
        }
        writer.WriteEndArray();
        writer.WriteString("tenantId", parameters.TenantId);
        writer.WritePropertyName("tilingConfigs");
        JsonSerializer.Serialize(writer, parameters.TilingConfigs, JsonOptions);
        writer.WriteString("imageId", context.ImageId);
        writer.WritePropertyName("roiFootprint");
        writer.WriteRawValue(context.RoiFootprint.GetRawText());
        writer.WriteString("photoTime", context.PhotoTime);
        writer.WriteString("sensorType", context.SensorType);
        writer.WriteString("imageUrl", context.ImageUrl);
        writer.WriteNumber("imageWidth", context.ImageWidth);
        writer.WriteNumber("imageHeight", context.ImageHeight);
        writer.WriteNumber("bestResolution", context.BestResolution);
        writer.WriteString("sensorName", context.SensorName);
        writer.WriteString("areaOfInterest", context.AreaOfInterest);
        writer.WriteString("gridType", context.GridType);
        writer.WriteString("gridURI", context.GridUri);
        if (extraData.ValueKind == JsonValueKind.Object && extraData.EnumerateObject().MoveNext())
        {
            writer.WritePropertyName("extraData");
            extraData.WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.Flush();

        return new PipelinePayload(
            buffer.WrittenSpan.ToArray(),
            "application/json",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["algorithmName"] = string.Join(",", parameters.AlgorithmNames),
                ["tenantId"] = parameters.TenantId
            }.ToFrozenDictionary(StringComparer.Ordinal),
            RabbitMqAttributes);
    }

    private static AsdRunParameters ParseRunParams(JsonElement runParams, List<ContractValidationError> errors)
    {
        if (runParams.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("runParams", "Must be an object."));
            return new(string.Empty, [], []);
        }

        ValidateProperties(runParams, RunParamFields, string.Empty, errors);
        var tenantId = string.Empty;
        if (!runParams.TryGetProperty("tenantId", out var tenant) ||
            tenant.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tenant.GetString()))
        {
            errors.Add(new("tenantId", "Must be a nonempty string."));
        }
        else
        {
            tenantId = tenant.GetString()!;
        }

        var algorithms = ReadAlgorithms(runParams, errors);
        var tilingConfigs = ReadTilingConfigs(runParams, errors);
        return new(tenantId, algorithms, tilingConfigs);
    }

    private static List<AlgorithmName> ReadAlgorithms(JsonElement runParams, List<ContractValidationError> errors)
    {
        var result = new List<AlgorithmName>();
        if (!runParams.TryGetProperty("algorithmNames", out var algorithms) ||
            algorithms.ValueKind != JsonValueKind.Array || algorithms.GetArrayLength() == 0)
        {
            errors.Add(new("algorithmNames", "Must be a nonempty array."));
            return result;
        }

        var seen = new HashSet<AlgorithmName>();
        var index = 0;
        foreach (var value in algorithms.EnumerateArray())
        {
            var field = $"algorithmNames[{index++}]";
            try
            {
                // The shared converter accepts only the exact supported string values.
                var algorithm = value.Deserialize<AlgorithmName>();
                if (!seen.Add(algorithm))
                {
                    errors.Add(new(field, "Duplicate algorithm names are not allowed."));
                }
                result.Add(algorithm);
            }
            catch (JsonException)
            {
                errors.Add(new(field, $"Must be {AlgorithmNameContract.AllowedJsonValues}."));
            }
        }

        return result;
    }

    private static List<TilingConfig> ReadTilingConfigs(JsonElement runParams, List<ContractValidationError> errors)
    {
        var result = new List<TilingConfig>();
        if (!runParams.TryGetProperty("tilingConfigs", out var tilingConfigs) ||
            tilingConfigs.ValueKind != JsonValueKind.Array || tilingConfigs.GetArrayLength() == 0)
        {
            errors.Add(new("tilingConfigs", "Must be a nonempty array."));
            return result;
        }

        var index = 0;
        foreach (var value in tilingConfigs.EnumerateArray())
        {
            var field = $"tilingConfigs[{index++}]";
            if (value.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new(field, "Must be an object."));
                continue;
            }

            var errorsBefore = errors.Count;
            ValidateProperties(value, TilingFields, field + ".", errors);
            var tiling = new TilingConfig
            {
                TileSizeWidth = ReadInteger(value, "tileSizeWidth", field, errors),
                TileSizeHeight = ReadInteger(value, "tileSizeHeight", field, errors),
                TileOverlapWidth = ReadInteger(value, "tileOverlapWidth", field, errors),
                TileOverlapHeight = ReadInteger(value, "tileOverlapHeight", field, errors)
            };
            if (errorsBefore == errors.Count && !TilingConfigValidator.IsValid(tiling, out var error))
            {
                errors.Add(new(field, error));
            }
            result.Add(tiling);
        }

        return result;
    }

    private static int ReadInteger(
        JsonElement element,
        string propertyName,
        string field,
        List<ContractValidationError> errors)
    {
        if (element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        errors.Add(new($"{field}.{propertyName}", "Must be a 32-bit integer."));
        return 0;
    }

    private static void ValidateProperties(
        JsonElement element,
        FrozenSet<string> allowed,
        string prefix,
        List<ContractValidationError> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                errors.Add(new(prefix + property.Name, "Unknown parameter."));
            }
            else if (!seen.Add(property.Name))
            {
                errors.Add(new(prefix + property.Name, "Duplicate parameters are not allowed."));
            }
        }
    }

    private sealed record AsdRunParameters(
        string TenantId,
        List<AlgorithmName> AlgorithmNames,
        List<TilingConfig> TilingConfigs);
}
