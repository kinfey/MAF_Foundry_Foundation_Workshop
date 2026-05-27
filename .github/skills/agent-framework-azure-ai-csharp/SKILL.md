---
name: agent-framework-azure-ai-csharp
description: Build Microsoft Foundry agents with the Microsoft Agent Framework .NET SDK (Microsoft.Agents.AI + Microsoft.Agents.AI.Foundry). Use when creating agents via AIProjectClient.AsAIAgent or FoundryAgent, calling function tools and hosted tools (code interpreter, file search, web search, memory search), integrating MCP servers (local McpClient and hosted ResponseTool.CreateMcpTool), consuming Foundry Toolboxes via MCP, managing AgentSession multi-turn conversations, producing structured output with ChatResponseFormat.ForJsonSchema, attaching FoundryMemoryProvider, or shipping reusable AgentInlineSkill / AgentClassSkill bundles via AgentSkillsProvider.
license: MIT
metadata:
  author: Microsoft
  version: "1.0.0"
  package: Microsoft.Agents.AI.Foundry
---

# Agent Framework Azure AI Foundry Agents (.NET)

Build agents on Microsoft Foundry using the Microsoft Agent Framework .NET SDK. The same `AIAgent` abstraction wraps both the Foundry Responses API (`AIProjectClient.AsAIAgent(...)`) and the persistent Foundry Agent resource (`FoundryAgent` + `AgentAdministrationClient`).

## Architecture

```
User Query → AIProjectClient.AsAIAgent(...) → Microsoft Foundry (Responses)
                  ↓                       └── FoundryAgent + AgentAdministrationClient (persistent)
            AIAgent.RunAsync() / .RunStreamingAsync()
                  ↓
   Tools: AIFunctionFactory.Create | Hosted (CodeInterpreter / FileSearch / WebSearch / MemorySearch)
        | McpClientTool (local MCP) | ResponseTool.CreateMcpTool (hosted MCP)
                  ↓
            AgentSession (multi-turn conversation, response-id chaining)
                  ↓
  Optional: FoundryMemoryProvider | AgentSkillsProvider (file/code/class skills)
```

## Installation

```bash
# Core agent runtime (always required)
dotnet add package Microsoft.Agents.AI --prerelease
dotnet add package Microsoft.Extensions.AI --prerelease

# Microsoft Foundry integration
dotnet add package Microsoft.Agents.AI.Foundry --prerelease
dotnet add package Azure.AI.Projects --prerelease

# Auth
dotnet add package Azure.Identity

# Optional: MCP client (for local-resolved MCP tools)
dotnet add package ModelContextProtocol --prerelease
```

## Prerequisites

- **.NET 10 SDK** or later
- A Microsoft Foundry project with a chat model deployment (e.g. `gpt-5.4-mini`)
- Azure CLI signed in (`az login`) with `Azure AI Developer` on the Foundry project (or another credential the chain resolves)
- For memory samples: an embedding deployment (e.g. `text-embedding-3-small` or `text-embedding-ada-002`)
- For Bing-grounded web search: a Bing connection configured on the project

## Environment Variables

```bash
export AZURE_AI_PROJECT_ENDPOINT="https://<account>.services.ai.azure.com/api/projects/<project>"
export AZURE_AI_MODEL_DEPLOYMENT_NAME="gpt-5.4-mini"

# Optional
export AZURE_AI_EMBEDDING_DEPLOYMENT_NAME="text-embedding-ada-002"   # memory / file search
export AZURE_AI_MEMORY_STORE_ID="memory-store-sample"                # FoundryMemoryProvider
export BING_CONNECTION_ID="<bing-connection-id>"                     # HostedWebSearchTool with grounding
```

## Authentication & Lifecycle

