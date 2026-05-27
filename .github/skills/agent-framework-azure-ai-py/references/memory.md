# Foundry Memory Context Provider

`FoundryMemoryProvider` plugs Azure AI Foundry's **memory store** into an `Agent` as a context provider. The provider automatically:

1. Retrieves **static memories** (user profile) on the first run.
2. Searches for **contextual memories** relevant to the current conversation on every turn.
3. Updates the memory store with new messages after each interaction (optionally batched).

Memories survive across sessions and across processes — unlike an in-memory thread/history provider, which is per-process.

## Prerequisites

- A Foundry project with **two** deployed models:
  - A chat/responses model (e.g. `gpt-4o`).
  - An **embedding** model (e.g. `text-embedding-3-small`).
- Azure CLI auth (`az login`) for local dev; `DefaultAzureCredential` for production.

## Environment Variables

```bash
export FOUNDRY_PROJECT_ENDPOINT="https://<account>.services.ai.azure.com/api/projects/<project>"
export FOUNDRY_MODEL="gpt-4o"
export AZURE_OPENAI_EMBEDDING_MODEL="text-embedding-3-small"
```

## Creating a Memory Store

Memory stores are created on the project, not on the agent. Each store has a definition (which chat + embedding models to use) and options (what to capture):

```python
from datetime import datetime, timezone
from azure.ai.projects.aio import AIProjectClient
from azure.ai.projects.models import MemoryStoreDefaultDefinition, MemoryStoreDefaultOptions
from azure.identity.aio import AzureCliCredential

async with (
    AzureCliCredential() as credential,
    AIProjectClient(endpoint=endpoint, credential=credential) as project_client,
):
    memory_store_name = f"agent_framework_memory_{datetime.now(timezone.utc):%Y%m%d}"

    options = MemoryStoreDefaultOptions(
        chat_summary_enabled=False,                       # full chat summarisation off
        user_profile_enabled=True,                        # extract durable user facts
        user_profile_details=(
            "Avoid irrelevant or sensitive data, such as age, financials, "
            "precise location, and credentials"
        ),
    )

    definition = MemoryStoreDefaultDefinition(
        chat_model=os.environ["FOUNDRY_MODEL"],
        embedding_model=os.environ["AZURE_OPENAI_EMBEDDING_MODEL"],
        options=options,
    )

    memory_store = await project_client.beta.memory_stores.create(
        name=memory_store_name,
        description="Memory store for Agent Framework",
        definition=definition,
    )
```

Stores accumulate cost over time. In dev, delete them at the end of the run:

```python
await project_client.beta.memory_stores.delete(memory_store_name)
```

## Attaching the Provider to an Agent

```python
from agent_framework import Agent, InMemoryHistoryProvider
from agent_framework.foundry import FoundryChatClient, FoundryMemoryProvider

memory_provider = FoundryMemoryProvider(
    project_client=project_client,
    memory_store_name=memory_store.name,
    scope="user_123",   # per-user partition; defaults to session_id if omitted
    update_delay=0,     # 0 = update immediately (demo); use >0 in production to batch
)

client = FoundryChatClient(project_client=project_client)

async with Agent(
    name="MemoryAgent",
    client=client,
    instructions=(
        "You are a helpful assistant that remembers past conversations. "
        "The memories from previous interactions are automatically provided to you."
    ),
    context_providers=[
        memory_provider,
        InMemoryHistoryProvider(load_messages=False),  # see "Disabling chat history" below
    ],
    default_options={"store": False},                  # don't persist service-side messages
) as agent:
    session = agent.create_session()
    await agent.run("I prefer dark roast coffee and I'm allergic to nuts", session=session)
    await asyncio.sleep(8)   # let memory extraction finish
    print(await agent.run("Recommend a coffee and snack for me?", session=session))
```

## `FoundryMemoryProvider` Parameters

| Parameter | Purpose |
|-----------|---------|
| `project_client` | Async `AIProjectClient` to talk to the Foundry project |
| `memory_store_name` | Name of an **existing** memory store to read/write |
| `scope` | Partition key for memories. Common choice: a stable user ID. **Defaults to the session id** — i.e. memories are siloed per session unless you set it. |
| `update_delay` | Seconds to wait before flushing new memories. `0` writes immediately (good for demos / tests). In production, use a higher value to batch updates and reduce embedding cost. |

## Scope: User vs Session

