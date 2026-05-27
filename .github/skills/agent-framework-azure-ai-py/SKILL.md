---
name: agent-framework-azure-ai-py
description: Build Azure AI Foundry agents using the Microsoft Agent Framework Python SDK (agent-framework-azure-ai). Use when creating persistent agents with AzureAIAgentsProvider, using hosted tools (code interpreter, file search, web search), integrating MCP servers, managing conversation threads, or implementing streaming responses. Covers function tools, structured outputs, and multi-tool agents.
license: MIT
metadata:
  author: Microsoft
  version: "1.0.0"
  package: agent-framework-azure-ai
---

# Agent Framework Azure Hosted Agents

Build persistent agents on Azure AI Foundry using the Microsoft Agent Framework Python SDK.

## Architecture

```
User Query → AzureAIAgentsProvider → Azure AI Agent Service (Persistent)
                    ↓
              Agent.run() / Agent.run_stream()
                    ↓
              Tools: Functions | Hosted (Code/Search/Web) | MCP
                    ↓
              AgentThread (conversation persistence)
```

## Installation

```bash
# Full framework (recommended)
pip install agent-framework --pre

# Or Azure-specific package only
pip install agent-framework-azure-ai --pre
```

> **Compatibility note (current prerelease).** Recent `agent-framework` builds publish on PyPI ship with `Agent` (in `agent_framework`) and `FoundryChatClient` (in `agent_framework.foundry`) as the supported high-level entry points, plus `MCPStreamableHTTPTool` for MCP. **`AzureAIAgentsProvider` and `HostedMCPTool` are documented below but may not be exported in your installed version** — if `from agent_framework.azure import AzureAIAgentsProvider` raises `ImportError`, use the **Foundry Chat Client** pattern lower in this file (verified by LAB 1 in the ZavaShop workshop) together with `MCPStreamableHTTPTool` from the [MCP reference](./references/mcp.md). API surface is the same: `Agent.run(..., session=session)`, `agent.create_session()`, function-tool list, `async with` lifecycle.

## Environment Variables

```bash
export AZURE_AI_PROJECT_ENDPOINT="https://<project>.services.ai.azure.com/api/projects/<project-id>"  # Required for all auth methods
export AZURE_AI_MODEL_DEPLOYMENT_NAME="gpt-4o-mini"  # Required for all auth methods
export BING_CONNECTION_ID="your-bing-connection-id"  # For web search
export AZURE_TOKEN_CREDENTIALS=prod # Required only if DefaultAzureCredential is used in production

# Foundry Toolbox (see references/foundry-toolbox.md)
export FOUNDRY_PROJECT_ENDPOINT="https://<account>.services.ai.azure.com/api/projects/<project-id>"
export FOUNDRY_MODEL="gpt-4o-mini"
export FOUNDRY_TOOLBOX_ENDPOINT="https://<account>.services.ai.azure.com/api/projects/<project>/toolsets/<name>/mcp?api-version=v1"
```

## Authentication & Lifecycle

> **🔑 Two rules apply to every code sample below:**
>
> 1. **Prefer `DefaultAzureCredential`.** It works locally (Azure CLI / VS Code / Developer CLI) and in Azure (managed identity, workload identity) with no code change. Avoid connection strings, account/API keys — they bypass Entra audit and rotation.
>    - Local dev: `DefaultAzureCredential` works as-is.
>    - Production: set `AZURE_TOKEN_CREDENTIALS=prod` (or `AZURE_TOKEN_CREDENTIALS=<specific_credential>`) to constrain the credential chain to production-safe credentials.
> 2. **Wrap every client in a context manager** so HTTP transports, sockets, and token caches are released deterministically:
>    - Sync: `with <Client>(...) as client:`
>    - Async: `async with <Client>(...) as client:` **and** `async with DefaultAzureCredential() as credential:` (from `azure.identity.aio`)
>
> Snippets may abbreviate this setup, but production code should always follow both rules.

```python
from azure.identity.aio import AzureCliCredential, DefaultAzureCredential, ManagedIdentityCredential

# Development
credential = AzureCliCredential()

# Production
# Local dev: DefaultAzureCredential. Production: set AZURE_TOKEN_CREDENTIALS=prod or AZURE_TOKEN_CREDENTIALS=<specific_credential>
credential = DefaultAzureCredential(require_envvar=True)
# Or use a specific credential directly in production:
# See https://learn.microsoft.com/python/api/overview/azure/identity-readme?view=azure-python#credential-classes
# credential = ManagedIdentityCredential()
```