> **🔑 Two rules apply to every sample below:**
>
> 1. **Prefer `DefaultAzureCredential` / `AzureCliCredential`.** They work locally (Azure CLI / VS) and in Azure (managed identity, workload identity) with no code change. Avoid connection strings and account keys — they bypass Entra audit and rotation. In production, pin the chain with a specific credential such as `ManagedIdentityCredential` to avoid probing latency.
> 2. **Dispose disposables.** Wrap `McpClient` with `await using`, and dispose any `HttpClient`/`HttpClientHandler` you own. `AIProjectClient` and `AIAgent` are designed to be long-lived; don't dispose them per request.

```csharp
using Azure.Core;
using Azure.Identity;

// Development
TokenCredential credential = new AzureCliCredential();

// Production
// TokenCredential credential = new DefaultAzureCredential();
// or: TokenCredential credential = new ManagedIdentityCredential();
```

## Core Workflow

### Basic Agent (Responses API path)

The shortest path is `AIProjectClient.AsAIAgent(...)`. This returns an `AIAgent` backed by the Foundry Responses API — no persistent agent resource is created on the service.

```csharp
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;

string endpoint = Environment.GetEnvironmentVariable("AZURE_AI_PROJECT_ENDPOINT")!;
string deploymentName = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME") ?? "gpt-5.4-mini";

AIAgent agent = new AIProjectClient(new Uri(endpoint), new DefaultAzureCredential())
    .AsAIAgent(
        model: deploymentName,
        instructions: "You are good at telling jokes.",
        name: "JokerAgent");

Console.WriteLine(await agent.RunAsync("Tell me a joke about a pirate."));
```

### Agent with Function Tools

Decorate static methods with `[Description]` and wrap them with `AIFunctionFactory.Create`. The agent calls them client-side.

```csharp
using System.ComponentModel;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

[Description("Get the weather for a given location.")]
static string GetWeather([Description("City to get the weather for.")] string location)
    => $"The weather in {location} is cloudy with a high of 15°C.";

[Description("Get the current UTC time as ISO-8601.")]
static string GetCurrentUtc() => DateTime.UtcNow.ToString("O");

AITool weatherTool = AIFunctionFactory.Create(GetWeather);
AITool clockTool = AIFunctionFactory.Create(GetCurrentUtc);

AIProjectClient projectClient = new(new Uri(endpoint), new DefaultAzureCredential());

AIAgent agent = projectClient.AsAIAgent(
    deploymentName,
    instructions: "You help with weather and time queries.",
    name: "WeatherAssistant",
    tools: [weatherTool, clockTool]);

AgentSession session = await agent.CreateSessionAsync();
Console.WriteLine(await agent.RunAsync("What is the weather like in Amsterdam?", session));
```

### Agent with Hosted Tools

Hosted tools execute **on the Foundry service**, not in your process. Just pass instances into `tools:`.

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using OpenAI.Responses;

AIAgent agent = projectClient.AsAIAgent(
    deploymentName,
    instructions: "You can run code, search files, and search the web.",
    name: "MultiToolAgent",
    tools:
    [
        new HostedCodeInterpreterTool { Inputs = [] },
        new HostedWebSearchTool(),
    ]);

Console.WriteLine(await agent.RunAsync("Calculate the factorial of 20 in Python."));
```

### Streaming Responses

`RunStreamingAsync` returns `IAsyncEnumerable<AgentResponseUpdate>`. Each update can be printed directly.

```csharp
await foreach (AgentResponseUpdate update in agent.RunStreamingAsync("Tell me a short story"))
{
    Console.Write(update);
}
Console.WriteLine();
```

### Multi-Turn Conversation with AgentSession

`AgentSession` is the .NET equivalent of a Python thread. It chains response IDs so the agent remembers prior turns. The same session works across streaming and non-streaming calls.

```csharp
AgentSession session = await agent.CreateSessionAsync();

Console.WriteLine(await agent.RunAsync("My name is Alice.", session));
Console.WriteLine(await agent.RunAsync("What is my name?", session));

