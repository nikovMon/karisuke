namespace ImagingPipeline.RuleMigrator;

/// <summary>The stored shape of the rules being migrated.</summary>
internal enum SourceSchema
{
    /// <summary>v1 <c>RuleDto</c>s, the old gateway's rules.</summary>
    V1,

    /// <summary>The old Algo system's rules.</summary>
    Algo
}

internal sealed class MigrationOptions
{
    public const string SectionName = "Migration";

    /// <summary>The catalog pipeline whose rules index and contract the rules migrate to.</summary>
    public string Pipeline { get; set; } = string.Empty;

    public SourceSchema SourceSchema { get; set; } = SourceSchema.V1;

    /// <summary>The index to read; defaults to the schema's usual index.</summary>
    public string? SourceIndex { get; set; }

    /// <summary>
    /// The photo age a rule marked as old-photo-sensitive gets as <c>match.maxPhotoAgeDays</c>;
    /// defaults to the old system's setting for the schema.
    /// </summary>
    public int? MaxPhotoAgeDays { get; set; }

    /// <summary>Writes the rules. Without it the run only reports what it would do.</summary>
    public bool Apply { get; set; }

    public string EffectiveSourceIndex => SourceIndex ?? (SourceSchema == SourceSchema.Algo ? "algo-rules" : "rules");

    public int EffectiveMaxPhotoAgeDays => MaxPhotoAgeDays ?? (SourceSchema == SourceSchema.Algo ? 2 : 7);

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Pipeline))
        {
            return "--pipeline is required.";
        }

        if (!Enum.IsDefined(SourceSchema))
        {
            return "--source-schema must be v1 or algo.";
        }

        if (SourceIndex is not null && string.IsNullOrWhiteSpace(SourceIndex))
        {
            return "--source-index must not be empty.";
        }

        return EffectiveMaxPhotoAgeDays <= 0 ? "--max-photo-age-days must be greater than 0." : null;
    }
}