## Core Workflow

### Basic Agent

```python
import asyncio
from agent_framework.azure import AzureAIAgentsProvider
from azure.identity.aio import AzureCliCredential

async def main():
    async with (
        AzureCliCredential() as credential,
        AzureAIAgentsProvider(credential=credential) as provider,
    ):
        agent = await provider.create_agent(
            name="MyAgent",
            instructions="You are a helpful assistant.",
        )
        
        result = await agent.run("Hello!")
        print(result.text)

asyncio.run(main())
```

### Agent with Function Tools

```python
from typing import Annotated
from pydantic import Field
from agent_framework.azure import AzureAIAgentsProvider
from azure.identity.aio import AzureCliCredential

def get_weather(
    location: Annotated[str, Field(description="City name to get weather for")],
) -> str:
    """Get the current weather for a location."""
    return f"Weather in {location}: 72°F, sunny"

def get_current_time() -> str:
    """Get the current UTC time."""
    from datetime import datetime, timezone
    return datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")

async def main():
    async with (
        AzureCliCredential() as credential,
        AzureAIAgentsProvider(credential=credential) as provider,
    ):
        agent = await provider.create_agent(
            name="WeatherAgent",
            instructions="You help with weather and time queries.",
            tools=[get_weather, get_current_time],  # Pass functions directly
        )
        
        result = await agent.run("What's the weather in Seattle?")
        print(result.text)
```

### Agent with Hosted Tools

```python
from agent_framework import (
    HostedCodeInterpreterTool,
    HostedFileSearchTool,
    HostedWebSearchTool,
)
from agent_framework.azure import AzureAIAgentsProvider
from azure.identity.aio import AzureCliCredential

async def main():
    async with (
        AzureCliCredential() as credential,
        AzureAIAgentsProvider(credential=credential) as provider,
    ):
        agent = await provider.create_agent(
            name="MultiToolAgent",
            instructions="You can execute code, search files, and search the web.",
            tools=[
                HostedCodeInterpreterTool(),
                HostedWebSearchTool(name="Bing"),
            ],
        )
        
        result = await agent.run("Calculate the factorial of 20 in Python")
        print(result.text)
```

### Streaming Responses

```python
async def main():
    async with (
        AzureCliCredential() as credential,
        AzureAIAgentsProvider(credential=credential) as provider,
    ):
        agent = await provider.create_agent(
            name="StreamingAgent",
            instructions="You are a helpful assistant.",
        )
        
        print("Agent: ", end="", flush=True)
        async for chunk in agent.run_stream("Tell me a short story"):
            if chunk.text:
                print(chunk.text, end="", flush=True)
        print()
```

### Conversation Threads

```python
from agent_framework.azure import AzureAIAgentsProvider
from azure.identity.aio import AzureCliCredential

async def main():
    async with (
        AzureCliCredential() as credential,
        AzureAIAgentsProvider(credential=credential) as provider,
    ):
        agent = await provider.create_agent(
            name="ChatAgent",
            instructions="You are a helpful assistant.",
            tools=[get_weather],
        )
        
        # Create thread for conversation persistence
        thread = agent.get_new_thread()
        
        # First turn
        result1 = await agent.run("What's the weather in Seattle?", thread=thread)
        print(f"Agent: {result1.text}")
        
        # Second turn - context is maintained
        result2 = await agent.run("What about Portland?", thread=thread)
        print(f"Agent: {result2.text}")
        
        # Save thread ID for later resumption
        print(f"Conversation ID: {thread.conversation_id}")
```

### Structured Outputs

```python
from pydantic import BaseModel, ConfigDict
from agent_framework.azure import AzureAIAgentsProvider
from azure.identity.aio import AzureCliCredential

class WeatherResponse(BaseModel):
    model_config = ConfigDict(extra="forbid")
    
    location: str
    temperature: float
    unit: str
    conditions: str

async def main():
    async with (
        AzureCliCredential() as credential,
        AzureAIAgentsProvider(credential=credential) as provider,
    ):
        agent = await provider.create_agent(
            name="StructuredAgent",
            instructions="Provide weather information in structured format.",
            response_format=WeatherResponse,
        )
        
        result = await agent.run("Weather in Seattle?")
        weather = WeatherResponse.model_validate_json(result.text)
        print(f"{weather.location}: {weather.temperature}°{weather.unit}")
```