// Persist / restore
JsonElement saved = await agent.SerializeSessionAsync(session);
AgentSession restored = await agent.DeserializeSessionAsync(saved);
Console.WriteLine(await agent.RunAsync("Do you still remember my name?", restored));
```

### Structured Output

Set `ChatResponseFormat.ForJsonSchema<T>()` once and call `RunAsync<T>(...)` to receive a deserialized result.

```csharp
using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

public sealed class PersonInfo
{
    [JsonPropertyName("name")]    public string? Name { get; set; }
    [JsonPropertyName("age")]     public int? Age { get; set; }
    [JsonPropertyName("occupation")] public string? Occupation { get; set; }
}

AIAgent agent = projectClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = "StructuredOutputAssistant",
    ChatOptions = new()
    {
        ModelId = deploymentName,
        Instructions = "Extract structured information about people.",
        ResponseFormat = ChatResponseFormat.ForJsonSchema<PersonInfo>(),
    },
});

AgentResponse<PersonInfo> response = await agent.RunAsync<PersonInfo>(
    "Please provide information about John Smith, a 35-year-old software engineer.");

Console.WriteLine($"{response.Result.Name} ({response.Result.Age}) — {response.Result.Occupation}");
```

## Persistent Foundry Agents (Server-Side Resource)

When you want the agent to be **visible in the Foundry portal** and reusable across apps, create a versioned agent record. The same `AIAgent` interface is returned.

```csharp
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Microsoft.Agents.AI.Foundry;

AIProjectClient projectClient = new(new Uri(endpoint), new DefaultAzureCredential());

ProjectsAgentVersion created = await projectClient.AgentAdministrationClient.CreateAgentVersionAsync(
    agentName: "JokerAgent",
    options: new ProjectsAgentVersionCreationOptions(
        new DeclarativeAgentDefinition(model: deploymentName) { Instructions = "You are good at telling jokes." }));

FoundryAgent foundryAgent = projectClient.AsAIAgent(created);
Console.WriteLine(await foundryAgent.RunAsync("Tell me a joke about a pirate."));

// Get the latest version by agent name
ProjectsAgentRecord record = await projectClient.AgentAdministrationClient.GetAgentAsync("JokerAgent");
FoundryAgent latest = projectClient.AsAIAgent(record);

// Delete all versions of the agent
projectClient.AgentAdministrationClient.DeleteAgent(foundryAgent.Name);
```

| Path | When to use |
|---|---|
| `AIProjectClient.AsAIAgent(model: ..., instructions: ...)` | Stateless agents, fast iteration, code-only definition |
| `AgentAdministrationClient.CreateAgentVersionAsync(...)` + `AsAIAgent(version)` | Persistent agent record, versioning, sharing with the portal/teammates |
| `new FoundryAgent(endpoint, credential, model, instructions, name)` | Quickest constructor when you already know endpoint/model |

## Hosted Tools Quick Reference

| Tool | Namespace | Purpose |
|---|---|---|
| `HostedCodeInterpreterTool` | `Microsoft.Extensions.AI` | Run Python on the service |
| `HostedFileSearchTool` | `Microsoft.Extensions.AI` | RAG over Foundry vector stores |
| `HostedWebSearchTool` | `Microsoft.Extensions.AI` | Bing-grounded web search |
| `MemorySearchPreviewTool` | `Azure.AI.Projects.Memory` | Recall memories from a Foundry memory store |
| `ResponseTool.CreateMcpTool(...)` | `OpenAI.Responses` | Service-managed (hosted) MCP tool |
| `McpClientTool` (cast to `AITool`) | `ModelContextProtocol.Client` | Client-managed MCP tool, invoked locally |
| `OpenAPITool` / `OpenApiToolDefinition` | `Azure.AI.Projects.Agents` | OpenAPI-defined REST tool |

See [references/tools.md](references/tools.md) for detailed patterns.

## MCP Integration

Two flavors, mirroring the Python skill. **Read the next paragraph before choosing — the two paths are NOT interchangeable.**

> **Wiring rule.** Hosted MCP (`ResponseTool.CreateMcpTool`) returns an `OpenAI.Responses.ResponseTool` wrapped as a `ProjectsAgentTool`, which is **only accepted by `AgentAdministrationClient.CreateAgentVersionAsync(...)` via `DeclarativeAgentDefinition.Tools`** (the persistent agent path). It does **not** implement `Microsoft.Extensions.AI.AITool`, so you cannot pass it into the stateless `AIProjectClient.AsAIAgent(model, instructions, name, tools: [...])` overload. For the stateless overload, use **local MCP** (`McpClient` + `mcpTools.Cast<AITool>()`).

```csharp
// 1) Hosted MCP — service-managed; requires the persistent CreateAgentVersionAsync path.
ProjectsAgentTool hostedMcp = ProjectsAgentTool.AsProjectTool(ResponseTool.CreateMcpTool(
    serverLabel: "microsoft_learn",
    serverUri: new Uri("https://learn.microsoft.com/api/mcp"),
    toolCallApprovalPolicy: new McpToolCallApprovalPolicy(GlobalMcpToolCallApprovalPolicy.NeverRequireApproval)));

