using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RuleMigrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string ApplyFlag = "--apply";
var switchMappings = new Dictionary<string, string>
{
    ["--pipeline"] = $"{MigrationOptions.SectionName}:{nameof(MigrationOptions.Pipeline)}",
    ["--source-schema"] = $"{MigrationOptions.SectionName}:{nameof(MigrationOptions.SourceSchema)}",
    ["--source-index"] = $"{MigrationOptions.SectionName}:{nameof(MigrationOptions.SourceIndex)}",
    ["--max-photo-age-days"] = $"{MigrationOptions.SectionName}:{nameof(MigrationOptions.MaxPhotoAgeDays)}"
};

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory
});
builder.Configuration.AddCommandLine([.. args.Where(arg => arg != ApplyFlag)], switchMappings);
builder.Configuration.AddPipelineCatalogFileAsBase(builder.Environment.ContentRootPath);
builder.Services.AddElasticsearchClient(builder.Configuration);
builder.Services.AddPipelineContracts();
builder.Services.AddPipelineCatalog(builder.Configuration);
builder.Services.AddSingleton<RuleMigration>();
using var host = builder.Build();

var options = builder.Configuration.GetSection(MigrationOptions.SectionName).Get<MigrationOptions>() ?? new();
options.Apply = args.Contains(ApplyFlag);
if (options.Validate() is { } optionsError)
{
    Console.Error.WriteLine(optionsError);
    Console.Error.WriteLine("Usage: rule-migrator --pipeline <id> [--source-schema v1|algo] [--source-index <index>] [--max-photo-age-days <days>] [--apply]");
    return 1;
}

var catalog = host.Services.GetRequiredService<IPipelineCatalog>();
if (!catalog.TryGet(options.Pipeline, out var pipeline))
{
    Console.Error.WriteLine($"Pipeline '{options.Pipeline}' is not in the catalog. Known: {string.Join(", ", catalog.GetAll().Select(p => p.PipelineId))}.");
    return 1;
}

var target = new MigrationTarget(
    pipeline.RulesIndex,
    host.Services.GetRequiredService<IPipelineContractRegistry>().GetRequired(pipeline.ContractId));
try
{
    var report = await host.Services.GetRequiredService<RuleMigration>().RunAsync(target, options, CancellationToken.None);
    report.Write(Console.Out, $"{options.EffectiveSourceIndex} ({options.SourceSchema.ToString().ToLowerInvariant()})", $"{pipeline.PipelineId} ({pipeline.RulesIndex})", options.Apply);
    return 0;
}
catch (ElasticsearchClientException exception)
{
    Console.Error.WriteLine($"Elasticsearch failed: {exception.Message}");
    return 1;
}
