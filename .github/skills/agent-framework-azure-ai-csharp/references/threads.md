# Sessions & Multi-Turn Reference (.NET)

`AgentSession` is the .NET equivalent of the Python "thread". It maintains conversation state via response-ID chaining (Responses API path) or service-side thread IDs (persistent `FoundryAgent` path) and lets the same agent serve multiple parallel conversations.

## Creating and Using Sessions

### Basic multi-turn

```csharp
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;

AIAgent agent = new AIProjectClient(new Uri(endpoint), new DefaultAzureCredential())
    .AsAIAgent(deploymentName, instructions: "You are a helpful assistant.", name: "ChatAgent");

AgentSession session = await agent.CreateSessionAsync();

Console.WriteLine(await agent.RunAsync("My name is Alice.", session));
Console.WriteLine(await agent.RunAsync("What did I just say my name was?", session));
Console.WriteLine(await agent.RunAsync("Tell me a joke about my name.", session));
```

### Inspecting session state

```csharp
AgentSession session = await agent.CreateSessionAsync();
await agent.RunAsync("Hello!", session);

// The session ID is stable across calls — useful for logging / correlation.
Console.WriteLine($"Session id: {session.Id}");
```

---

## Persistence (Serialize / Deserialize)

`SerializeSessionAsync` returns a `JsonElement` you can store anywhere (Cosmos, blob, Redis). `DeserializeSessionAsync` reconstructs it.

```csharp
using System.Text.Json;

AgentSession session = await agent.CreateSessionAsync();
await agent.RunAsync("Start the conversation.", session);

JsonElement saved = await agent.SerializeSessionAsync(session);
await File.WriteAllTextAsync("session.json", saved.GetRawText());

// Later — possibly in another process:
JsonElement loaded = JsonDocument.Parse(await File.ReadAllTextAsync("session.json")).RootElement;
AgentSession restored = await agent.DeserializeSessionAsync(loaded);
Console.WriteLine(await agent.RunAsync("Continue our conversation.", restored));
```

---

## Sessions with Streaming

The same session works across streaming and non-streaming calls.

```csharp
AgentSession session = await agent.CreateSessionAsync();

// Turn 1 — streaming
await foreach (AgentResponseUpdate update in agent.RunStreamingAsync("Tell me about Python.", session))
    Console.Write(update);
Console.WriteLine();

// Turn 2 — non-streaming, context preserved
Console.WriteLine(await agent.RunAsync("What was that language again?", session));

// Turn 3 — streaming again
await foreach (AgentResponseUpdate update in agent.RunStreamingAsync("Give me a hello-world example.", session))
    Console.Write(update);
Console.WriteLine();
```

---

## Sessions with Tools

Tools work transparently within sessions:

```csharp
using System.ComponentModel;
using Microsoft.Extensions.AI;

[Description("Search the product catalog.")]
static string SearchCatalog([Description("Search query.")] string query)
    => $"Results for '{query}': Laptop A, Laptop B, Laptop C";

[Description("Look up details about a product.")]
static string GetProductDetails([Description("Product name.")] string productName)
    => $"{productName}: $999, In stock";

AIAgent agent = projectClient.AsAIAgent(
    deploymentName,
    instructions: "Help users find and learn about products.",
    name: "ShoppingAgent",
    tools: [AIFunctionFactory.Create(SearchCatalog), AIFunctionFactory.Create(GetProductDetails)]);

AgentSession session = await agent.CreateSessionAsync();

Console.WriteLine(await agent.RunAsync("Search for laptops.", session));
Console.WriteLine(await agent.RunAsync("Tell me about Laptop A.", session));
Console.WriteLine(await agent.RunAsync("Is it in stock?", session));
```

---

## Parallel Sessions (multi-user)

A single `AIAgent` instance is thread-safe. Each user gets their own `AgentSession`:

```csharp
static async Task HandleUserSession(AIAgent agent, string userId, IReadOnlyList<string> prompts)
{
    AgentSession session = await agent.CreateSessionAsync();
    Console.WriteLine($"--- session for {userId} ---");
    foreach (string prompt in prompts)
        Console.WriteLine(await agent.RunAsync(prompt, session));
}

await Task.WhenAll(
    HandleUserSession(agent, "alice", ["I like sci-fi.", "Recommend a movie."]),
    HandleUserSession(agent, "bob",   ["I prefer comedies.", "Recommend a movie."]));
```

---

## Persistent (Server-Side) Sessions

When you create a `FoundryAgent` via `AgentAdministrationClient.CreateAgentVersionAsync(...)`, sessions are also reflected on the server. They appear in the Foundry portal under the agent's **Conversations** tab.

```csharp
using Microsoft.Agents.AI.Foundry;

FoundryAgent foundry = projectClient.AsAIAgent(version);

AgentSession session = await foundry.CreateSessionAsync();
Console.WriteLine(await foundry.RunAsync("Tell me a joke about a pirate.", session));
Console.WriteLine(await foundry.RunAsync("Now make it about a cat.", session));

// The same session is accessible from any process via session.Id and DeserializeSessionAsync.
```

---

## Common Patterns

| Need | Pattern |
|---|---|
| Single user, multi-turn | One `AgentSession`, reused on every call |
| Multi-user web app | One `AIAgent`, one `AgentSession` per user / chat room |
| Resumable bots | Persist `await agent.SerializeSessionAsync(session)` per-user |
| Branching / experimentation | Create a new `AgentSession` per branch — they are cheap |
| Foundry portal visibility | Use `FoundryAgent` (`AsAIAgent(version)`) so sessions show up in the portal |