ProjectsAgentVersion version = await projectClient.AgentAdministrationClient.CreateAgentVersionAsync(
    agentName: "DocsAgent",
    options: new ProjectsAgentVersionCreationOptions(new DeclarativeAgentDefinition(model: deploymentName)
    {
        Instructions = "Answer questions using Microsoft documentation.",
        Tools = { hostedMcp },
    }));
AIAgent hostedAgent = projectClient.AsAIAgent(version);

// 2) Local MCP — your code holds the connection; works with the stateless AsAIAgent overload.
await using McpClient mcpClient = await McpClient.CreateAsync(new HttpClientTransport(new()
{
    Endpoint = new Uri("https://learn.microsoft.com/api/mcp"),
    Name = "Microsoft Learn MCP",
}));
IList<McpClientTool> mcpTools = await mcpClient.ListToolsAsync();

AIAgent localAgent = projectClient.AsAIAgent(
    deploymentName,
    instructions: "Answer questions using Microsoft documentation.",
    name: "DocsAgent",
    tools: [.. mcpTools.Cast<AITool>()]);
```

See [references/mcp.md](references/mcp.md) for approval flows, auth headers, hosted vs. local trade-offs, and Foundry Toolbox consumption.

## Foundry Memory (Cross-Session Recall)

Attach a `FoundryMemoryProvider` to persist user-scoped memories that survive across sessions and processes.

The same Foundry Memory RBAC rule applies to .NET as to Python: a 401 from the memory backend hitting the embedding deployment almost always means the **calling identity** (your signed-in user when using `AzureCliCredential` locally, or the app's managed identity in production) lacks the embedding data action. The `Foundry User` role covers chat completions but NOT embeddings. Grant that caller `Cognitive Services OpenAI User` on both the AI account and project scopes, plus `Cognitive Services User` on the AI account scope, before changing agent code. The legacy `properties.agentIdentity.agentIdentityId` path is `null` on newer Foundry projects and is NOT the fix — only grant that ServiceIdentity SP the same three roles when the ARM lookup returns a non-empty value.

```csharp
using Microsoft.Agents.AI.Foundry;

FoundryMemoryProvider memoryProvider = new(
    projectClient,
    memoryStoreName: "memory-store-sample",
    stateInitializer: _ => new(new FoundryMemoryProviderScope("sample-user-123")));

ChatClientAgent agent = projectClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = "TravelAssistantWithFoundryMemory",
    ChatOptions = new()
    {
        ModelId = deploymentName,
        Instructions = "Use known memories about the user; do not invent details.",
    },
    AIContextProviders = [memoryProvider],
});

