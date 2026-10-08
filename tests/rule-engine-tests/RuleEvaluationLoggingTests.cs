using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Observability;
using ImagingPipeline.RuleEngine.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using static ImagingPipeline.RuleEngine.Tests.RuleTestData;

namespace ImagingPipeline.RuleEngine.Tests;

public sealed class RuleEvaluationLoggingTests
{
    [Fact]
    public void LogsAStaticMessageWithMatchedRulesAndMissReasonsAsFields()
    {
        var otherSensor = Rule("other-sensor");
        otherSensor.Match!.Sensors = [new SensorConfig { Name = "other-camera", RegistrationQualities = [RegistrationQuality.Accurate] }];
        var evaluation = new RuleMatcher(new FakeTimeProvider(Now))
            .Match(Image(), [ActiveRule(Rule("hit")), ActiveRule(otherSensor)]);
        var logger = new ScopeRecordingLogger();

        logger.LogRulesEvaluated(evaluation);

        var log = Assert.Single(logger.Entries);
        Assert.Equal(7002, log.EventId);
        Assert.Equal(LogLevel.Information, log.Level);
        Assert.Equal("Image evaluated against pipeline rules.", log.Message);
        Assert.Equal(["hit"], Assert.IsType<string[]>(log.Fields[TelemetryAttributeNames.RulesMatchedIds]));
        Assert.Equal(["other-sensor"], Assert.IsType<string[]>(log.Fields[TelemetryAttributeNames.RulesMissedSensorIds]));
        Assert.Equal(1, log.Fields[TelemetryAttributeNames.RulesMissedSensorCount]);
        Assert.Equal(0, log.Fields[TelemetryAttributeNames.RulesMissedGeometryCount]);
    }

    private sealed record LogEntry(int EventId, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Fields);

    /// <summary>Records each log entry with the fields of the scopes open when it was written.</summary>
    private sealed class ScopeRecordingLogger : ILogger
    {
        private readonly List<IEnumerable<KeyValuePair<string, object?>>> _scopes = [];

        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            var fields = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
            _scopes.Add(fields);
            return new Scope(() => _scopes.Remove(fields));
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = _scopes.SelectMany(scope => scope).ToDictionary(field => field.Key, field => field.Value);
            Entries.Add(new LogEntry(eventId.Id, logLevel, formatter(state, exception), fields));
        }

        private sealed class Scope(Action close) : IDisposable
        {
            public void Dispose() => close();
        }
    }
}
