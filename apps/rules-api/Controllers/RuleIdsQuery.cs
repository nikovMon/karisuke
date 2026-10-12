namespace ImagingPipeline.Rules.Api.Controllers;

/// <summary>Parses the comma-separated <c>ids</c> query parameter of the bulk rule routes.</summary>
internal static class RuleIdsQuery
{
    public static IReadOnlyList<string> Parse(string? ids) =>
        string.IsNullOrWhiteSpace(ids)
            ? []
            : ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