await memoryProvider.EnsureMemoryStoreCreatedAsync(deploymentName, embeddingModelName);

AgentSession session = await agent.CreateSessionAsync();
await agent.RunAsync("Hi, I'm Taylor and I love hiking.", session);
await memoryProvider.WhenUpdatesCompletedAsync();   // memory extraction is async on the service
Console.WriteLine(await agent.RunAsync("Recap what you know about me.", session));
```

See [references/memory.md](references/memory.md) for `MemorySearchPreviewTool` (RAPI path), `ChatHistoryMemoryProvider` (vector-store transcripts), and scoping semantics.

## LAB 3 C# Evaluation / Red-Team Bridge

FoundryEvals and Azure AI Evaluation RedTeam are Python SDK surfaces today. For a C# LAB 3 implementation, do **not** try to recreate those evaluators in .NET. Build the C# Aria agent with `FoundryMemoryProvider`, then expose it over HTTP so the Python harness can score the C# behavior.

The verified ZavaShop pattern is:

- ASP.NET Core project using `Microsoft.NET.Sdk.Web`.
- Register the C# `AIAgent` with `builder.AddAIAgent("Aria", (_, _) => agent).WithInMemorySessionStore()` and `app.MapAGUI("Aria", "/")` for AG-UI compatibility.
- Add a deterministic JSON endpoint for the Python harness:

```csharp
app.MapPost("/chat", async (ChatRequest request) =>
{
    AgentSession session = await agent.CreateSessionAsync();
    try
    {
        AgentResponse response = await agent.RunAsync(request.Message, session);
        return Results.Ok(new ChatResponse(response.ToString()));
    }
    catch (Exception)
    {
        return Results.Ok(new ChatResponse(
            "I can't help with that request. I can only assist with safe ZavaShop customer-service tasks."));
    }
});

internal sealed record ChatRequest(string Message);
internal sealed record ChatResponse(string Text);
```

The exception-to-refusal behavior is important: RedTeam prompts can trigger Azure OpenAI content filtering, and the scoring harness must see a safe refusal instead of an HTTP 500. Run the bridge with `dotnet run --project workshop/LAB03-customer-memory-eval/AriaAgent -- --serve`, then run Python scoring with `AGUI_SERVER_URL=http://127.0.0.1:5100 conda run -n agentdev --no-capture-output python workshop/LAB03-customer-memory-eval/evaluate_aria.py` and the same prefix for `redteam_aria.py`.

## Agent Skills (Code / Class / File)

The .NET SDK ships an Agent Skills runtime that lets an agent advertise modular capability bundles and load them on demand via progressive disclosure. Attach a `AgentSkillsProvider` via `AIContextProviders`.

```csharp
using Microsoft.Agents.AI;

var unitConverter = new AgentInlineSkill(
        name: "unit-converter",
        description: "Convert between miles/km/lb/kg using a multiplication factor.",
        instructions: "1. Read the conversion-table resource. 2. Call the convert script.")
    .AddResource("conversion-table", "| miles | kilometers | 1.60934 |")
    .AddScript("convert", (double value, double factor)
        => JsonSerializer.Serialize(new { value, factor, result = Math.Round(value * factor, 4) }));

AIAgent agent = projectClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = "UnitConverterAgent",
    ChatOptions = new() { ModelId = deploymentName, Instructions = "You can convert units." },
    AIContextProviders = [new AgentSkillsProvider(unitConverter)],
});

Console.WriteLine(await agent.RunAsync("How many km is 26.2 miles?"));
```

Three flavors: `AgentInlineSkill` (code-defined), `AgentClassSkill<TSelf>` (class-based with `[AgentSkillResource]` / `[AgentSkillScript]` attributes), and file-based `SKILL.md` directories with a script runner. See [references/skills.md](references/skills.md).

## Foundry Toolbox via MCP

