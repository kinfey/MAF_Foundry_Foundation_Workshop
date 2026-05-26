# Tools Reference (.NET)

Function tools and hosted tools for Microsoft Foundry agents using the Microsoft Agent Framework .NET SDK.

## Function Tools (`AIFunctionFactory`)

Function tools run **in your process**. Mark them `static`, decorate parameters and return doc-comments with `[Description]`, and wrap with `AIFunctionFactory.Create`.

```csharp
using System.ComponentModel;
using Microsoft.Extensions.AI;

[Description("Get the weather for a given location.")]
static string GetWeather(
    [Description("City name (e.g. 'Amsterdam').")] string location)
    => $"The weather in {location} is cloudy with a high of 15°C.";

AITool weatherTool = AIFunctionFactory.Create(GetWeather);
```

### Pass tools to the agent

```csharp
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;

AIProjectClient projectClient = new(new Uri(endpoint), new DefaultAzureCredential());

AIAgent agent = projectClient.AsAIAgent(
    deploymentName,
    instructions: "You help with weather queries.",
    name: "WeatherAssistant",
    tools: [weatherTool]);

AgentSession session = await agent.CreateSessionAsync();
Console.WriteLine(await agent.RunAsync("What's the weather in Amsterdam?", session));
```

### Async function tools

`AIFunctionFactory.Create` accepts `async` methods and `Task<T>` / `ValueTask<T>` return values directly:

```csharp
[Description("Look up the latest order status for a customer.")]
static async Task<string> GetOrderStatus(
    [Description("Customer email.")] string email,
    CancellationToken cancellationToken = default)
{
    // call your backend...
    await Task.Delay(50, cancellationToken);
    return $"Last order for {email}: shipped 2 days ago.";
}

AITool tool = AIFunctionFactory.Create(GetOrderStatus);
```

### Instance methods + Dependency Injection

For methods that need state, you can pass a delegate from an instance — typically resolved from DI:

```csharp
public sealed class OrdersPlugin(IOrdersService orders)
{
    [Description("Get the latest order status for a customer.")]
    public Task<string> GetOrderStatusAsync(
        [Description("Customer email.")] string email,
        CancellationToken ct = default)
        => orders.GetLatestStatusAsync(email, ct);
}

var plugin = serviceProvider.GetRequiredService<OrdersPlugin>();
AITool ordersTool = AIFunctionFactory.Create(plugin.GetOrderStatusAsync);
```

### Function-call approval (human-in-the-loop)

Mark a tool as requiring approval, then handle `ToolApprovalRequestContent` in the response loop. The same pattern works for any approval-gated tool, including Hosted MCP — see [mcp.md](mcp.md).

---

## `HostedCodeInterpreterTool`

Runs Python on the Foundry service. Useful for math, data analysis, plotting.

```csharp
using Microsoft.Extensions.AI;
using OpenAI.Assistants;

AIAgent agent = projectClient.AsAIAgent(
    deploymentName,
    instructions: "You are a personal math tutor. Use Python to solve problems.",
    name: "CoderAgent",
    tools: [new HostedCodeInterpreterTool { Inputs = [] }]);

AgentResponse response = await agent.RunAsync("Solve sin(x) + x^2 = 42.");

// Inspect the generated code
CodeInterpreterToolCallContent? toolCall = response.Messages
    .SelectMany(m => m.Contents)
    .OfType<CodeInterpreterToolCallContent>()
    .FirstOrDefault();

if (toolCall?.Inputs.OfType<DataContent>().FirstOrDefault() is { } codeInput
    && codeInput.HasTopLevelMediaType("text"))
{
    Console.WriteLine($"Code:\n{System.Text.Encoding.UTF8.GetString(codeInput.Data.ToArray())}");
}

// Inspect the run result
CodeInterpreterToolResultContent? toolResult = response.Messages
    .SelectMany(m => m.Contents)
    .OfType<CodeInterpreterToolResultContent>()
    .FirstOrDefault();

if (toolResult?.Outputs.OfType<TextContent>().FirstOrDefault() is { } result)
{
    Console.WriteLine($"Result: {result.Text}");
}
```

### File outputs

When the model writes files (CSV, PNG, etc.) the result contains `TextAnnotationUpdate` items pointing to file IDs. Download with `aiProjectClient.GetProjectOpenAIClient().GetProjectFilesClient()`.

```csharp
foreach (AIAnnotation annotation in response.Messages
    .SelectMany(m => m.Contents).SelectMany(c => c.Annotations ?? []))
{
    if (annotation.RawRepresentation is TextAnnotationUpdate citation)
        Console.WriteLine($"file: {citation.OutputFileId} (display name {citation.TextToReplace})");
}
```

---

## `HostedFileSearchTool`

RAG over a Foundry vector store. Upload files first, then attach the vector-store ID to the tool.

