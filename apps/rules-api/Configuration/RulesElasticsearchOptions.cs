namespace ImagingPipeline.Rules.Api.Configuration;

public sealed class RulesElasticsearchOptions
{
    public const string SectionName = "Elasticsearch";

    public string Index { get; set; } = "rules";

    /// <summary>Append-only index of v2 rule changes, shared by all pipelines.</summary>
    public string AuditIndex { get; set; } = "rules-audit";

    public int DefaultSearchSize { get; set; } = 100;

    public int MaxSearchSize { get; set; } = 1000;

    /// <summary>Most rule IDs one bulk request may change.</summary>
    public int MaxBulkSize { get; set; } = 500;

    internal string? ValidatePage(int from, int size) =>
        from < 0 || size <= 0 || size > MaxSearchSize
            ? $"from must be at least 0 and size must be between 1 and {MaxSearchSize}."
            : null;

    internal bool IsValid(out string error)
    {
        if (string.IsNullOrWhiteSpace(Index))
        {
            error = "Rules Elasticsearch Index must not be empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(AuditIndex))
        {
            error = "Rules Elasticsearch AuditIndex must not be empty.";
            return false;
        }

        if (DefaultSearchSize <= 0)
        {
            error = "Rules Elasticsearch DefaultSearchSize must be greater than 0.";
            return false;
        }

        if (MaxSearchSize < DefaultSearchSize)
        {
            error = "Rules Elasticsearch MaxSearchSize must be greater than or equal to DefaultSearchSize.";
            return false;
        }

        if (MaxBulkSize <= 0)
        {
            error = "Rules Elasticsearch MaxBulkSize must be greater than 0.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