A Foundry Toolbox is a server-side versioned tool bundle. Point an `McpClient` at its MCP endpoint to consume it from any agent:

```csharp
string toolboxEndpoint = $"{projectEndpoint}/toolboxes/{toolboxName}/mcp?api-version=v{version}";

using var httpClient = new HttpClient(new BearerTokenHandler(credential, "https://ai.azure.com/.default"));

await using McpClient mcpClient = await McpClient.CreateAsync(
    new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = new Uri(toolboxEndpoint),
        Name = "foundry_toolbox",
        TransportMode = HttpTransportMode.StreamableHttp,
        AdditionalHeaders = new Dictionary<string, string>
        {
            ["Foundry-Features"] = "Toolboxes=V1Preview",
        },
    },
    httpClient));

IList<McpClientTool> mcpTools = await mcpClient.ListToolsAsync();

AIAgent agent = projectClient.AsAIAgent(
    deploymentName,
    instructions: "Use the toolbox tools to answer.",
    name: "ToolboxMcpAgent",
    tools: [.. mcpTools.Cast<AITool>()]);
```

See [references/foundry-toolbox.md](references/foundry-toolbox.md) for the full `BearerTokenHandler`, toolbox CRUD via `AgentAdministrationClient.GetAgentToolboxes()`, and when to choose a toolbox over per-app MCP.

## Conventions

- **One `AIProjectClient` per process** — it's HTTP-pipeline-backed and reused.
- **`AIAgent`/`AIProjectClient` are not `IDisposable` per-request** — keep them alive.
- **`await using McpClient`** — always; it owns a network connection.
- **Use `AgentSession`** for any multi-turn flow; pass it on every `RunAsync` / `RunStreamingAsync` call in the conversation.
- **Mark function-tool methods `static`** and decorate with `[Description]` so the schema is generated automatically.
- **For tools that take complex args**, mark parameters with `[Description]`; primitives infer from name.

## Best Practices

1. **Reuse `AIAgent`** across calls — it's thread-safe and the underlying HTTP pipeline is pooled.
2. **Always use `AzureCliCredential` (local) or pinned production credential** — avoid raw API keys.
3. **Prefer `AIProjectClient.AsAIAgent(...)`** for app-owned agents; promote to `FoundryAgent` versioning only when the agent must be portable across apps or visible in the portal.
4. **For Hosted MCP**, default to `GlobalMcpToolCallApprovalPolicy.NeverRequireApproval` only when the toolset is trusted; otherwise use `AlwaysRequireApproval` and handle `ToolApprovalRequestContent` in the response loop.
5. **For memory**, set a stable user-scoped `FoundryMemoryProviderScope`; otherwise memories are siloed per session.

## Reference Files

- [references/tools.md](references/tools.md) — function tools, `HostedCodeInterpreterTool`, `HostedFileSearchTool`, `HostedWebSearchTool`, OpenAPI tools, annotations & citations.
- [references/mcp.md](references/mcp.md) — local `McpClient`, `ResponseTool.CreateMcpTool` hosted MCP, approval policies, `DelegatingAIFunction` wrapping.
- [references/threads.md](references/threads.md) — `AgentSession` multi-turn, serialize/deserialize, parallel sessions, streaming + sessions.
- [references/memory.md](references/memory.md) — `FoundryMemoryProvider`, `MemorySearchPreviewTool`, `ChatHistoryMemoryProvider` over a vector store.
- [references/skills.md](references/skills.md) — `AgentInlineSkill`, `AgentClassSkill<TSelf>`, file-based skills, `AgentSkillsProviderBuilder`, DI.
- [references/foundry-toolbox.md](references/foundry-toolbox.md) — toolbox creation via `AgentToolboxes`, `BearerTokenHandler`, MCP consumption.
- [references/advanced.md](references/advanced.md) — structured output, OpenAPI tools, file generation, dependency injection, middleware, observability.
