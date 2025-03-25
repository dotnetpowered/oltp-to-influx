using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Grpc.Core;
using Grpc.Core.Interceptors;
using System.Threading.Tasks;

public class Program
{
    public static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                // Load settings from appsettings.json
                var influxDbSettings = context.Configuration.GetSection("InfluxDB").Get<InfluxDbSettings>();
                if (influxDbSettings == null)
                {
                    throw new InvalidOperationException("InfluxDB settings not found in appsettings.json");
                }

                // Configure InfluxDB Client
                services.AddSingleton<IInfluxDBClient>(sp =>
                {
                    return new InfluxDBClient(influxDbSettings.Url, influxDbSettings.Token);
                });

                // Register settings for processors and authentication
                services.AddSingleton(influxDbSettings);

                // Configure OpenTelemetry with authentication
                services.AddOpenTelemetry()
                    .WithTracing(tracerProviderBuilder =>
                    {
                        tracerProviderBuilder
                            .SetResourceBuilder(ResourceBuilder.CreateDefault()
                                .AddService("CustomOtlpCollector"))
                            .AddOtlpExporter(options =>
                            {
                                // HTTP authentication
                                options.Endpoint = new Uri("http://0.0.0.0:4318"); // Change to https for TLS
                                options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                                options.Headers = $"Authorization=Bearer {influxDbSettings.ExpectedOtlpToken}";
                            })
                            .AddProcessor(new InfluxDbTraceProcessor(
                                services.BuildServiceProvider().GetRequiredService<IInfluxDBClient>(),
                                services.BuildServiceProvider().GetRequiredService<InfluxDbSettings>()));
                    })
                    .WithMetrics(meterProviderBuilder =>
                    {
                        meterProviderBuilder
                            .AddOtlpExporter(options =>
                            {
                                options.Endpoint = new Uri("http://0.0.0.0:4318");
                                options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                                options.Headers = $"Authorization=Bearer {influxDbSettings.ExpectedOtlpToken}";
                            })
                            .AddProcessor(new InfluxDbMetricsProcessor(
                                services.BuildServiceProvider().GetRequiredService<IInfluxDBClient>(),
                                services.BuildServiceProvider().GetRequiredService<InfluxDbSettings>()));
                    })
                    .WithLogging(loggingProviderBuilder =>
                    {
                        loggingProviderBuilder
                            .AddOtlpExporter(options =>
                            {
                                options.Endpoint = new Uri("http://0.0.0.0:4318");
                                options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                                options.Headers = $"Authorization=Bearer {influxDbSettings.ExpectedOtlpToken}";
                            })
                            .AddProcessor(new InfluxDbLogProcessor(
                                services.BuildServiceProvider().GetRequiredService<IInfluxDBClient>(),
                                services.BuildServiceProvider().GetRequiredService<InfluxDbSettings>()));
                    });

                // Add gRPC authentication interceptor
                services.AddSingleton<AuthInterceptor>(sp => 
                    new AuthInterceptor(influxDbSettings.ExpectedOtlpToken));
            })
            .Build();

        await host.RunAsync();
    }
}

// InfluxDB settings class with OTLP token
public class InfluxDbSettings
{
    public string Url { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Org { get; set; } = string.Empty;
    public string ExpectedOtlpToken { get; set; } = string.Empty; // Token clients must provide
}

// gRPC Authentication Interceptor
public class AuthInterceptor : Interceptor
{
    private readonly string _expectedToken;

    public AuthInterceptor(string expectedToken)
    {
        _expectedToken = expectedToken;
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var authHeader = context.RequestHeaders.GetValue("authorization");
        if (string.IsNullOrEmpty(authHeader) || authHeader != $"Bearer {_expectedToken}")
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid or missing token"));
        }
        return continuation(request, context);
    }
}