## Provider Methods

| Method | Description |
|--------|-------------|
| `create_agent()` | Create new agent on Azure AI service |
| `get_agent(agent_id)` | Retrieve existing agent by ID |
| `as_agent(sdk_agent)` | Wrap SDK Agent object (no HTTP call) |

## Foundry Chat Client (Alternative Pattern)

For stateless agent calls without creating a persistent Azure AI agent resource, use `FoundryChatClient` directly with `Agent`:

```python
from agent_framework import Agent
from agent_framework.foundry import FoundryChatClient
from azure.identity import DefaultAzureCredential

async with Agent(
    client=FoundryChatClient(
        project_endpoint=os.environ["FOUNDRY_PROJECT_ENDPOINT"],
        model=os.environ["FOUNDRY_MODEL"],
        credential=DefaultAzureCredential(),
    ),
    instructions="You are a helpful assistant.",
    tools=...,
) as agent:
    result = await agent.run("Hello!")
```

See [references/foundry-toolbox.md](references/foundry-toolbox.md) for the full comparison with `AzureAIAgentsProvider` and the toolbox-consumption pattern.

## Agent Skills (SDK Skill Abstractions)

The SDK ships an **Agent Skills** runtime that lets an agent advertise modular capability bundles and load them on demand via progressive disclosure (advertise → load → read resource / run script). Skills can be defined three ways and freely composed:

- **Code-defined** — `InlineSkill` with `@skill.resource` and `@skill.script` decorators.
- **Class-based** — subclass `ClassSkill` and decorate methods with `@ClassSkill.resource` / `@ClassSkill.script`.
- **File-based** — `SKILL.md` directories on disk discovered by `FileSkillsSource` / `SkillsProvider.from_paths(...)`; file-based scripts require a `script_runner` (e.g. a subprocess runner).

Attach skills to any `Agent` via `context_providers=[SkillsProvider(...)]`:

```python
from agent_framework import Agent, InlineSkill, SkillFrontmatter, SkillsProvider
from agent_framework.foundry import FoundryChatClient
from azure.identity import AzureCliCredential

deploy_skill = InlineSkill(
    frontmatter=SkillFrontmatter(name="deployment", description="Deploy app versions."),
    instructions="Use the deploy script to ship a version to an environment.",
)

@deploy_skill.script
def deploy(version: str, environment: str = "staging") -> str:
    return f"Deployed {version} to {environment}"

async with Agent(
    client=FoundryChatClient(project_endpoint=..., model=..., credential=AzureCliCredential()),
    instructions="You are a deployment assistant.",
    context_providers=[SkillsProvider(deploy_skill, require_script_approval=True)],
) as agent:
    ...
```

Compose multiple sources with `AggregatingSkillsSource`, `DeduplicatingSkillsSource`, and `FilteringSkillsSource`. Gate sensitive scripts with `require_script_approval=True` and approve via `request.to_function_approval_response(...)` in the `result.user_input_requests` loop.

See [references/skills.md](references/skills.md) for the full guide (code/class/file patterns, source composition, approval loop, troubleshooting).

## Agent Memory (Foundry Managed)

Attach a Foundry memory store to any agent via `FoundryMemoryProvider` as a context provider. The provider auto-loads static user-profile memories on first run, searches contextual memories every turn, and writes new memories after each interaction.

```python
from agent_framework import Agent, InMemoryHistoryProvider
from agent_framework.foundry import FoundryChatClient, FoundryMemoryProvider

memory_provider = FoundryMemoryProvider(
    project_client=project_client,
    memory_store_name=memory_store.name,
    scope="user_123",       # per-user partition; defaults to session_id
    update_delay=0,         # immediate writes in dev; batch in prod
)

async with Agent(
    client=FoundryChatClient(project_client=project_client),
    instructions="You remember user preferences across sessions.",
    context_providers=[memory_provider, InMemoryHistoryProvider(load_messages=False)],
    default_options={"store": False},
) as agent:
    ...
```

