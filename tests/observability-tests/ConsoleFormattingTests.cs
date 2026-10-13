using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Observability.Tests;

public sealed class ConsoleFormattingTests
{
    [Fact]
    public void StructuredConsoleFormatter_PreservesRuleIdArraysUtf8HeadersAndNumbers()
    {
        using var host = CreateHost();
        var formatter = host.Services.GetServices<ConsoleFormatter>()
            .Single(item => item.Name == StructuredJsonConsoleFormatter.FormatterName);
        var scopes = new LoggerExternalScopeProvider();
        using var scope = scopes.Push(new Dictionary<string, object?>
        {
            [TelemetryAttributeNames.RulesMatchedIds] = new[] { "כלל-א", "rule-b" },
            [TelemetryAttributeNames.RulesMissedSensorCount] = 2,
            ["UpdatedFields"] = new object[] { Encoding.UTF8.GetBytes("gridType"), Encoding.UTF8.GetBytes("שם") },
            [TelemetryAttributeNames.SensorName] = "מצלמה-🌍"
        });
        var entry = new LogEntry<string>(LogLevel.Information, "UnifiedGateway", new EventId(7002),
            "Image evaluated against pipeline rules.", null, (message, _) => message);
        using var writer = new StringWriter();

        formatter.Write(in entry, scopes, writer);

        using var json = JsonDocument.Parse(writer.ToString());
        var fields = json.RootElement.GetProperty("Scopes")[0];
        Assert.Equal(JsonValueKind.Array, fields.GetProperty(TelemetryAttributeNames.RulesMatchedIds).ValueKind);
        Assert.Equal("כלל-א", fields.GetProperty(TelemetryAttributeNames.RulesMatchedIds)[0].GetString());
        Assert.Equal(2, fields.GetProperty(TelemetryAttributeNames.RulesMissedSensorCount).GetInt32());
        Assert.Equal("gridType", fields.GetProperty("UpdatedFields")[0].GetString());
        Assert.Equal("שם", fields.GetProperty("UpdatedFields")[1].GetString());
        Assert.Equal("מצלמה-🌍", fields.GetProperty(TelemetryAttributeNames.SensorName).GetString());
        Assert.DoesNotContain("System.String[]", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("System.Byte[]", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConsoleLogging_UsesStructuredFormatterByDefault()
    {
        using var host = CreateHost();

        Assert.Equal(StructuredJsonConsoleFormatter.FormatterName,
            host.Services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName);
    }

    private static IHost CreateHost()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:Traces:Enabled"] = "false",
            ["Observability:Metrics:Enabled"] = "false",
            ["Observability:Logs:ConsoleEnabled"] = "true",
            ["Observability:Logs:Logstash:Enabled"] = "false"
        });
        builder.AddImagingPipelineObservability(ObservabilityServiceNames.UnifiedGateway);
        return builder.Build();
    }
}
