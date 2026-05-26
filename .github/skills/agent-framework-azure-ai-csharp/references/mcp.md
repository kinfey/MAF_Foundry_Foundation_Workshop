# MCP Integration Reference (.NET)

Model Context Protocol (MCP) integration patterns for Microsoft Foundry agents.

## Overview

The .NET SDK supports two MCP flavors that mirror the Python skill:

| Tool | Connection managed by | Agent wiring | Use case |
|---|---|---|---|
| `ResponseTool.CreateMcpTool(...)` (hosted) | Foundry service | `DeclarativeAgentDefinition.Tools` → `CreateAgentVersionAsync` → `AsAIAgent(version)` (persistent agent only) | Public MCP servers, no client-side connection needed |
| `McpClient` + `McpClientTool` (local) | Your code | `AsAIAgent(model, instructions, name, tools: [.. mcpTools.Cast<AITool>()])` (stateless agent) | Authenticated / private servers, transparent local invocation, fast iteration |

> **Important.** `ProjectsAgentTool.AsProjectTool(ResponseTool.CreateMcpTool(...))` is **not** an `AITool` — it cannot be passed to the stateless `AsAIAgent(model, ..., tools: [...])` overload. Use hosted MCP only via the persistent `CreateAgentVersionAsync` path; otherwise use local MCP.

---

## Hosted MCP (Service-Managed)

The Foundry service connects to the MCP server. Use it when the MCP endpoint is reachable from Azure and you don't need to inject custom request handling.

### Basic usage

```csharp
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

ResponseTool mcpTool = ResponseTool.CreateMcpTool(
    serverLabel: "microsoft_learn",
    serverUri: new Uri("https://learn.microsoft.com/api/mcp"),
    toolCallApprovalPolicy: new McpToolCallApprovalPolicy(
        GlobalMcpToolCallApprovalPolicy.NeverRequireApproval));

ProjectsAgentVersion version = await projectClient.AgentAdministrationClient.CreateAgentVersionAsync(
    agentName: "MicrosoftLearnAgent",
    options: new ProjectsAgentVersionCreationOptions(new DeclarativeAgentDefinition(model: deploymentName)
    {
        Instructions = "Answer using only Microsoft Learn content.",
        Tools = { mcpTool },
    }));

AIAgent agent = projectClient.AsAIAgent(version);
AgentSession session = await agent.CreateSessionAsync();
Console.WriteLine(await agent.RunAsync("Summarize MCP tool calling in Foundry.", session));
```

### Restricting which tools the server may expose

```csharp
ResponseTool mcpTool = ResponseTool.CreateMcpTool(
    serverLabel: "microsoft_learn",
    serverUri: new Uri("https://learn.microsoft.com/api/mcp"),
    allowedTools: new McpToolFilter { ToolNames = { "microsoft_docs_search" } },
    toolCallApprovalPolicy: new McpToolCallApprovalPolicy(
        GlobalMcpToolCallApprovalPolicy.NeverRequireApproval));
```

### Approval policies

```csharp
// Always require approval
new McpToolCallApprovalPolicy(GlobalMcpToolCallApprovalPolicy.AlwaysRequireApproval)

// Never require approval (trusted toolset)
new McpToolCallApprovalPolicy(GlobalMcpToolCallApprovalPolicy.NeverRequireApproval)

// Per-tool policy
new McpToolCallApprovalPolicy(new PerToolMcpToolCallApprovalPolicy
{
    AlwaysRequireApproval = { ToolNames = { "delete_resource", "modify_config" } },
    NeverRequireApproval = { ToolNames = { "search", "read" } },
})
```

### Handling approval requests

When the policy is `AlwaysRequireApproval`, the response will contain `ToolApprovalRequestContent` items. Reply with `ChatRole.User` messages built from `approvalRequest.CreateResponse(approved: ...)`:

```csharp
AgentResponse response = await agent.RunAsync(prompt, session);
List<ToolApprovalRequestContent> approvals = response.Messages
    .SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().ToList();

while (approvals.Count > 0)
{
    List<ChatMessage> userResponses = approvals.ConvertAll(req =>
    {
        var call = (McpServerToolCallContent)req.ToolCall!;
        Console.WriteLine($"Approve {call.ServerName}.{call.Name}? (Y/N)");
        bool approved = Console.ReadLine()?.Equals("Y", StringComparison.OrdinalIgnoreCase) ?? false;
        return new ChatMessage(ChatRole.User, [req.CreateResponse(approved)]);
    });

    response = await agent.RunAsync(userResponses, session);
    approvals = response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().ToList();
}

Console.WriteLine(response);
```

### Custom headers (e.g. private server auth)

```csharp
ResponseTool mcpTool = ResponseTool.CreateMcpTool(
    serverLabel: "private_mcp",
    serverUri: new Uri("https://my-mcp.example.com/mcp"),
    headers: new Dictionary<string, string>
    {
        ["Authorization"] = "Bearer <static-token>",
        ["X-Tenant-Id"] = "acme",
    },
    toolCallApprovalPolicy: new McpToolCallApprovalPolicy(GlobalMcpToolCallApprovalPolicy.NeverRequireApproval));
```

