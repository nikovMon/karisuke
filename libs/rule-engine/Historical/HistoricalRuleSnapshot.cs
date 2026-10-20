using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RuleEngine.Loading;
using ImagingPipeline.RuleEngine.Rules;

namespace ImagingPipeline.RuleEngine.Historical;

/// <summary>Builds a saved rule for historical matching, retaining every condition except age.</summary>
public static class HistoricalRuleSnapshot
{
    public static ActiveRule Build(PipelineRuleDocument rule, IPipelineContract contract)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.Id);
        if (rule.Match?.MaxPhotoAgeDays is { } days && days > TimeSpan.MaxValue.TotalDays)
            throw new ArgumentException("Historical rule maxPhotoAgeDays exceeds the supported time span.", nameof(rule));

        // Validate the original document first: age can be its only match condition. Inactive
        // rules are allowed for explicit historical runs. The caller owns the frozen document.
        var snapshot = RuleSnapshotBuilder.Build(new RuleLoadResult([rule], []), contract);
        if (snapshot.Rules.Count != 1 || snapshot.Rejections.Count != 0)
        {
            throw new ArgumentException(
                $"Historical rule is invalid: {string.Join("; ", snapshot.Rejections.Select(rejection => rejection.Reason))}",
                nameof(rule));
        }

        return snapshot.Rules[0] with { MaxPhotoAge = null };
    }
}
