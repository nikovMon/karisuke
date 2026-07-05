namespace ImagingPipeline.Rules.Api.Configuration;

public sealed class RulesElasticsearchOptions
{
    public const string SectionName = "Rules";

    public string IndexName { get; set; } = "rules";

    public int DefaultSearchSize { get; set; } = 100;

    public int MaxSearchSize { get; set; } = 1000;

    internal bool IsValid(out string error)
    {
        if (string.IsNullOrWhiteSpace(IndexName))
        {
            error = "Rules IndexName must not be empty.";
            return false;
        }

        if (DefaultSearchSize <= 0)
        {
            error = "Rules DefaultSearchSize must be greater than 0.";
            return false;
        }

        if (MaxSearchSize < DefaultSearchSize)
        {
            error = "Rules MaxSearchSize must be greater than or equal to DefaultSearchSize.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