Memory stores are created on the project (`project_client.beta.memory_stores.create(...)`) with both a **chat model** and an **embedding model** in the definition. Use a stable `scope` (e.g. user ID) for memories that should follow a user across sessions. On the current prerelease, wire `AIProjectClient(per_call_policies=[IdentityEncodingPolicy()])` where the policy sets `Accept-Encoding: identity`; otherwise `memory_stores.create` / `list` / `search_memories` can fail with `UnicodeDecodeError` on gzipped responses. Use uuid-suffixed store names for demos because delete + immediate same-name create can collide.

A 401 from the memory backend hitting the embedding deployment almost always means the **calling identity** lacks the embedding data action. The `Foundry User` role covers chat completions but NOT embeddings, and `FoundryMemoryProvider.search_memories` runs the embedding call under the caller's token. Grant the calling identity — for local dev that's the signed-in user (`az ad signed-in-user show --query id -o tsv`), for server-side hosting the app's managed identity — three roles: `Cognitive Services OpenAI User` on both the AI account and project scopes, plus `Cognitive Services User` on the account scope. The legacy `properties.agentIdentity.agentIdentityId` path is `null` on newer projects and is NOT the fix; only grant that ServiceIdentity SP the same three roles when `az resource show --ids .../projects/<project> --api-version 2025-04-01-preview --query "properties.agentIdentity.agentIdentityId" -o tsv` returns a non-empty value.

See [references/memory.md](references/memory.md) for full setup, `MemoryStoreDefaultDefinition`/`MemoryStoreDefaultOptions`, scope semantics, and troubleshooting.

## Agent Evaluation & Red-Teaming

Three evaluation surfaces ship with `agent-framework-azure-ai`:

- **`FoundryEvals`** — quality, behaviour, tool-use, and safety evaluators run in the Foundry portal.
- **`RedTeam`** — adversarial scans (PyRIT-backed) that score an agent's Attack Success Rate.
- **Self-reflection** — a critique-and-retry loop driven by a `FoundryEvals` score.

```python
from agent_framework import evaluate_agent
from agent_framework.foundry import FoundryChatClient, FoundryEvals, evaluate_traces

chat_client = FoundryChatClient(project_endpoint=..., model=..., credential=AzureCliCredential())
agent_evals = FoundryEvals(
    client=chat_client,
    evaluators=[FoundryEvals.RELEVANCE, FoundryEvals.TOOL_CALL_ACCURACY],
)

# Dev inner loop: run + evaluate in one call
results = await evaluate_agent(agent=agent, queries=[...], evaluators=agent_evals)

# Zero-code-change: score responses or App Insights traces after the fact
results = await evaluate_traces(
    response_ids=["resp_abc123", "resp_def456"],
    evaluators=[FoundryEvals.RELEVANCE, FoundryEvals.GROUNDEDNESS, FoundryEvals.TOOL_CALL_ACCURACY],
    client=chat_client,
)
```

Use `ConversationSplit.{LAST_TURN, FULL}` or `EvalItem.per_turn_items(...)` to slice multi-turn conversations. Use `evaluate_workflow(...)` for multi-agent orchestrations; each result entry exposes `sub_results` per agent. For `evaluate_agent(...)` on `agent-framework 1.0.0rc3`, keep the evaluator list to `FoundryEvals.RELEVANCE` + `FoundryEvals.TOOL_CALL_ACCURACY`: `FoundryEvals.GROUNDEDNESS` currently fails the agent-eval dataset path with `MissingRequiredDataMapping: tool_definitions`. Use groundedness only through direct `FoundryEvals.evaluate([EvalItem(..., context=...)])` / self-reflection flows where you provide `context=` manually. Per-item pass/fail is on `scores[].passed`; `EvalItemResult.status == "completed"` is just run status.

For adversarial testing, install `azure-ai-evaluation[redteam]`. With azure-ai-evaluation 1.16+, import `RedTeam` from `azure.ai.evaluation.red_team`, pass the Foundry project endpoint URL string directly as `azure_ai_project`, and pass a **sync** `azure.identity.AzureCliCredential` to `RedTeam(...)` even when the agent code uses `azure.identity.aio.AzureCliCredential`. Pin `pyrit==0.11.0` for azure-ai-evaluation 1.16+ (`pyrit==0.9.0` for older evaluation packages). Compose attack strategies with nested lists such as `[AttackStrategy.Base64, AttackStrategy.ROT13]` when `AttackStrategy.Compose(...)` is unavailable. Target **ASR < 5%** before production.

