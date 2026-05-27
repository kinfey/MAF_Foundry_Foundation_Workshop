---
name: agent-framework-agui-csharp
description: Build AG-UI protocol clients and servers with the Microsoft Agent Framework .NET SDK (Microsoft.Agents.AI.AGUI + Microsoft.Agents.AI.Hosting.AGUI.AspNetCore). Use when exposing an AIAgent over HTTP with server-sent events, building remote agent clients, supporting frontend tool rendering, agentic generative UI, predictive state updates, shared state, or human-in-the-loop UX. Covers AGUIChatClient, MapAGUI, AddAGUI, session storage, DelegatingAIAgent, and the AgentResponseUpdate streaming contract.
license: MIT
metadata:
  author: Microsoft
  version: "1.0.0"
  package: Microsoft.Agents.AI.AGUI
---

# Agent Framework AG-UI (.NET)

Expose any `AIAgent` over the [AG-UI protocol](https://docs.ag-ui.com/) and consume it from a remote client over server-sent events. AG-UI standardizes the wire format for `messages → tool calls → text deltas → state updates → errors` so frontends, mobile apps, and CLI clients can interop with any compliant agent server.

This SDK ships two pieces:

- **`Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`** — `app.MapAGUI(endpoint, agent)` extension that turns an `AIAgent` into an AG-UI server endpoint backed by SSE.
- **`Microsoft.Agents.AI.AGUI`** — `AGUIChatClient` that connects to an AG-UI endpoint and surfaces the stream as `AgentResponseUpdate`s through the normal `AIAgent` API.

## Architecture

```
┌──────────── AG-UI Client ─────────────┐         ┌──────────── AG-UI Server ─────────────┐
│                                        │         │                                        │
│  AGUIChatClient(httpClient, endpoint)  │  HTTP   │  builder.Services.AddAGUI()            │
│           ↓ .AsAIAgent(...)            │  POST   │  app.MapAGUI("/agentName", agent)      │
│  AIAgent  ──────────────────────────── │ ──────► │  AIAgent (ChatClientAgent /            │
│           agent.RunStreamingAsync(     │         │           DelegatingAIAgent / custom)  │
│             messages, session)         │  SSE    │           ↓                            │
│           ↓ async foreach              │ ◄────── │   AgentResponseUpdate stream:          │
│   AgentResponseUpdate                  │         │     TextContent / FunctionCallContent  │
│     .Contents                          │         │     FunctionResultContent /            │
│       TextContent                      │         │     DataContent / ErrorContent         │
│       FunctionCallContent (frontend)   │         │                                        │
│       FunctionResultContent            │         │  .WithInMemorySessionStore()           │
│       DataContent (state snapshot)     │         │  .WithSessionStore<TStore>()           │
│       ErrorContent                     │         │                                        │
│     .ConversationId  /  .ResponseId    │         │                                        │
└────────────────────────────────────────┘         └────────────────────────────────────────┘
```

Two highlights worth understanding before writing code:

- **Frontend tools.** Tools registered on the **client** (via `AIFunctionFactory.Create`) are exposed to the server's model as callable functions. The server emits `FunctionCallContent`; the client executes the .NET delegate locally and returns the result. This is how AG-UI does generative UI / browser-side actions.
- **Backend tools** registered on the **server** run server-side as usual. The server emits no client-side dispatch; the client just sees text deltas.

> **⚠ Frontend tools + GitHub Copilot CLI backbone.** When the server-side `AIAgent` is built from `CopilotClient.AsAIAgent(...)` (i.e. the model is the local GitHub Copilot CLI rather than an `IChatClient` such as Azure OpenAI), the CLI's tool list is frozen at `SessionConfig` time. Frontend tools advertised by the AG-UI client are not merged into the running Copilot session, so the model is likely to **hallucinate** the tool call ("notification sent") rather than dispatch `FunctionCallContent` back to the client. For deterministic, machine-checkable evidence that a client tool fired, expose a parallel non-AG-UI HTTP route (e.g. `app.MapPost("/scripted-reservation", ...)`) and have the client invoke its local delegate after a direct call to that route. See *GitHub Copilot CLI as the backbone* below.

## Installation

```bash
# Server (Azure OpenAI backbone)
dotnet add package Microsoft.Agents.AI.Hosting.AGUI.AspNetCore --prerelease
dotnet add package Microsoft.Agents.AI.OpenAI --prerelease
dotnet add package Azure.AI.OpenAI
dotnet add package Azure.Identity

# Server (GitHub Copilot CLI backbone — see section below)
dotnet add package Microsoft.Agents.AI.Hosting.AGUI.AspNetCore --prerelease
dotnet add package Microsoft.Agents.AI.GitHub.Copilot --prerelease   # GitHub.Copilot.SDK flows in transitively — do NOT add it directly
dotnet add package Microsoft.Agents.AI.Hosting --prerelease

# Client
dotnet add package Microsoft.Agents.AI --prerelease
dotnet add package Microsoft.Agents.AI.AGUI --prerelease
```

> Adding `GitHub.Copilot.SDK` as a direct `<PackageReference>` breaks restore with `CopilotCliVersion is not set` — `Microsoft.Agents.AI.GitHub.Copilot` ships the MSBuild props that set that property and pulls the SDK in for you. Stick to the framework package.

The server project's csproj uses `Microsoft.NET.Sdk.Web`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFrameworks>net10.0</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

## Prerequisites

- **.NET 10 SDK** or later
- **Azure OpenAI** deployment (or any `IChatClient`-compatible model)
- `az login` (server uses `DefaultAzureCredential` by default)
- Network reachability from client to server (default: `http://localhost:5100`)

## Environment Variables

```bash
# Server
export AZURE_OPENAI_ENDPOINT="https://<resource>.openai.azure.com/"
export AZURE_OPENAI_DEPLOYMENT_NAME="gpt-5.4-mini"

# Client
export AGUI_SERVER_URL="http://localhost:5100"
```

## Authentication & Lifecycle

> **🔑 Two rules apply to every code sample below:**
>
> 1. **Prefer `DefaultAzureCredential` / `AzureCliCredential`.** The server side authenticates to Azure OpenAI; the AGUI HTTP channel itself is plain HTTP/SSE (add your own auth middleware in production).
> 2. **Dispose `HttpClient` and `AGUIChatClient` consumers correctly.** `AGUIChatClient` takes ownership of the underlying transport via the `HttpClient` you supply; wrap it in `using` (or register it via `IHttpClientFactory`).

```csharp
using Azure.Identity;

// Server-side credential for Azure OpenAI
var credential = new DefaultAzureCredential();
// Production: prefer a specific identity to avoid latency from probing
// var credential = new ManagedIdentityCredential();
```

## Core Workflow

### Minimal Server (`MapAGUI`)

Mirrors [`AGUIServer/Program.cs`](https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/05-end-to-end/AGUIClientServer/AGUIServer/Program.cs).

```csharp
using System.ComponentModel;
using AGUIServer;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient().AddLogging();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Add(AGUIServerSerializerContext.Default));

// ⬇ The single line that wires AG-UI into the DI container.
builder.Services.AddAGUI();

string endpoint = builder.Configuration["AZURE_OPENAI_ENDPOINT"]
    ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set.");
string deploymentName = builder.Configuration["AZURE_OPENAI_DEPLOYMENT_NAME"]
    ?? throw new InvalidOperationException("AZURE_OPENAI_DEPLOYMENT_NAME is not set.");

const string AgentName = "AGUIAssistant";

var agent = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
    .GetChatClient(deploymentName)
    .AsAIAgent(
        name: AgentName,
        tools: [
            AIFunctionFactory.Create(
                () => DateTimeOffset.UtcNow,
                name: "get_current_time",
                description: "Get the current UTC time."),
            AIFunctionFactory.Create(
                ([Description("The weather forecast request")] ServerWeatherForecastRequest request) =>
                    new ServerWeatherForecastResponse
                    {
                        Summary = "Sunny",
                        TemperatureC = 25,
                        Date = request.Date,
                    },
                name: "get_server_weather_forecast",
                description: "Gets the forecast for a specific location and date",
                AGUIServerSerializerContext.Default.Options),
        ]);

builder
    .AddAIAgent(AgentName, (_, _) => agent)
    .WithInMemorySessionStore();   // session storage (swap for persistent store in prod)

WebApplication app = builder.Build();
app.MapAGUI(AgentName, "/");
await app.RunAsync();
```

Run:

```bash
dotnet run --urls "http://localhost:5100"
```

### Minimal Client (`AGUIChatClient`)

Mirrors [`AGUIClient/Program.cs`](https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/05-end-to-end/AGUIClientServer/AGUIClient/Program.cs).

```csharp
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AGUI;
using Microsoft.Extensions.AI;

string serverUrl = Environment.GetEnvironmentVariable("AGUI_SERVER_URL") ?? "http://localhost:5100";

using HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

var chatClient = new AGUIChatClient(
    httpClient,
    serverUrl,
    jsonSerializerOptions: AGUIClientSerializerContext.Default.Options);

AIAgent agent = chatClient.AsAIAgent(
    name: "agui-client",
    description: "AG-UI Client Agent",
    tools: []);                     // frontend tools go here

AgentSession session = await agent.CreateSessionAsync();
List<ChatMessage> messages = [new(ChatRole.System, "You are a helpful assistant.")];

while (true)
{
    Console.Write("\nUser (:q to exit): ");
    string? line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line) || line is ":q" or "quit") { break; }

    messages.Add(new(ChatRole.User, line));

    await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session))
    {
        foreach (AIContent content in update.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    Console.Write(text.Text);
                    break;
                case ErrorContent err:
                    Console.Error.WriteLine($"\n[Error] {err.Message}");
                    break;
            }
        }
    }
    messages.Clear();
}
```

### Frontend Tools (Tools Defined on the Client)

The client registers `AIFunction`s; the server's model sees them in the tool list and can call them. The .NET delegate executes **on the client** and the result is sent back.

```csharp
var changeBackground = AIFunctionFactory.Create(
    () =>
    {
        Console.ForegroundColor = ConsoleColor.DarkBlue;
        Console.WriteLine("Changing color to blue");
    },
    name: "change_background_color",
    description: "Change the console background color to dark blue.");

var readClientClimateSensors = AIFunctionFactory.Create(
    ([Description("The sensors measurements to include in the response")] SensorRequest request) =>
        new SensorResponse { Temperature = 22.5, Humidity = 45.0, AirQualityIndex = 75 },
    name: "read_client_climate_sensors",
    description: "Reads the climate sensor data from the client device.",
    serializerOptions: AGUIClientSerializerContext.Default.Options);

AIAgent agent = chatClient.AsAIAgent(
    name: "agui-client",
    tools: [changeBackground, readClientClimateSensors]);
```

Observe each tool round-trip in the stream:

```csharp
await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session))
{
    foreach (AIContent content in update.Contents)
    {
        switch (content)
        {
            case TextContent text:
                Console.Write(text.Text);
                break;

            case FunctionCallContent call:
                Console.WriteLine($"\n[Function Call] {call.Name}({JsonSerializer.Serialize(call.Arguments)})");
                break;

            case FunctionResultContent result when result.Exception is not null:
                Console.WriteLine($"\n[Function Error] {result.Exception}");
                break;

            case FunctionResultContent result:
                Console.WriteLine($"\n[Function Result] {result.Result}");
                break;

            case ErrorContent err:
                string code = err.AdditionalProperties?["Code"] as string ?? "Unknown";
                Console.WriteLine($"\n[Error {code}] {err.Message}");
                break;
        }
    }
}
```

### Source-Generated Serializer Context (Required for AOT)

AG-UI uses `JsonSerializerContext` for trim-safe payload handling. Declare every type that crosses the wire.

```csharp
using System.Text.Json.Serialization;

[JsonSerializable(typeof(SensorRequest))]
[JsonSerializable(typeof(SensorResponse))]
internal sealed partial class AGUIClientSerializerContext : JsonSerializerContext;
```

On the server:

```csharp
[JsonSerializable(typeof(ServerWeatherForecastRequest))]
[JsonSerializable(typeof(ServerWeatherForecastResponse))]
internal sealed partial class AGUIServerSerializerContext : JsonSerializerContext;

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Add(AGUIServerSerializerContext.Default));
```

Pass `xxxSerializerContext.Default.Options` to `AGUIChatClient(... jsonSerializerOptions: ...)` and to every `AIFunctionFactory.Create(... serializerOptions: ...)` for typed tool payloads.

### Multiple Endpoints on One Server

Mirror [`AGUIDojoServer/Program.cs`](https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/05-end-to-end/AGUIClientServer/AGUIDojoServer/Program.cs) — register several agents on different paths from a single server:

```csharp
builder.Services.AddAGUI();

ChatClientAgentFactory.Initialize(app.Configuration);

app.MapAGUI("/agentic_chat",            ChatClientAgentFactory.CreateAgenticChat());
app.MapAGUI("/backend_tool_rendering",  ChatClientAgentFactory.CreateBackendToolRendering());
app.MapAGUI("/human_in_the_loop",       ChatClientAgentFactory.CreateHumanInTheLoop());
app.MapAGUI("/tool_based_generative_ui",ChatClientAgentFactory.CreateToolBasedGenerativeUI());

var jsonOptions = app.Services
    .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>();

app.MapAGUI("/agentic_generative_ui",   ChatClientAgentFactory.CreateAgenticUI(jsonOptions.Value.SerializerOptions));
app.MapAGUI("/shared_state",            ChatClientAgentFactory.CreateSharedState(jsonOptions.Value.SerializerOptions));
app.MapAGUI("/predictive_state_updates",ChatClientAgentFactory.CreatePredictiveStateUpdates(jsonOptions.Value.SerializerOptions));
```

### REST Test (`.http` File)

The server accepts a plain AG-UI POST. Use this for smoke tests before plugging in the client:

```http
@host = http://localhost:5100

### Send a message to the AG-UI agent
POST {{host}}/
Content-Type: application/json

{
  "threadId": "thread_123",
  "runId": "run_456",
  "messages": [
    { "role": "user", "content": "What is the capital of France?" }
  ],
  "context": {}
}
```

The response is an SSE stream — open it in the REST Client or `curl --no-buffer` to watch events arrive.

### GitHub Copilot CLI as the backbone (`CopilotClient.AsAIAgent`)

When the model behind the AG-UI mount is the local GitHub Copilot CLI (rather than an `IChatClient`), build the agent through `SessionConfig` so the CLI receives the system prompt, the model name, the tool list, **and** a permission handler in one shot. The bare `AsAIAgent(ownsClient, name, instructions, tools)` overload throws `ArgumentException "An OnPermissionRequest handler is required"` at the first tool call.

```csharp
using GitHub.Copilot.SDK;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.GitHub.Copilot;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;

const string AgentName = "retail_orchestrator";
const string Instructions = "You are ZavaShop's retail orchestrator. ...";

string cliPath = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH")
              ?? Environment.GetEnvironmentVariable("GITHUB_COPILOT_CLI_PATH")
              ?? "copilot";
string model   = Environment.GetEnvironmentVariable("GITHUB_COPILOT_MODEL") ?? "gpt-5.5";

CopilotClient copilotClient = new(new CopilotClientOptions { CliPath = cliPath });
await copilotClient.StartAsync();                            // boot the CLI subprocess

SessionConfig sessionConfig = new()
{
    OnPermissionRequest = PermissionHandler.ApproveAll,      // REQUIRED — see Best Practices
    Model               = model,
    SystemMessage       = new SystemMessageConfig { Mode = SystemMessageMode.Append, Content = Instructions },
    Tools =
    [
        AIFunctionFactory.Create(ServerTools.SearchProducts,    name: "search_products",     description: "Search the catalog."),
        AIFunctionFactory.Create(ServerTools.GetWarehouseStock, name: "get_warehouse_stock", description: "Get stock for a SKU at a warehouse."),
    ],
};

AIAgent retailAgent = copilotClient.AsAIAgent(
    sessionConfig,
    ownsClient: true,                                        // agent disposes the CLI subprocess on shutdown
    id:   "retail-orchestrator",
    name: AgentName,
    description: Instructions);

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient().AddLogging();
builder.Services.AddAGUI();
builder.AddAIAgent(AgentName, (_, _) => retailAgent).WithInMemorySessionStore();

WebApplication app = builder.Build();
app.UseMiddleware<ApiKeyMiddleware>();                       // see API-key middleware below
app.MapAGUI(AgentName, "/retail");                           // (agentName, pattern)
app.MapPost("/scripted-reservation", async (ReservationRequest r) =>
    Results.Ok(new { order = await ServerTools.RunRetailWorkflow(r.CustomerId, r.Sku, r.Quantity, r.PreferredWarehouse) }));
await app.RunAsync("http://127.0.0.1:5100");
```

Key rules:

- Always pass an **explicit `name:`** to `AIFunctionFactory.Create(...)`. Without it the SDK uses the .NET method name, and the system prompt's `snake_case` references won't match the function name the model sees.
- Never define your own `MapAGUI(this WebApplication, ...)` extension. It will out-rank the real `IEndpointRouteBuilder` extension during overload resolution and turn the SSE stream into a one-shot JSON blob.
- Mount AG-UI at `/<path>` and a non-AG-UI bypass route at `/<other-path>` on the same `WebApplication` — the bypass is how you get reliable client-tool evidence with the Copilot CLI backbone (see warning above).

### API-key middleware (`X-API-Key`)

`AddAGUI` ships no authentication. For the workshop and most production setups, gate every request with a header check:

```csharp
public sealed class ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger)
{
    private static int _warned;

    public async Task InvokeAsync(HttpContext context)
    {
        string? expected = Environment.GetEnvironmentVariable("AG_UI_API_KEY");
        if (string.IsNullOrEmpty(expected))
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
                logger.LogWarning("AG_UI_API_KEY is not set — running in dev mode without auth.");
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-API-Key", out var got) || got != expected)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("bad api key");
            return;
        }

        await next(context);
    }
}

// Program.cs:  app.UseMiddleware<ApiKeyMiddleware>();   BEFORE  app.MapAGUI(...)
```

### Customizing Behavior with `DelegatingAIAgent`

Wrap an inner `AIAgent` to inject AG-UI-specific behavior — shared state, predictive state updates, structured snapshots, multi-turn orchestration. The Dojo server uses this pattern for every advanced scenario.

```csharp
internal sealed class SharedStateAgent(AIAgent innerAgent, JsonSerializerOptions options)
    : DelegatingAIAgent(innerAgent)
{
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 1. Read client-supplied state from ChatOptions.AdditionalProperties["ag_ui_state"]
        // 2. Inject the state into a system message for the inner agent
        // 3. Run the inner agent and capture structured output
        // 4. Emit a DataContent("application/json") snapshot to the client
        // 5. Optionally do a follow-up summarizing turn

        // ... full implementation in references/server.md
        await foreach (var u in InnerAgent.RunStreamingAsync(messages, session, options, cancellationToken))
        {
            yield return u;
        }
    }
}
```

See [references/server.md](references/server.md) for the full predictive-state-updates and shared-state patterns.

## Core Types Quick Reference

| Type | Namespace | Purpose |
|------|-----------|--------|
| `AGUIChatClient` | `Microsoft.Agents.AI.AGUI` | Client transport. Wraps an `HttpClient` and speaks AG-UI to a remote endpoint. |
| `AGUIChatClient.AsAIAgent(name, description, tools)` | `Microsoft.Agents.AI` | Promotes the chat client to an `AIAgent` with frontend tools. |
| `app.MapAGUI(name, agent)` / `app.MapAGUI(path, agent)` | `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` | Maps an `AIAgent` to an AG-UI HTTP endpoint. |
| `builder.Services.AddAGUI()` | `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` | Registers AG-UI services in DI. |
| `.AddAIAgent(name, factory).WithInMemorySessionStore()` | `Microsoft.Agents.AI.Hosting` | Registers the agent + in-memory session storage. |
| `AgentSession` / `agent.CreateSessionAsync()` | `Microsoft.Agents.AI` | Conversation handle that the AG-UI server uses as `threadId`. |
| `AgentResponseUpdate` | `Microsoft.Agents.AI` | Streaming update returned by `RunStreamingAsync`. |
| `update.AsChatResponseUpdate()` | `Microsoft.Agents.AI` | Cast to `ChatResponseUpdate` for `ConversationId`, `ResponseId`, etc. |
| `DelegatingAIAgent` | `Microsoft.Agents.AI` | Base class for wrapping an inner agent — override `RunCoreStreamingAsync`. |

## Streaming Content Types Quick Reference

| Content (in `update.Contents`) | When it appears | Notes |
|--------------------------------|----------------|-------|
| `TextContent` | Streaming text deltas from the model. | Concatenate to build the full message. |
| `FunctionCallContent` | Server-issued frontend tool call. | The client's tool executes locally; SDK auto-sends the result. |
| `FunctionResultContent` | Result of a tool call (frontend or backend). | Inspect `.Exception` for failures. |
| `DataContent` (`application/json`) | Structured state snapshot (shared state, predictive state updates). | Payload is JSON-serialized state. |
| `ErrorContent` | Server-reported error. | Read `.Message` and `.AdditionalProperties["Code"]`. |

## Complete Example

End-to-end pair that demonstrates streaming text + frontend tool + structured serializer context.

**Server**

```csharp
using System.ComponentModel;
using AGUIServer;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient().AddLogging();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Add(AGUIServerSerializerContext.Default));
builder.Services.AddAGUI();

string endpoint = builder.Configuration["AZURE_OPENAI_ENDPOINT"]!;
string deployment = builder.Configuration["AZURE_OPENAI_DEPLOYMENT_NAME"]!;
const string AgentName = "AGUIAssistant";

var agent = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
    .GetChatClient(deployment)
    .AsAIAgent(
        name: AgentName,
        tools: [
            AIFunctionFactory.Create(
                () => DateTimeOffset.UtcNow,
                name: "get_current_time",
                description: "Get the current UTC time."),
        ]);

builder.AddAIAgent(AgentName, (_, _) => agent).WithInMemorySessionStore();

WebApplication app = builder.Build();
app.MapAGUI(AgentName, "/");
await app.RunAsync();
```

**Client**

```csharp
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AGUI;
using Microsoft.Extensions.AI;

string url = Environment.GetEnvironmentVariable("AGUI_SERVER_URL") ?? "http://localhost:5100";
using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };

var chatClient = new AGUIChatClient(http, url, jsonSerializerOptions: null);

var changeBackground = AIFunctionFactory.Create(
    () => Console.WriteLine("[client] changing background"),
    name: "change_background_color",
    description: "Change the console background color.");

AIAgent agent = chatClient.AsAIAgent(
    name: "agui-client",
    description: "AG-UI Client Agent",
    tools: [changeBackground]);

AgentSession session = await agent.CreateSessionAsync();
List<ChatMessage> messages = [new(ChatRole.System, "You are helpful.")];

while (true)
{
    Console.Write("\n> ");
    string? line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line) || line is ":q") { break; }
    messages.Add(new(ChatRole.User, line));

    string? threadId = null, runId = null;
    bool isFirst = true;

    await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session))
    {
        ChatResponseUpdate chat = update.AsChatResponseUpdate();
        threadId ??= chat.ConversationId;
        runId = update.ResponseId;

        if (isFirst && threadId is not null && runId is not null)
        {
            Console.WriteLine($"[Run Started - Thread: {threadId}, Run: {runId}]");
            isFirst = false;
        }

        foreach (AIContent c in update.Contents)
        {
            switch (c)
            {
                case TextContent t:                Console.Write(t.Text); break;
                case FunctionCallContent fc:       Console.WriteLine($"\n[Call] {fc.Name}"); break;
                case FunctionResultContent fr:     Console.WriteLine($"\n[Result] {fr.Result}"); break;
                case ErrorContent err:             Console.Error.WriteLine($"\n[Error] {err.Message}"); break;
            }
        }
    }
    Console.WriteLine($"\n[Run Finished - Thread: {threadId}, Run: {runId}]");
    messages.Clear();
}
```

## Conventions

- **One agent per route.** `MapAGUI(path, agent)` binds one `AIAgent` to one route. For multiple personas / scenarios, register multiple endpoints on the same server (the Dojo sample).
- **Always register a session store.** Without `.WithInMemorySessionStore()` (or a persistent equivalent), the AG-UI server has no conversation context to associate with `threadId`.
- **Source-generate serializer contexts.** Every type that crosses the wire — tool arguments, tool results, state snapshots — must be reachable from a `[JsonSerializable]` partial context, and that context must be added to `ConfigureHttpJsonOptions` on the server and to `AGUIChatClient`/`AIFunctionFactory.Create` on the client.
- **Use `update.AsChatResponseUpdate()`** to access `ConversationId` and `ResponseId`. The raw `AgentResponseUpdate` only exposes `ResponseId` directly.
- **Frontend tools come from the client constructor**, backend tools come from the server `AsAIAgent(... tools: ...)` call. Don't duplicate the same tool on both sides.
- **`DelegatingAIAgent` for AG-UI customization.** When you need to inject state, emit structured snapshots, or rewrite messages, derive from `DelegatingAIAgent` and override `RunCoreStreamingAsync`. Don't try to hook the AG-UI middleware itself.

## Best Practices

1. **Production session storage.** `WithInMemorySessionStore()` loses everything on restart. Implement a session store backed by Redis, Cosmos DB, or your existing chat history store, and call `.WithSessionStore<TStore>()`.
2. **Auth at the HTTP layer.** AG-UI is plain HTTP/SSE — add ASP.NET Core auth middleware (`AddAuthentication` + `RequireAuthorization()`) in front of `MapAGUI`, or use a simple `ApiKeyMiddleware` like the one above. Don't rely on the protocol for security.
3. **Bound client timeouts.** AG-UI runs can stream for a long time. Set `HttpClient.Timeout` generously (e.g. 60 s+) and pass `CancellationToken` through `RunStreamingAsync(..., cancellationToken)` so users can abort.
4. **Surface frontend tool errors.** Wrap your `AIFunctionFactory.Create` delegate body in try/catch and return a structured error object — otherwise the model receives an opaque exception string and can't recover.
5. **Don't block on `DataContent` snapshots.** State snapshots can arrive interleaved with text. Render them incrementally — patch your UI on every snapshot rather than waiting for the run to end.
6. **Production credential.** Replace `DefaultAzureCredential` with a specific credential (e.g. `ManagedIdentityCredential`) in deployed environments to avoid credential-chain probing latency and accidental fallbacks.
7. **HTTP logging in dev only.** The Dojo server enables full request/response body logging (`HttpLoggingFields.RequestBody | ResponseBody`). Keep that on `Development` profile only — SSE bodies are large.
8. **GitHub Copilot CLI backbone: always go through `SessionConfig`.** `CopilotClient.AsAIAgent(sessionConfig, ownsClient, id, name, description)` is the only overload that lets you install `OnPermissionRequest = PermissionHandler.ApproveAll`. The bare `(ownsClient, name, instructions, tools)` overload throws `ArgumentException "An OnPermissionRequest handler is required"` the first time the model tries to call a tool — the CLI raises a `CUSTOM_TOOL` permission per function call regardless of `[Description]`/`AIFunctionFactory` configuration. The `Microsoft.Agents.AI.GitHub.Copilot` package brings `GitHub.Copilot.SDK` in transitively — do not add a direct `<PackageReference>` to the SDK.
9. **GitHub Copilot CLI backbone: never trust frontend tools to round-trip.** The CLI's tool list is fixed at `SessionConfig` construction time; tools advertised by `AGUIChatClient.AsAIAgent(tools: ...)` are *not* merged into the running session. Expose a parallel non-AG-UI HTTP endpoint (e.g. `app.MapPost("/scripted-reservation", ...)`) that performs the same work, and have the client invoke its local delegate after a direct call to that endpoint. This is the only deterministic way to assert that the frontend tool actually fired locally in a verify harness.
10. **Always name your tools explicitly.** `AIFunctionFactory.Create(method, name: "snake_case", description: "...")` — the system prompt almost always refers to the function by its snake_case name, and silently falling back to the .NET method name (`PascalCase`) leaves the model unable to dispatch.
11. **Verify harnesses: walk to repo root.** When the verify binary lives in `bin/Debug/net10.0/`, relative project paths like `"../../RetailServer"` resolve incorrectly. Walk parent directories from `AppContext.BaseDirectory` until a known sentinel file (e.g. `data/zava_warehouses.json`) is found, then build absolute project paths from there. Pre-build all projects and spawn subprocesses with `dotnet run --project "<abs>" --no-build` to keep stdout deterministic.
12. **Foundry backbone + AG-UI client tools → fresh `AgentSession` per turn (today).** When the AG-UI mount is backed by a Foundry agent (`AIProjectClient.AsAIAgent(new ChatClientAgentOptions { ChatOptions = { ModelId, Instructions, Tools = [...] } })`) **and** the client registers any tools through `chatClient.AsAIAgent(name, description, tools: [...])`, reusing one `AgentSession` across turns wedges with `BadRequestError: 400 ... No tool output found for function call call_…. Parameter: input` on the second turn. The AG-UI ASP.NET Core bridge executes the client tool locally and ships its result back over SSE, but does **not** persist the matching `function_call_output` into the Foundry-side conversation thread. Workaround until that gap is closed upstream: open a fresh `AgentSession session = await agent.CreateSessionAsync();` per turn on the client (smoke clients, page-transition-style UIs). This only applies to **Foundry-backed** agents — `AzureOpenAIClient.GetChatClient(...).AsAIAgent(...)` and `CopilotClient.AsAIAgent(...)` mounts don't exhibit this because they don't carry server-side thread state across requests. Server-only tools (no client tools at all) are also unaffected.
13. **Linking upstream LAB source instead of `<ProjectReference>`.** When the upstream lab keeps its types `internal` (so a class library refactor is intrusive) **and** its CLI `Main` would collide with your host's `Main`, prefer `<Compile Include="..\..\OtherLab\Program.cs" Link="OtherLabSrc.cs" />` combined with a preprocessor constant (`<DefineConstants>$(DefineConstants);HOST_NAME</DefineConstants>`) that the upstream file wraps **only its CLI block** with: `#if !HOST_NAME` / `internal static class Program { … }` / `#endif`. The upstream `.csproj` still builds standalone; the consumer assembly compiles every workflow type, executor, record, and factory directly — no NuGet packaging, no `internal`→`public` surgery. This is how LAB 5 reuses LAB 4's `WorkflowFactory.Build` / `ShippedVoucher` etc. from inside the same assembly as the AG-UI host.
14. **Running a workflow from inside an `AIFunction` delegate.** A server-side tool can drive a full `Microsoft.Agents.AI.Workflows` workflow: `var (workflow, checkpoints, _) = WorkflowFactory.Build(Path.Combine(Path.GetTempPath(), $"<lab>-{Guid.NewGuid():N}")); await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, input, checkpoints, sessionId: $"<lab>-{input}-{DateTime.UtcNow:yyyyMMddHHmmssfff}"); await foreach (WorkflowEvent evt in run.WatchStreamAsync()) { switch on WorkflowOutputEvent.Data is ShippedVoucher / ExecutorFailedEvent / WorkflowErrorEvent }`. Give each invocation its own checkpoint directory so concurrent tool calls don't collide, and clean it up best-effort in a `finally`. **Do not** try to drive the workflow's `RequestPort` (HITL gate) from inside the tool — the Foundry response is suspended while your delegate is awaiting, and there's no inbound channel to pump `RequestInfoEvent` responses back. Instead, check the HITL precondition (e.g. `total_usd >= threshold`) *inside the tool* and return an `ApprovalDialog`-shaped record so the **client** drives the gate; retry the tool with `supervisorApproval=true` once approved.
15. **Tool payloads as generative-UI / tool-based-UI sources.** The .NET AG-UI bridge transports `AIFunction` return values straight through `Microsoft.Extensions.AI` serialization; clients read them as `FunctionResultContent` in `update.Contents`. To deliver "tool-based UI" or "generative UI", design typed records with a discriminator: `internal sealed record ExceptionsResult(string Component, IReadOnlyList<ExceptionRow> Rows, int HighSeverity)` with `Component = "ExceptionsList"`. Frontend toggles render based on the `Component` value. There is **no** `IAGUIContext`, `ToolResult`, `ctx.UpdateStateAsync`, `ctx.InvokeClientToolAsync`, or `PredictStateConfig` API in this layer — those are imaginary surfaces from older draft READMEs. Shared-state and predictive-state-updates patterns belong in `DelegatingAIAgent` overrides emitting `DataContent("application/json")` snapshots (see [references/server.md](references/server.md)), not in tool return values.

## Reference Files

- [references/server.md](references/server.md): `MapAGUI`, `AddAGUI`, session storage, multiple endpoints, `DelegatingAIAgent` patterns (shared state, predictive state updates, agentic UI).
- [references/client.md](references/client.md): `AGUIChatClient`, `AsAIAgent`, frontend tools, `AgentResponseUpdate` content switching, session management, error handling.
- [references/protocol.md](references/protocol.md): AG-UI request shape, SSE event stream, `threadId` / `runId` semantics, content type reference.
- [references/advanced.md](references/advanced.md): Production hardening — auth, persistent session stores, AOT/source-generated serializer contexts, observability, structured state snapshots, multi-tenancy.
