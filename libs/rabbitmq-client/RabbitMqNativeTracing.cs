using System.Diagnostics;
using ImagingPipeline.Observability;
using RabbitMQ.Client;

namespace ImagingPipeline.RabbitMqClient;

internal static class RabbitMqNativeTracing
{
    public static IMessageTraceContextPropagator Propagator { get; } =
        new W3CMessageTraceContextPropagator();

    public static void Configure()
    {
        RabbitMQActivitySource.UseRoutingKeyAsOperationName = false;
        RabbitMQActivitySource.TracingOptions.UsePublisherAsParent = true;
        RabbitMQActivitySource.ContextInjector = Inject;
        RabbitMQActivitySource.ContextExtractor = Extract;
    }

    private static void Inject(Activity activity, IDictionary<string, object?> headers) =>
        Propagator.Inject(headers, activity.Context);

    private static ActivityContext Extract(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers is null)
        {
            return default;
        }

        var headers = properties.Headers is IReadOnlyDictionary<string, object?> readOnlyHeaders
            ? readOnlyHeaders
            : new Dictionary<string, object?>(properties.Headers, StringComparer.Ordinal);
        return Propagator.Extract(headers).ActivityContext;
    }
}
