namespace ImagingPipeline.RuleMigrator;

internal sealed class MigrationOptions
{
    public const string SectionName = "Migration";

    /// <summary>The catalog pipeline whose rules index and contract the rules migrate to.</summary>
    public string Pipeline { get; set; } = string.Empty;

    /// <summary>The v1 index to read.</summary>
    public string SourceIndex { get; set; } = "rules";

    /// <summary>The photo age a v1 <c>isPhotoOld</c> rule gets as <c>match.maxPhotoAgeDays</c>.</summary>
    public int MaxPhotoAgeDays { get; set; } = 7;

    /// <summary>Writes the rules. Without it the run only reports what it would do.</summary>
    public bool Apply { get; set; }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Pipeline))
        {
            return "--pipeline is required.";
        }

        if (string.IsNullOrWhiteSpace(SourceIndex))
        {
            return "--source-index must not be empty.";
        }

        return MaxPhotoAgeDays <= 0 ? "--max-photo-age-days must be greater than 0." : null;
    }
}
