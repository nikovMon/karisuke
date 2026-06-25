namespace ImagingPipeline.Rules.Api.Configuration;

public sealed class RulesElasticsearchOptions
{
    public const string SectionName = "Rules";

    public string IndexName { get; set; } = "rules";

    internal bool IsValid(out string error)
    {
        if (string.IsNullOrWhiteSpace(IndexName))
        {
            error = "Rules IndexName must not be empty.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
