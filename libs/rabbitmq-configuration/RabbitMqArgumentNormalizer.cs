using System.Collections.Frozen;
using System.Globalization;

namespace ImagingPipeline.RabbitMqConfiguration;

public enum RabbitMqArgumentConversionPolicy
{
    /// <summary>
    /// Preserves the runtime client's Int32/Boolean conversion for arbitrary argument keys.
    /// Larger integers and floating-point strings remain strings, including header binding values.
    /// </summary>
    Legacy,

    /// <summary>Preserves the catalog's additional Int64 and finite Double string conversions.</summary>
    ExtendedNumeric
}

/// <summary>
/// Copies and normalizes argument tables without broker access or validation. Known string
/// arguments remain strings; other string conversions use the selected compatibility policy
/// and invariant culture. Already typed values, including null, byte arrays and nested tables,
/// pass through unchanged. The copy is shallow and does not mutate those values.
/// </summary>
public static class RabbitMqArgumentNormalizer
{
    private static readonly FrozenSet<string> StringArguments = new[]
    {
        "x-dead-letter-exchange", "x-dead-letter-routing-key", "x-queue-type", "x-overflow", "x-match",
        "alternate-exchange", "x-delayed-type"
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsStringArgument(string key) => StringArguments.Contains(key);

    public static Dictionary<string, object?> Normalize(
        IEnumerable<KeyValuePair<string, object?>> arguments,
        RabbitMqArgumentConversionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (policy is not (RabbitMqArgumentConversionPolicy.Legacy or RabbitMqArgumentConversionPolicy.ExtendedNumeric))
            throw new ArgumentOutOfRangeException(nameof(policy));

        return arguments.ToDictionary(
            argument => argument.Key,
            argument => NormalizeValue(argument.Key, argument.Value, policy),
            StringComparer.Ordinal);
    }

    private static object? NormalizeValue(string key, object? value, RabbitMqArgumentConversionPolicy policy)
    {
        if (value is not string text || IsStringArgument(key)) return value;

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
        if (policy == RabbitMqArgumentConversionPolicy.ExtendedNumeric &&
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longInteger)) return longInteger;
        if (bool.TryParse(text, out var boolean)) return boolean;
        if (policy == RabbitMqArgumentConversionPolicy.ExtendedNumeric &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return number;
        return text;
    }
}
