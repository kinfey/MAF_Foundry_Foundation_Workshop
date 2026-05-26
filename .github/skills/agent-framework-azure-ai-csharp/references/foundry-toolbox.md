# Foundry Toolbox Reference (.NET)

A **Foundry Toolbox** is a server-side, versioned bundle of agent tools (function tools, hosted tools, MCP wrappers, OpenAPI tools) that you publish once and consume from any agent via MCP. Toolboxes are governed centrally and updated independently of the agent definitions that consume them.

## When to Use a Toolbox

- The same set of tools is needed by **multiple agents**.
- You want **centralized versioning / RBAC** for a tool catalog.
- You want tools to be **visible in the Foundry portal**.
- You need a **stable MCP endpoint** that other apps (and agents) can subscribe to.

For one-off per-app tools, register them inline (see [tools.md](tools.md)) instead.

---

## Toolbox Lifecycle

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ 1. Enable preview flag on the admin client                                   │
│ 2. Define tools (function / hosted / MCP / OpenAPI)                          │
│ 3. CreateToolboxVersionAsync → returns ToolboxVersion (.Name + .Version)     │
│ 4. Consume by URL:                                                           │
│       {endpoint}/toolboxes/{name}/mcp?api-version=v{version}                 │
│    via McpClient + BearerTokenHandler                                        │
│ 5. Update by creating a new version (versions are immutable)                 │
│ 6. Delete with DeleteToolboxVersionAsync / DeleteToolboxAsync                │
└──────────────────────────────────────────────────────────────────────────────┘
```

---

## Enabling the Preview Feature

The toolbox API is in V1Preview. Enable it on the `AgentAdministrationClient` via a per-call policy header.

```csharp
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Core;
using Azure.Core.Pipeline;

internal sealed class FoundryFeaturesPolicy(string headerValue) : HttpPipelineSynchronousPolicy
{
    public override void OnSendingRequest(HttpMessage message)
        => message.Request.Headers.SetValue("Foundry-Features", headerValue);
}

AIProjectClientOptions options = new();
options.AddPolicy(new FoundryFeaturesPolicy("Toolboxes=V1Preview"), HttpPipelinePosition.PerCall);

AIProjectClient projectClient = new(new Uri(endpoint), new DefaultAzureCredential(), options);
AgentAdministrationClient adminClient = projectClient.AgentAdministrationClient;
AgentToolboxes toolboxes = adminClient.GetAgentToolboxes();
```

---

## Creating a Toolbox

```csharp
using Microsoft.Extensions.AI;
using OpenAI.Responses;

[Description("Get the latest order status for a customer.")]
static string GetOrderStatus([Description("Customer email.")] string email)
    => $"Last order for {email}: shipped 2 days ago.";

[Description("Look up a SKU in inventory.")]
static string GetInventory([Description("SKU code.")] string sku)
    => $"{sku}: 42 units in stock.";

List<ProjectsAgentTool> tools =
[
    ProjectsAgentTool.AsProjectTool(AIFunctionFactory.Create(GetOrderStatus)),
    ProjectsAgentTool.AsProjectTool(AIFunctionFactory.Create(GetInventory)),
    ProjectsAgentTool.AsProjectTool(new HostedWebSearchTool()),
];

ToolboxVersion version = await toolboxes.CreateToolboxVersionAsync(
    toolboxName: "operations-toolbox",
    tools: tools,
    description: "Internal operations tools (orders, inventory, web search).");

Console.WriteLine($"Created toolbox '{version.Name}' version {version.Version}");
```

### Listing / inspecting

```csharp
await foreach (ToolboxVersion v in toolboxes.GetToolboxVersionsAsync("operations-toolbox"))
    Console.WriteLine($"  {v.Name} v{v.Version}  ({v.CreatedAt:u})");
```

### Deleting

```csharp
await toolboxes.DeleteToolboxVersionAsync("operations-toolbox", version.Version);
await toolboxes.DeleteToolboxAsync("operations-toolbox");
```

---

## Consuming a Toolbox from an Agent

A toolbox is exposed as **an MCP server** at:

```
{projectEndpoint}/toolboxes/{toolboxName}/mcp?api-version=v{version}
```

The endpoint requires an Azure AD bearer token in the `Authorization` header (scope `https://ai.azure.com/.default`) **and** the `Foundry-Features: Toolboxes=V1Preview` header.

### `BearerTokenHandler`

```csharp
using System.Net.Http.Headers;
using Azure.Core;

internal sealed class BearerTokenHandler(TokenCredential credential, string scope) : DelegatingHandler
{
    private readonly TokenRequestContext _tokenContext = new([scope]);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        AccessToken token = await credential
            .GetTokenAsync(_tokenContext, cancellationToken)
            .ConfigureAwait(false);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
```

### Connecting via `McpClient`

```csharp
using ModelContextProtocol.Client;
using Microsoft.Agents.AI;

string toolboxName = version.Name;
int toolboxVersion = version.Version;

string toolboxUrl = $"{endpoint}/toolboxes/{toolboxName}/mcp?api-version=v{toolboxVersion}";

using var httpClient = new HttpClient(new BearerTokenHandler(credential, "https://ai.azure.com/.default")
{
    InnerHandler = new HttpClientHandler(),
});

await using McpClient mcpClient = await McpClient.CreateAsync(
    new HttpClientTransport(
        new HttpClientTransportOptions
        {
            Endpoint = new Uri(toolboxUrl),
            Name = "foundry_toolbox",
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Foundry-Features"] = "Toolboxes=V1Preview",
            },
        },
        httpClient));

IList<McpClientTool> toolboxTools = await mcpClient.ListToolsAsync();
Console.WriteLine($"Toolbox exposes: {string.Join(", ", toolboxTools.Select(t => t.Name))}");

AIAgent agent = projectClient.AsAIAgent(
    deploymentName,
    instructions: "Use the operations toolbox to answer.",
    name: "OpsAgent",
    tools: [.. toolboxTools.Cast<AITool>()]);

Console.WriteLine(await agent.RunAsync("What's the latest order status for alice@contoso.com?"));
```

---

## Toolbox vs. Per-Agent Tools

| Aspect | Toolbox | Per-agent tools |
|---|---|---|
| Where defined | Server-side, versioned | Inline in the agent |
| Discovery | MCP `list_tools` against toolbox URL | Agent definition only |
| Sharing | Many agents subscribe to one toolbox | Tools live with one agent |
| Versioning | Independent of agent versions | Bumped together with the agent |
| Updates | Create new toolbox version | Edit agent config |
| Best for | Org-wide tool catalog | One-off agent helpers |

---

## Updating a Toolbox

Toolbox versions are immutable. To "update", create a new version (existing consumers continue to use older versions until they re-resolve):

```csharp
ToolboxVersion next = await toolboxes.CreateToolboxVersionAsync(
    toolboxName: "operations-toolbox",
    tools: nextTools,
    description: "Adds purchase-order tool.");

// Consumers move to the new version by updating their URL: ...?api-version=v{next.Version}
```

---

## Best Practices

1. **Use one toolbox per business domain** — orders, inventory, billing — not a single mega-toolbox.
2. **Pin consumers to a specific `api-version=v<N>`** in their MCP URL. Don't trail the latest implicitly.
3. **Wrap toolbox tools with `DelegatingAIFunction`** on the consumer side when you need per-tenant logging or budget enforcement (see [mcp.md](mcp.md)).
4. **Rotate the token credential**, not the toolbox: bearer tokens are short-lived because `BearerTokenHandler` refreshes them automatically per request.
5. **Delete unused toolbox versions** to keep the portal tidy and reduce RBAC surface area.
