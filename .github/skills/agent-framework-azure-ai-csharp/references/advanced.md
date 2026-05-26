# Advanced Patterns Reference (.NET)

Structured output, OpenAPI tools, file outputs, dependency injection, middleware, and observability for Microsoft Foundry agents.

---

## 1. Structured Output

Bind the response to a strongly-typed class via `ChatResponseFormat.ForJsonSchema<T>()` and `RunAsync<T>(...)`.

```csharp
using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

public sealed class PersonInfo
{
    [JsonPropertyName("name")]       public string? Name { get; set; }
    [JsonPropertyName("age")]        public int? Age { get; set; }
    [JsonPropertyName("occupation")] public string? Occupation { get; set; }
}

AIAgent agent = projectClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = "PersonInfoExtractor",
    ChatOptions = new()
    {
        ModelId = deploymentName,
        Instructions = "Extract structured information about people.",
        ResponseFormat = ChatResponseFormat.ForJsonSchema<PersonInfo>(),
    },
});

AgentResponse<PersonInfo> response = await agent.RunAsync<PersonInfo>(
    "Please provide information about John Smith, a 35-year-old software engineer.");

PersonInfo info = response.Result;
Console.WriteLine($"{info.Name} ({info.Age}) — {info.Occupation}");
```

### Streaming structured output

`RunStreamingAsync<T>` yields incremental `AgentResponseUpdate<T>` values; the typed result is finalized at the end.

```csharp
await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(
    "Provide information about Marie Curie, the famous physicist."))
{
    Console.Write(update);
}
```

For complex schemas, also see `ChatResponseFormat.ForJsonSchema(jsonSchema, schemaName, schemaDescription)` to provide a custom schema document.

---

## 2. OpenAPI Tools

Expose any REST API as a tool by registering its OpenAPI spec.

```csharp
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using OpenAI.Responses;

OpenApiToolDefinition weatherApi = ResponseTool.CreateOpenApiTool(
    name: "WeatherAPI",
    description: "Query a public weather API.",
    spec: File.ReadAllText("specs/weather.openapi.json"),
    auth: OpenApiAuthDetails.Anonymous());     // .ManagedIdentity(), .Connection("conn-id"), .ApiKey(...)

ProjectsAgentVersion version = await projectClient.AgentAdministrationClient.CreateAgentVersionAsync(
    agentName: "WeatherAPIAgent",
    options: new ProjectsAgentVersionCreationOptions(new DeclarativeAgentDefinition(model: deploymentName)
    {
        Instructions = "Use the WeatherAPI for weather questions.",
        Tools = { ProjectsAgentTool.AsProjectTool(weatherApi) },
    }));

AIAgent agent = projectClient.AsAIAgent(version);
Console.WriteLine(await agent.RunAsync("What's the forecast for Seattle this weekend?"));
```

OpenAPI tools execute **on the Foundry service**; configure auth via `OpenApiAuthDetails`.

---

## 3. File Outputs from Code Interpreter

When `HostedCodeInterpreterTool` writes files, citations expose `OutputFileId`. Download via the project's OpenAI files client.

```csharp
using OpenAI.Files;
using Microsoft.Extensions.AI;

AIAgent agent = projectClient.AsAIAgent(
    deploymentName,
    instructions: "Use Python to generate analysis and CSV files.",
    name: "AnalysisAgent",
    tools: [new HostedCodeInterpreterTool { Inputs = [] }]);

AgentResponse response = await agent.RunAsync(
    "Generate a CSV with the first 100 Fibonacci numbers and save it as fib.csv.");

var files = projectClient.GetProjectOpenAIClient().GetProjectFilesClient();

foreach (AIAnnotation annotation in response.Messages
    .SelectMany(m => m.Contents).SelectMany(c => c.Annotations ?? []))
{
    if (annotation.RawRepresentation is TextAnnotationUpdate cite && cite.OutputFileId is { } fileId)
    {
        BinaryData content = await files.DownloadFileAsync(fileId);
        await File.WriteAllBytesAsync(cite.TextToReplace ?? $"{fileId}.bin", content.ToArray());
        Console.WriteLine($"  downloaded {fileId}");
    }
}
```

---

## 4. Dependency Injection

Wire the project client, agent, and providers through the generic host.

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());

builder.Services.AddSingleton(sp => new AIProjectClient(
    new Uri(builder.Configuration["AzureAI:ProjectEndpoint"]!),
    sp.GetRequiredService<TokenCredential>()));

builder.Services.AddSingleton<AIAgent>(sp =>
{
    var pc = sp.GetRequiredService<AIProjectClient>();
    return pc.AsAIAgent(
        builder.Configuration["AzureAI:ModelDeploymentName"]!,
        instructions: "You are a production assistant.",
        name: "ProdAgent");
});

builder.Services.AddHostedService<ChatWorker>();
IHost host = builder.Build();
await host.RunAsync();