For tokens that need to refresh per request, prefer the local-MCP path with a `DelegatingHandler` (next section).

---

## Local MCP (Client-Managed)

Use `McpClient` from `ModelContextProtocol.Client`. Your code holds the network connection; the agent calls tools through it.

### Basic local MCP

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

await using McpClient mcpClient = await McpClient.CreateAsync(new HttpClientTransport(new()
{
    Endpoint = new Uri("https://learn.microsoft.com/api/mcp"),
    Name = "Microsoft Learn MCP",
}));

IList<McpClientTool> mcpTools = await mcpClient.ListToolsAsync();
Console.WriteLine($"MCP tools: {string.Join(", ", mcpTools.Select(t => t.Name))}");

AIAgent agent = projectClient.AsAIAgent(
    deploymentName,
    instructions: "Answer Microsoft documentation questions via MCP.",
    name: "DocsAgent",
    tools: [.. mcpTools.Cast<AITool>()]);

Console.WriteLine(await agent.RunAsync("How do I create an Azure storage account with the CLI?"));
```

> `McpClient` owns a network connection — **always `await using`** it.

### Wrapping MCP tools for logging / metering / approval

Use `DelegatingAIFunction` to intercept invocations:

```csharp
internal sealed class LoggingMcpTool(AIFunction inner) : DelegatingAIFunction(inner)
{
    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"  >> [LOCAL MCP] Invoking '{this.Name}' locally...");
        return base.InvokeCoreAsync(arguments, cancellationToken);
    }
}

List<AITool> wrappedTools = mcpTools.Select(t => (AITool)new LoggingMcpTool(t)).ToList();

AIAgent agent = projectClient.AsAIAgent(
    deploymentName,
    instructions: "Answer documentation questions.",
    name: "DocsAgent",
    tools: wrappedTools);
```

### Local MCP with bearer-token auth

Inject a fresh Azure AD bearer token on every MCP request via a `DelegatingHandler`. This is the pattern used for Foundry Toolboxes (see [foundry-toolbox.md](foundry-toolbox.md)).

```csharp
internal sealed class BearerTokenHandler(TokenCredential credential, string scope) : DelegatingHandler
{
    private readonly TokenRequestContext _tokenContext = new([scope]);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        AccessToken token = await credential.GetTokenAsync(_tokenContext, cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

using var httpClient = new HttpClient(new BearerTokenHandler(credential, "https://ai.azure.com/.default")
{
    InnerHandler = new HttpClientHandler(),
});

await using McpClient mcpClient = await McpClient.CreateAsync(new HttpClientTransport(new()
{
    Endpoint = new Uri("https://my-protected-mcp.example.com/mcp"),
    Name = "Protected MCP",
    TransportMode = HttpTransportMode.StreamableHttp,
}, httpClient));
```

### Multiple MCP servers

```csharp
await using McpClient docsMcp = await McpClient.CreateAsync(new HttpClientTransport(new()
{
    Endpoint = new Uri("https://learn.microsoft.com/api/mcp"),
    Name = "Docs MCP",
}));

await using McpClient githubMcp = await McpClient.CreateAsync(new HttpClientTransport(new()
{
    Endpoint = new Uri("https://api.githubcopilot.com/mcp"),
    Name = "GitHub MCP",
}, githubAuthenticatedHttpClient));

List<AITool> tools =
[
    .. (await docsMcp.ListToolsAsync()).Cast<AITool>(),
    .. (await githubMcp.ListToolsAsync()).Cast<AITool>(),
];

AIAgent agent = projectClient.AsAIAgent(
    deploymentName,
    instructions: "Search docs and interact with GitHub.",
    name: "MultiMcpAgent",
    tools: tools);
```

---

## Hosted vs. Local MCP

| Aspect | Hosted (`ResponseTool.CreateMcpTool`) | Local (`McpClient` + `McpClientTool`) |
|---|---|---|
| Connection manager | Foundry service | Your code |
| `await using` required | No | **Yes** |
| Visible in Foundry portal | Yes (when attached to a `FoundryAgent` version) | No |
| Best for | Public MCP servers, persistent agent versions | Auth-headers per request, debugging, wrapping with `DelegatingAIFunction` |
| Token refresh | Static `headers:` only | Per-request via `DelegatingHandler` |
| Logging / interception | No | Yes (`DelegatingAIFunction`) |

### When to choose which

- **Hosted MCP** when the MCP endpoint is public, trusted, and you want the Foundry agent record to carry the tool config.
- **Local MCP** when you need per-request authentication (Foundry Toolbox, GitHub PATs, OBO), local logging/metering, or you do not want to register a persistent agent version.

---

## Long-Running MCP Tasks

For MCP tools that return progress (SEP-2663), the agent wrapper polls transparently across both `RunAsync` and `RunStreamingAsync`. No extra code is needed in your agent; the SDK drives the task to completion. See the `Agent_MCP_LongRunningTask_Client` sample for the wire-level details.