- **`scope="user_<id>"`** — durable memory that follows the user across sessions and devices. Use for preferences, profile facts, and long-running context.
- **`scope` unset** — the provider uses the `session_id` as scope, so memories are isolated per session. Useful for sandboxes, untrusted users, or scratch contexts.

You can attach two providers with different scopes (e.g. one user-scoped and one session-scoped) if you want both layers.

## Disabling Chat History Storage

The sample combines `FoundryMemoryProvider` with `InMemoryHistoryProvider(load_messages=False)` and `default_options={"store": False}`:

- `store=False` tells the Responses API **not to persist messages service-side**.
- `load_messages=False` tells the local history provider **not to replay messages** from prior turns.

Together these force the agent to rely solely on Foundry memory for context — proving the memory provider works. In real apps, leave `store=True` and `load_messages=True` so the agent benefits from both message history and semantic memory.

## Inspecting Stored Memories

```python
res = await project_client.beta.memory_stores.search_memories(
    name=memory_store.name,
    scope="user_123",
)
for memory in res.memories:
    print(memory.memory_item.content)
```

Typical extracted content:

```text
The user is allergic to nuts.
The user prefers dark roast coffee.
```

## When to Use Foundry Memory vs Other Patterns

| Need | Use |
|------|-----|
| Cross-session, cross-device user profile that persists | **`FoundryMemoryProvider`** |
| Single-process scratch memory during a run | `InMemoryHistoryProvider` |
| Threads / explicit conversation transcripts | `AgentThread` from `AzureAIAgentsProvider` (see [threads.md](threads.md)) |
| Retrieval over arbitrary documents | `HostedFileSearchTool` or an Azure AI Search context provider |

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `Failed to create memory store` | Wrong project endpoint or missing RBAC | Use the project endpoint from Foundry > Settings; assign `Azure AI Developer` on the project |
| Empty memories on follow-up | `update_delay` too high or follow-up issued too quickly | Use `update_delay=0` in tests; add `await asyncio.sleep(...)` between writes and reads |
| Memories ignored across sessions | `scope` defaulted to `session_id` | Set `scope` to a stable user ID |
| Embedding errors | Embedding model not deployed in the project | Deploy `text-embedding-3-small` (or similar) and set `AZURE_OPENAI_EMBEDDING_MODEL` |
| Cost growing unexpectedly | `update_delay=0` in production | Raise `update_delay` to batch writes; periodically delete unused stores |
| Service-side messages duplicating memory | `store=True` while also using `FoundryMemoryProvider` | Either keep both (full transcript + memory) or set `default_options={"store": False}` to rely only on memory |
| `401` from the memory backend when calling the embedding deployment | The **calling identity** (the credential you pass to `AIProjectClient`) is missing the embedding data action. `Foundry User` covers chat completions but NOT embeddings, and `FoundryMemoryProvider.search_memories` runs under the caller's token | Grant the calling identity (signed-in user locally, managed identity in production) `Cognitive Services OpenAI User` on BOTH the AI account and project scopes, plus `Cognitive Services User` on the account scope. Only when `properties.agentIdentity.agentIdentityId` is non-empty on the project must you grant the same three roles to that separate ServiceIdentity SP — newer projects return `null` and don't need it |
| `UnicodeDecodeError` from `memory_stores.create` / `list` / `search_memories` | Server returns gzipped response that the prerelease SDK does not decode | Attach an `Accept-Encoding: identity` policy via `AIProjectClient(per_call_policies=[...])` — see snippet below |
| `delete(name)` then immediate `create(name)` returns a 409 / soft-tombstone | Memory store names linger after delete | Always use a unique suffix: `f"zavashop-memory-{uuid.uuid4().hex[:8]}"` |
| Session 2 doesn't recall facts even after a long sleep | `FoundryMemoryProvider.after_run` is fire-and-forget — it submits `begin_update_memories` but doesn't await the poller | For demos, explicitly call `await project_client.beta.memory_stores.begin_update_memories(name=..., scope=..., items=[...], update_delay=0)` and `await poller.result()`, then poll `search_memories` until rows land — don't `asyncio.sleep()` and hope |
| Memory store has rows but Session 2 still ignores them | Seed blurb was action-oriented (e.g. *"ship my order to..."*) — the memory extractor only stores declarative user facts | Phrase the seed as personal facts: *"My name is X. I prefer Y. I am allergic to Z. My delivery window is W."* |