// HTTP Authentication Middleware (simplified for this example)
public class HttpAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _expectedToken;

    public HttpAuthMiddleware(RequestDelegate next, string expectedToken)
    {
        _next = next;
        _expectedToken = expectedToken;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("Authorization", out var authHeader) ||
            authHeader != $"Bearer {_expectedToken}")
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Invalid or missing token");
            return;
        }
        await _next(context);
    }
}

// Custom processor for traces
public class InfluxDbTraceProcessor : BaseProcessor<Activity>
{
    private readonly IInfluxDBClient _influxDbClient;
    private readonly InfluxDbSettings _settings;

    public InfluxDbTraceProcessor(IInfluxDBClient influxDbClient, InfluxDbSettings settings)
    {
        _influxDbClient = influxDbClient;
        _settings = settings;
    }

    public override async void OnEnd(Activity activity)
    {
        var writeApi = _influxDbClient.GetWriteApiAsync();
        var point = PointData
            .Measurement("traces")
            .Tag("operation", activity.DisplayName)
            .Tag("status", activity.Status.ToString())
            .Field("duration_ms", activity.Duration.TotalMilliseconds)
            .Timestamp(activity.StartTimeUtc, WritePrecision.Ms);

        await writeApi.WritePointAsync(point, _settings.Bucket, _settings.Org);
    }
}

// Custom processor for metrics
public class InfluxDbMetricsProcessor : BaseProcessor<Metric>
{
    private readonly IInfluxDBClient _influxDbClient;
    private readonly InfluxDbSettings _settings;

    public InfluxDbMetricsProcessor(IInfluxDBClient influxDbClient, InfluxDbSettings settings)
    {
        _influxDbClient = influxDbClient;
        _settings = settings;
    }

    public override async void OnEnd(Metric metric)
    {
        var writeApi = _influxDbClient.GetWriteApiAsync();
        foreach (var metricPoint in metric.GetMetricPoints())
        {
            var point = PointData
                .Measurement(metric.Name)
                .Field("value", GetMetricValue(metricPoint))
                .Timestamp(metricPoint.EndTime.UtcDateTime, WritePrecision.Ms);

            foreach (var tag in metricPoint.Tags)
            {
                point = point.Tag(tag.Key, tag.Value.ToString());
            }

            await writeApi.WritePointAsync(point, _settings.Bucket, _settings.Org);
        }
    }

    private double GetMetricValue(MetricPoint metricPoint)
    {
        return metricPoint.MetricType switch
        {
            MetricType.LongSum => metricPoint.GetSumLong(),
            MetricType.DoubleSum => metricPoint.GetSumDouble(),
            MetricType.LongGauge => metricPoint.GetGaugeLastValueLong(),
            MetricType.DoubleGauge => metricPoint.GetGaugeLastValueDouble(),
            _ => 0
        };
    }
}

// Custom processor for logs
public class InfluxDbLogProcessor : BaseProcessor<LogRecord>
{
    private readonly IInfluxDBClient _influxDbClient;
    private readonly InfluxDbSettings _settings;

    public InfluxDbLogProcessor(IInfluxDBClient influxDbClient, InfluxDbSettings settings)
    {
        _influxDbClient = influxDbClient;
        _settings = settings;
    }

    public override async void OnEnd(LogRecord logRecord)
    {
        var writeApi = _influxDbClient.GetWriteApiAsync();
        var point = PointData
            .Measurement("logs")
            .Tag("severity", logRecord.Severity.ToString())
            .Tag("category", logRecord.CategoryName ?? "unknown")
            .Field("message", logRecord.FormattedMessage ?? logRecord.Body.ToString())
            .Timestamp(logRecord.Timestamp, WritePrecision.Ms);

        if (logRecord.Attributes != null)
        {
            foreach (var attribute in logRecord.Attributes)
            {
                point = point.Tag(attribute.Key, attribute.Value?.ToString() ?? "null");
            }
        }

        await writeApi.WritePointAsync(point, _settings.Bucket, _settings.Org);
    }
}