See [references/evaluation.md](references/evaluation.md) for the full evaluator catalog, the `AgentEvalConverter` pattern, multi-turn / workflow / similarity flows, red-team strategy list, and the Reflexion self-reflection loop.

## Hosted Tools Quick Reference

| Tool | Import | Purpose |
|------|--------|---------|
| `HostedCodeInterpreterTool` | `from agent_framework import HostedCodeInterpreterTool` | Execute Python code |
| `HostedFileSearchTool` | `from agent_framework import HostedFileSearchTool` | Search vector stores |
| `HostedWebSearchTool` | `from agent_framework import HostedWebSearchTool` | Bing web search |
| `HostedMCPTool` | `from agent_framework import HostedMCPTool` | Service-managed MCP |
| `MCPStreamableHTTPTool` | `from agent_framework import MCPStreamableHTTPTool` | Client-managed MCP |

## Complete Example

```python
import asyncio
from typing import Annotated
from pydantic import BaseModel, Field
from agent_framework import (
    HostedCodeInterpreterTool,
    HostedWebSearchTool,
    MCPStreamableHTTPTool,
)
from agent_framework.azure import AzureAIAgentsProvider
from azure.identity.aio import AzureCliCredential


def get_weather(
    location: Annotated[str, Field(description="City name")],
) -> str:
    """Get weather for a location."""
    return f"Weather in {location}: 72°F, sunny"


class AnalysisResult(BaseModel):
    summary: str
    key_findings: list[str]
    confidence: float


async def main():
    async with (
        AzureCliCredential() as credential,
        MCPStreamableHTTPTool(
            name="Docs MCP",
            url="https://learn.microsoft.com/api/mcp",
        ) as mcp_tool,
        AzureAIAgentsProvider(credential=credential) as provider,
    ):
        agent = await provider.create_agent(
            name="ResearchAssistant",
            instructions="You are a research assistant with multiple capabilities.",
            tools=[
                get_weather,
                HostedCodeInterpreterTool(),
                HostedWebSearchTool(name="Bing"),
                mcp_tool,
            ],
        )
        
        thread = agent.get_new_thread()
        
        # Non-streaming
        result = await agent.run(
            "Search for Python best practices and summarize",
            thread=thread,
        )
        print(f"Response: {result.text}")
        
        # Streaming
        print("\nStreaming: ", end="")
        async for chunk in agent.run_stream("Continue with examples", thread=thread):
            if chunk.text:
                print(chunk.text, end="", flush=True)
        print()
        
        # Structured output
        result = await agent.run(
            "Analyze findings",
            thread=thread,
            response_format=AnalysisResult,
        )
        analysis = AnalysisResult.model_validate_json(result.text)
        print(f"\nConfidence: {analysis.confidence}")


if __name__ == "__main__":
    asyncio.run(main())
```

## Conventions

- Always use async context managers: `async with provider:`
- Pass functions directly to `tools=` parameter (auto-converted to AIFunction)
- Use `Annotated[type, Field(description=...)]` for function parameters
- Use `get_new_thread()` for multi-turn conversations
- Prefer `HostedMCPTool` for service-managed MCP, `MCPStreamableHTTPTool` for client-managed

## Best Practices

1. **This SDK is async-first** — use `async def` handlers and `async with` throughout.
2. **Always use context managers for clients and async credentials.** Wrap every client in `with Client(...) as client:` (sync) or `async with Client(...) as client:` (async). For async `DefaultAzureCredential` from `azure.identity.aio`, also use `async with credential:` so tokens and transports are cleaned up.

## Reference Files

- [references/tools.md](references/tools.md): Detailed hosted tool patterns
- [references/mcp.md](references/mcp.md): MCP integration (hosted + local)
- [references/foundry-toolbox.md](references/foundry-toolbox.md): Foundry Toolbox + `FoundryChatClient` + `header_provider` patterns
- [references/skills.md](references/skills.md): Agent Skills (code/class/file), source composition, script approval, filtering
- [references/memory.md](references/memory.md): `FoundryMemoryProvider`, memory stores, scope semantics, chat-history coexistence
- [references/evaluation.md](references/evaluation.md): `FoundryEvals`, `evaluate_agent` / `evaluate_traces` / `evaluate_workflow`, multi-turn splits, red-teaming, self-reflection loop
- [references/threads.md](references/threads.md): Thread and conversation management
- [references/advanced.md](references/advanced.md): OpenAPI, citations, structured outputs