### Identity / gzip workaround (verified on agent-framework 1.0.0rc3)

```python
from azure.core.pipeline.policies import SansIOHTTPPolicy

class IdentityEncodingPolicy(SansIOHTTPPolicy):
    """Force `Accept-Encoding: identity` so memory_stores responses are not gzipped."""
    def on_request(self, request):  # type: ignore[override]
        request.http_request.headers["Accept-Encoding"] = "identity"

async with (
    AzureCliCredential() as credential,
    AIProjectClient(
        endpoint=endpoint,
        credential=credential,
        per_call_policies=[IdentityEncodingPolicy()],
    ) as project_client,
):
    ...
```

### Synchronous demo-flush (verified pattern for `update_delay=0` runs)

```python
# After Session 1's turn, explicitly flush + wait for the poller, then poll
# search_memories until rows appear. Don't rely on `asyncio.sleep()`.
update_poller = await project_client.beta.memory_stores.begin_update_memories(
    name=memory_store.name,
    scope=f"customer_{customer_id}",
    items=[
        {"role": "user", "type": "message", "content": prefs_blurb},
        {"role": "assistant", "type": "message", "content": seed_result.text},
    ],
    update_delay=0,
)
try:
    await update_poller.result()
except Exception:
    # The provider's own after_run write usually lands even when this
    # explicit call surfaces a 401 — fall through to the polling loop.
    pass

for _ in range(12):
    res = await project_client.beta.memory_stores.search_memories(
        name=memory_store.name,
        scope=f"customer_{customer_id}",
    )
    if res.memories:
        break
    await asyncio.sleep(5)
```

### Granting RBAC to the calling identity

For local development, the calling identity is your signed-in user. For server-side hosting, it's the app's managed identity. Either way, the same three roles must land on it:

```bash
# Local dev — signed-in user is the caller.
caller_object_id=$(az ad signed-in-user show --query id -o tsv)
caller_principal_type=User

account_scope="/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.CognitiveServices/accounts/<account>"
project_scope="$account_scope/projects/<project>"

# 1. Lets the caller's token call the embedding deployment that
#    FoundryMemoryProvider.search_memories invokes on the data plane.
az role assignment create --assignee-object-id "$caller_object_id" \
  --assignee-principal-type "$caller_principal_type" \
  --role "Cognitive Services OpenAI User" --scope "$account_scope"

# 2. Same role at project scope (memory store / search lives under the project).
az role assignment create --assignee-object-id "$caller_object_id" \
  --assignee-principal-type "$caller_principal_type" \
  --role "Cognitive Services OpenAI User" --scope "$project_scope"

# 3. Cognitive Services User on the account is the third leg — missing this
#    leaves search_memories returning a 401 even with the two above.
az role assignment create --assignee-object-id "$caller_object_id" \
  --assignee-principal-type "$caller_principal_type" \
  --role "Cognitive Services User" --scope "$account_scope"
```

**Legacy `properties.agentIdentity` projects only.** A small subset of older Foundry projects expose a separate ServiceIdentity SP at `properties.agentIdentity.agentIdentityId`. The memory backend uses THAT SP for data-plane calls in those projects, so you must repeat the same three role assignments with the SP's object id and `--assignee-principal-type ServicePrincipal`. Newer projects return `null` here — skip this step:

```bash
az resource show \
  --ids "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.CognitiveServices/accounts/<account>/projects/<project>" \
  --api-version 2025-04-01-preview \
  --query "properties.agentIdentity.agentIdentityId" -o tsv
# empty output → modern project, skip the agentIdentity path entirely.
```

## Public API Reference

```python
from agent_framework import Agent, InMemoryHistoryProvider
from agent_framework.foundry import FoundryChatClient, FoundryMemoryProvider
from azure.ai.projects.aio import AIProjectClient
from azure.ai.projects.models import (
    MemoryStoreDefaultDefinition,
    MemoryStoreDefaultOptions,
)
```

- `project_client.beta.memory_stores.create(name, description, definition)` — create a memory store.
- `project_client.beta.memory_stores.delete(name)` — delete a memory store.
- `project_client.beta.memory_stores.search_memories(name, scope)` — inspect stored memories.

## Related Sample

- [azure_ai_foundry_memory.py](https://github.com/microsoft/agent-framework/blob/main/python/samples/02-agents/context_providers/azure_ai_foundry_memory.py)
