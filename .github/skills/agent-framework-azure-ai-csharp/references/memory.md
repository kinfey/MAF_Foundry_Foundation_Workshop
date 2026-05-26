# Memory Reference (.NET)

Cross-session and intra-session memory for Microsoft Foundry agents.

## Memory Options Compared

| Approach | Storage | Scope | When to use |
|---|---|---|---|
| `FoundryMemoryProvider` | Foundry memory store (server-side) | User-scoped, cross-session | Long-lived user facts, multi-device continuity |
| `MemorySearchPreviewTool` | Foundry memory store (server-side) | User-scoped, cross-session | Same store, but recall is model-decided (RAPI path) |
| `ChatHistoryMemoryProvider` | Any `IVectorStore` (e.g. in-memory) | App-controlled | Lightweight transcript memory without Foundry memory store |

`FoundryMemoryProvider` automatically queries the memory store before every turn and writes extracted facts asynchronously. `MemorySearchPreviewTool` is a hosted tool the model can decide to call.

---

## `FoundryMemoryProvider` (Recommended)

### Setup

```csharp
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry;
using Microsoft.Extensions.AI;

string memoryStoreName = "memory-store-sample";
string deploymentName  = "gpt-5.4-mini";
string embedModelName  = "text-embedding-ada-002";
string userId          = "sample-user-123";

AIProjectClient projectClient = new(new Uri(endpoint), new DefaultAzureCredential());

FoundryMemoryProvider memoryProvider = new(
    projectClient,
    memoryStoreName,
    stateInitializer: _ => new FoundryMemoryProviderState(new FoundryMemoryProviderScope(userId)));

// Create the memory store on the service the first time
await memoryProvider.EnsureMemoryStoreCreatedAsync(deploymentName, embedModelName);

ChatClientAgent agent = projectClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = "TravelAssistantWithFoundryMemory",
    ChatOptions = new()
    {
        ModelId = deploymentName,
        Instructions = "You are a travel agent. Use any known facts about the user.",
    },
    AIContextProviders = [memoryProvider],
});
```

### Writing memories

Memories are extracted server-side from conversation turns. Drive the chat normally, then **wait** for extraction to settle if you want to read them back immediately:

```csharp
AgentSession session = await agent.CreateSessionAsync();
await agent.RunAsync("Hi! I'm Taylor and I prefer aisle seats.", session);

// Memory extraction runs asynchronously on the service
await memoryProvider.WhenUpdatesCompletedAsync();
```

### Recalling memories on a new session

```csharp
AgentSession freshSession = await agent.CreateSessionAsync();
Console.WriteLine(await agent.RunAsync("Recap what you know about me.", freshSession));
```

### Cleanup

```csharp
await memoryProvider.EnsureStoredMemoriesDeletedAsync();   // delete just this user's memories
await memoryProvider.EnsureMemoryStoreDeletedAsync();      // delete the store entirely
```

### Scoping

`FoundryMemoryProviderScope` is the multi-tenancy key. Use a stable per-user identifier (e.g. an Entra object ID) so memories survive across sessions for the same person.

```csharp
new FoundryMemoryProviderScope(userId: entraUserObjectId)
```

---

## `MemorySearchPreviewTool` (Model-Driven Recall)

Attach the memory store **as a hosted tool**. The model decides when to query it. Use this when you want the model to be explicit about memory usage.

```csharp
using Azure.AI.Projects.Memory;
using Microsoft.Agents.AI.Foundry;

string memoryStoreName = "demo_memory_store";
string userScope = "user_alice";

// Provision the memory store directly via the typed client
await projectClient.MemoryStores.CreateMemoryStoreAsync(
    memoryStoreName,
    new MemoryStoreCreationOptions(deploymentName, embedModelName));

MemorySearchPreviewTool memorySearchTool = new(memoryStoreName, userScope) { UpdateDelayInSecs = 0 };

AIAgent agent = projectClient.AsAIAgent(
    deploymentName,
    instructions: "Remember user facts and recall them.",
    name: "MemoryAgent",
    tools: [FoundryAITool.FromResponseTool(memorySearchTool)]);

AgentSession session = await agent.CreateSessionAsync();
await agent.RunAsync("Hi! I'm Alice and I prefer aisle seats.", session);
Console.WriteLine(await agent.RunAsync("Recall what you know about me.", session));
```

### Direct CRUD on the memory store

```csharp
// Add a memory item
await projectClient.MemoryStores.AddMemoryAsync(
    memoryStoreName,
    new MemoryAddOptions
    {
        UserScope = userScope,
        Content = "User: Alice. Preference: window seats on long-haul flights.",
    });

// Search
MemorySearchResult result = await projectClient.MemoryStores.SearchMemoriesAsync(
    memoryStoreName,
    new MemorySearchOptions { UserScope = userScope, Query = "seat preference", TopK = 3 });

foreach (var memory in result.Value.Memories)
    Console.WriteLine($"  - {memory.Content}");
```

---

## `ChatHistoryMemoryProvider` (Vector-Store Transcripts)

For lightweight in-process transcript memory **without** a Foundry memory store, use `ChatHistoryMemoryProvider` over any `IVectorStore` — typically `InMemoryVectorStore`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.InMemory;
using Azure.AI.OpenAI;

AzureOpenAIClient openAi = new(new Uri(endpoint), new DefaultAzureCredential());
IEmbeddingGenerator<string, Embedding<float>> embedder =
    openAi.GetEmbeddingClient(embedModelName).AsIEmbeddingGenerator();

IVectorStore vectorStore = new InMemoryVectorStore();
ChatHistoryMemoryProvider chatMemory = new(vectorStore, embedder, collectionName: "conversations");

AIAgent agent = projectClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = "RecallAgent",
    ChatOptions = new() { ModelId = deploymentName, Instructions = "Recall prior turns when asked." },
    AIContextProviders = [chatMemory],
});
```

`ChatHistoryMemoryProvider` does not need an external memory store — it embeds turns into the vector store you provide, and retrieves the closest neighbours before each call.

---

## Choosing Between Memory Providers

```
┌────────────────────────────────────────────────────────────────────────────┐
│ Need cross-process / cross-device memory of user facts?                    │
│   YES → FoundryMemoryProvider (automatic, recommended)                     │
│         or MemorySearchPreviewTool (model-driven, hosted tool)             │
│   NO  → ChatHistoryMemoryProvider over InMemoryVectorStore                 │
│         (or Redis / Cosmos vector connector)                               │
└────────────────────────────────────────────────────────────────────────────┘
```

| Concern | `FoundryMemoryProvider` | `MemorySearchPreviewTool` | `ChatHistoryMemoryProvider` |
|---|---|---|---|
| Writes happen | Automatically after each turn | Automatically after each turn | Automatically after each turn |
| Reads happen | Before every turn | When the model decides | Before every turn |
| Storage | Foundry memory store | Foundry memory store | Any `IVectorStore` |
| Cross-process | ✅ | ✅ | Only if vector store is shared |
| Setup steps | `EnsureMemoryStoreCreatedAsync` | `CreateMemoryStoreAsync` | None |

---

## Common Pitfalls

- **Forgetting `WhenUpdatesCompletedAsync`** — extraction is async; you may not see memories on the very next call without it.
- **Per-session scope** — if you pass no `userId`, the scope is the session and memories don't carry across sessions.
- **Embedding deployment** — `FoundryMemoryProvider.EnsureMemoryStoreCreatedAsync` requires an embedding deployment configured on the project.