internal sealed class ChatWorker(AIAgent agent, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AgentSession session = await agent.CreateSessionAsync();
        Console.WriteLine(await agent.RunAsync("Hello, what can you do?", session, stoppingToken));
        lifetime.StopApplication();
    }
}
```

---

## 5. Middleware via `DelegatingAIFunction`

Wrap any `AITool` derived from `AIFunction` to inject behaviour (logging, redaction, budget, retry):

```csharp
internal sealed class LoggingTool(AIFunction inner) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"[tool] {Name} called with: {arguments}");
        object? result = await base.InvokeCoreAsync(arguments, cancellationToken);
        Console.WriteLine($"[tool] {Name} returned: {result}");
        return result;
    }
}

AITool wrapped = new LoggingTool(AIFunctionFactory.Create(GetWeather));
```

This works on both function tools and MCP tools (`McpClientTool` is an `AIFunction`).

---

## 6. Observability (OpenTelemetry)

Enable OpenTelemetry to capture agent and tool calls as spans/metrics.

```csharp
using OpenTelemetry;
using OpenTelemetry.Trace;

using TracerProvider tracer = Sdk.CreateTracerProviderBuilder()
    .AddSource("Microsoft.Agents.AI*")
    .AddSource("Microsoft.Extensions.AI*")
    .AddOtlpExporter()
    .Build();

AIAgent agent = projectClient.AsAIAgent(
    deploymentName,
    instructions: "...",
    name: "ObservedAgent");

// Spans for RunAsync, tool invocations, MCP calls now flow to your OTLP collector.
await agent.RunAsync("Hello");
```

Combine with Azure Monitor / Application Insights by adding `AddAzureMonitorTraceExporter()` from `Azure.Monitor.OpenTelemetry.Exporter`.

---

## 7. Error Handling & Retries

The HTTP pipeline already retries on transient errors. For business-level retries (e.g. tool call returned an error message), drive the loop yourself:

```csharp
async Task<AgentResponse> RunWithBusinessRetries(AIAgent agent, string prompt, AgentSession session, int maxAttempts = 3)
{
    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        AgentResponse response = await agent.RunAsync(prompt, session);
        if (!response.Text.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
            return response;

        prompt = $"That attempt failed. Try a different approach. Original task: {prompt}";
    }
    throw new InvalidOperationException("Exhausted attempts.");
}
```

---

## 8. Cancellation

All `RunAsync` / `RunStreamingAsync` overloads accept a `CancellationToken`. Pass it through to honor user timeouts:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    AgentResponse response = await agent.RunAsync("Long task...", session, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Agent run cancelled.");
}
```

---

## 9. Cleanup of Persistent Resources

When you stop using a `FoundryAgent`, delete it to avoid clutter. Vector stores and uploaded files used by `HostedFileSearchTool` should also be cleaned up.

```csharp
// Persistent agent
projectClient.AgentAdministrationClient.DeleteAgent("MyPersistentAgent");

// File search artefacts
await vectorStores.DeleteVectorStoreAsync(vectorStoreId);
await files.DeleteFileAsync(uploadedFile.Id);

// Memory store
await memoryProvider.EnsureStoredMemoriesDeletedAsync();
await memoryProvider.EnsureMemoryStoreDeletedAsync();

// Toolbox
await toolboxes.DeleteToolboxVersionAsync("operations-toolbox", version.Version);
```

---

## 10. Putting It Together — Production Pattern

```csharp
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
builder.Services.AddSingleton(sp => new AIProjectClient(
    new Uri(builder.Configuration["AzureAI:ProjectEndpoint"]!),
    sp.GetRequiredService<TokenCredential>()));

builder.Services.AddSingleton<FoundryMemoryProvider>(sp => new FoundryMemoryProvider(
    sp.GetRequiredService<AIProjectClient>(),
    memoryStoreName: builder.Configuration["AzureAI:MemoryStoreName"]!,
    stateInitializer: _ => new(new FoundryMemoryProviderScope(userId: "tenant-default"))));

builder.Services.AddSingleton<AgentSkillsProvider>(_ => new AgentSkillsProviderBuilder()
    .AddDirectory(Path.Combine(AppContext.BaseDirectory, "skills"), SubprocessScriptRunner.RunAsync)
    .Build());

builder.Services.AddSingleton<AIAgent>(sp =>
{
    var pc = sp.GetRequiredService<AIProjectClient>();
    return pc.AsAIAgent(new ChatClientAgentOptions
    {
        Name = "ProductionAgent",
        ChatOptions = new()
        {
            ModelId = builder.Configuration["AzureAI:ModelDeploymentName"]!,
            Instructions = "You are a professional assistant.",
        },
        AIContextProviders =
        [
            sp.GetRequiredService<FoundryMemoryProvider>(),
            sp.GetRequiredService<AgentSkillsProvider>(),
        ],
    });
});

// Telemetry
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource("Microsoft.Agents.AI*")
        .AddSource("Microsoft.Extensions.AI*")
        .AddOtlpExporter());

await builder.Build().RunAsync();
```

This single `AIAgent` is safe to share across all incoming requests; each user gets their own `AgentSession`.