```csharp
using OpenAI.Files;

var openAi = projectClient.GetProjectOpenAIClient();
var files = openAi.GetProjectFilesClient();
var vectorStores = openAi.GetProjectVectorStoresClient();

OpenAIFile uploaded = files.UploadFile(
    filePath: "data/knowledge_base.txt",
    purpose: FileUploadPurpose.Assistants);

var vsResult = await vectorStores.CreateVectorStoreAsync(new()
{
    FileIds = { uploaded.Id },
    Name = "EmployeeDirectory_VectorStore",
});

string vectorStoreId = vsResult.Value.Id;

AIAgent agent = projectClient.AsAIAgent(
    deploymentName,
    instructions: "Search uploaded files to answer questions.",
    name: "FileSearchAgent",
    tools: [new HostedFileSearchTool { Inputs = [new HostedVectorStoreContent(vectorStoreId)] }]);

AgentResponse response = await agent.RunAsync("Who is the youngest employee?");
Console.WriteLine(response);

// File citations
foreach (AIAnnotation annotation in response.Messages
    .SelectMany(m => m.Contents).SelectMany(c => c.Annotations ?? []))
{
    if (annotation.RawRepresentation is TextAnnotationUpdate citation)
        Console.WriteLine($"  file {citation.OutputFileId} → {citation.TextToReplace}");
}

// Cleanup
await vectorStores.DeleteVectorStoreAsync(vectorStoreId);
await files.DeleteFileAsync(uploaded.Id);
```

### Multiple vector stores

```csharp
new HostedFileSearchTool
{
    Inputs =
    [
        new HostedVectorStoreContent("vs-policy-docs"),
        new HostedVectorStoreContent("vs-technical-specs"),
    ],
}
```

---

## `HostedWebSearchTool`

Bing-grounded web search. Requires `BING_CONNECTION_ID` configured on the project.

```csharp
using OpenAI.Responses;

AIAgent agent = projectClient.AsAIAgent(
    deploymentName,
    instructions: "Search the web for current information.",
    name: "WebSearchAgent",
    tools: [new HostedWebSearchTool()]);

AgentResponse response = await agent.RunAsync("What's the weather today in Seattle?");
Console.WriteLine(response.Text);

// URL citations
foreach (AIAnnotation annotation in response.Messages
    .SelectMany(m => m.Contents).SelectMany(c => c.Annotations ?? []))
{
    if (annotation.RawRepresentation is UriCitationMessageAnnotation cit)
        Console.WriteLine($"  {cit.Title} — {cit.Uri}");
}
```

---

## `MemorySearchPreviewTool`

Recall facts from a Foundry memory store **without** attaching `FoundryMemoryProvider`. Useful when you want the model to decide when to query memory.

```csharp
using Azure.AI.Projects.Memory;
using Microsoft.Agents.AI.Foundry;

MemorySearchPreviewTool memorySearchTool = new(memoryStoreName, userScope: "user_alice")
{
    UpdateDelayInSecs = 0,
};

AIAgent agent = projectClient.AsAIAgent(
    deploymentName,
    instructions: "Remember what users tell you and recall it later.",
    name: "MemoryAgent",
    tools: [FoundryAITool.FromResponseTool(memorySearchTool)]);
```

See [memory.md](memory.md) for `FoundryMemoryProvider` (automatic recall on every turn) and chat-history memory.

---

## OpenAPI Tools

Expose a REST API as a tool via its OpenAPI spec.

```csharp
using Azure.AI.Projects.Agents;
using OpenAI.Responses;

OpenApiToolDefinition weatherApi = ResponseTool.CreateOpenApiTool(
    name: "WeatherAPI",
    description: "Query a public weather API.",
    spec: File.ReadAllText("specs/weather.openapi.json"),
    auth: OpenApiAuthDetails.Anonymous());     // or .ManagedIdentity(), .Connection("conn-id"), ...

ProjectsAgentVersion version = await projectClient.AgentAdministrationClient.CreateAgentVersionAsync(
    agentName: "WeatherAPIAgent",
    options: new ProjectsAgentVersionCreationOptions(new DeclarativeAgentDefinition(model: deploymentName)
    {
        Instructions = "Use the weather API to answer weather questions.",
        Tools = { ProjectsAgentTool.AsProjectTool(weatherApi) },
    }));

AIAgent agent = projectClient.AsAIAgent(version);
```

---

## Combining Tools

All tool kinds compose. Pass any mix into `tools:`.

```csharp
AIAgent agent = projectClient.AsAIAgent(
    deploymentName,
    instructions: "You are a research assistant.",
    name: "ResearchAssistant",
    tools:
    [
        AIFunctionFactory.Create(GetWeather),
        new HostedCodeInterpreterTool { Inputs = [] },
        new HostedWebSearchTool(),
        new HostedFileSearchTool { Inputs = [new HostedVectorStoreContent("vs-kb")] },
        // Plus MCP tools, OpenAPI tools, etc.
    ]);
```

## Hosted vs. Function: Decision Table

| Need | Use |
|---|---|
| Logic only your service can run | Function tool |
| Service-side Python execution | `HostedCodeInterpreterTool` |
| RAG over your docs | `HostedFileSearchTool` |
| Bing grounding | `HostedWebSearchTool` |
| Tool already exposed via MCP | MCP (hosted or local) — see [mcp.md](mcp.md) |
| Tool already exposed via OpenAPI | `ResponseTool.CreateOpenApiTool` |
| Cross-session user facts | `FoundryMemoryProvider` or `MemorySearchPreviewTool` |
