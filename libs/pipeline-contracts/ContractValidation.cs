using System.Collections.Frozen;
using System.Text.Json;

namespace ImagingPipeline.PipelineContracts;

/// <summary>Run-parameter checks shared by the contracts, so every contract reports errors the same way.</summary>
internal static class ContractValidation
{
    /// <summary>Rejects properties that are not in <paramref name="allowed"/>, and repeated ones.</summary>
    public static void ValidateProperties(
        JsonElement value,
        FrozenSet<string> allowed,
        List<ContractValidationError> errors,
        string prefix = "")
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                errors.Add(new(prefix + property.Name, "Unknown parameter."));
            else if (!seen.Add(property.Name))
                errors.Add(new(prefix + property.Name, "Duplicate parameters are not allowed."));
        }
    }

    public static string ReadRequiredString(JsonElement value, string name, List<ContractValidationError> errors)
    {
        if (value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()))
            return property.GetString()!;
        errors.Add(new(name, "Must be a nonempty string."));
        return string.Empty;
    }

    public static void ThrowIfInvalid(IReadOnlyCollection<ContractValidationError> errors, string parameterName)
    {
        if (errors.Count != 0)
            throw new ArgumentException(string.Join("; ", errors.Select(error => $"{error.Field}: {error.Message}")), parameterName);
    }
}
