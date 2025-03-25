# oltp-to-influx

### Configuration Steps
1. **Set Up `appsettings.json`**:
   - Fill in all required values, including the `ExpectedOtlpToken`.

2. **Run the Collector**:
   - Use `dotnet run` to start the application.
   - It listens for OTLP data over gRPC on `localhost:4317` and HTTP on `localhost:4318`.

3. **Client Configuration**:
   - Clients must include the `Authorization: Bearer <expected-otlp-token>` header in their OTLP requests. For example:
     ```csharp
     tracerProviderBuilder.AddOtlpExporter(options =>
     {
         options.Endpoint = new Uri("http://localhost:4318");
         options.Headers = "Authorization=Bearer <expected-otlp-token>";
         options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
     });
     ```
