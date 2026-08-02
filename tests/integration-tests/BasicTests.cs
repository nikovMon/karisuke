using System.Text.Json;

namespace ImagingPipeline.IntegrationTests;

public sealed class BasicTests
{
    [Fact]
    public void GatewayOutputQueueDeclarationMatchesTbPublisherInputQueueDeclaration()
    {
        var repositoryRoot = FindRepositoryRoot();

        using var gatewayDocument = ReadAppSettings(repositoryRoot, "gateway");
        using var tbPublisherDocument = ReadAppSettings(repositoryRoot, "tb-publisher");

        var gatewayRabbitMq = gatewayDocument.RootElement.GetProperty("RabbitMq");
        var tbPublisherRabbitMq = tbPublisherDocument.RootElement.GetProperty("RabbitMq");

        Assert.Equal(
            GetRequiredString(gatewayRabbitMq, "OutputQueue"),
            GetRequiredString(tbPublisherRabbitMq, "InputQueue"));

        var outputQueueArguments = ReadArguments(gatewayRabbitMq, "OutputQueueHeaders");
        var inputQueueArguments = BuildInputQueueArguments(tbPublisherRabbitMq);

        Assert.Equal(
            inputQueueArguments.OrderBy(argument => argument.Key, StringComparer.Ordinal),
            outputQueueArguments.OrderBy(argument => argument.Key, StringComparer.Ordinal));
    }

    private static JsonDocument ReadAppSettings(string repositoryRoot, string applicationName)
    {
        var path = Path.Combine(repositoryRoot, "apps", applicationName, "appsettings.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static Dictionary<string, string> BuildInputQueueArguments(JsonElement rabbitMq)
    {
        var arguments = ReadArguments(rabbitMq, "HeadersArguments");

        arguments.TryAdd(
            "x-dead-letter-exchange",
            GetOptionalString(rabbitMq, "DeadLetterExchange"));

        arguments.TryAdd(
            "x-dead-letter-routing-key",
            GetOptionalString(rabbitMq, "DeadLetterRoutingKey") is { Length: > 0 } routingKey
                ? routingKey
                : arguments.GetValueOrDefault(
                    "x-dead-letter-routing-key",
                    GetRequiredString(rabbitMq, "DeadLetterQueue")));

        return arguments;
    }

    private static Dictionary<string, string> ReadArguments(JsonElement rabbitMq, string propertyName)
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!rabbitMq.TryGetProperty(propertyName, out var configuredArguments))
        {
            return arguments;
        }

        foreach (var argument in configuredArguments.EnumerateObject())
        {
            arguments.Add(argument.Name, NormalizeArgumentValue(argument.Value));
        }

        return arguments;
    }

    private static string NormalizeArgumentValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();

    private static string GetRequiredString(JsonElement section, string propertyName) =>
        section.GetProperty(propertyName).GetString()
        ?? throw new InvalidOperationException($"RabbitMq:{propertyName} must be a string.");

    private static string GetOptionalString(JsonElement section, string propertyName) =>
        section.TryGetProperty(propertyName, out var property)
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ImagingPipeline.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not find the repository root above '{AppContext.BaseDirectory}'.");
    }
}
