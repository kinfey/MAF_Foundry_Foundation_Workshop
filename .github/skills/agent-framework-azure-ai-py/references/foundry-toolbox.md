# Foundry Toolbox Reference

Patterns for using **Foundry Toolboxes** — server-side managed MCP toolsets configured in a Microsoft Foundry project — with the Microsoft Agent Framework Python SDK.

## What is a Foundry Toolbox?

A Foundry Toolbox is a **versioned, server-side bundle of tools** (typically MCP tools) configured against a Foundry project. Instead of declaring tools inside each application, you configure them once in the Foundry portal (or via the `azure.ai.projects` SDK), then point any agent at the toolbox's MCP endpoint:

```
https://<account>.services.ai.azure.com/api/projects/<project>/toolsets/<name>/mcp?api-version=v1
```

Agents call the toolbox over MCP at runtime and discover its tools dynamically — you do **not** fan tools out into individual specs in your code.

### When to use a toolbox

| Use a toolbox when… | Use `HostedMCPTool` / `MCPStreamableHTTPTool` directly when… |
|---|---|
| Tools should be centrally managed and versioned per Foundry project | Tools are app-specific or experimental |
| Multiple agents/apps share the same tool set | Only one agent uses the tool |
| Tool config (URLs, approval policy) should change without redeploying apps | Tool URLs are stable and owned by the app |

---

## Environment Variables

```bash
export FOUNDRY_PROJECT_ENDPOINT="https://<account>.services.ai.azure.com/api/projects/<project-id>"
export FOUNDRY_MODEL="gpt-4o-mini"
export FOUNDRY_TOOLBOX_ENDPOINT="https://<account>.services.ai.azure.com/api/projects/<project>/toolsets/<name>/mcp?api-version=v1"
```

The `<name>` segment in `FOUNDRY_TOOLBOX_ENDPOINT` must match the toolbox name used at creation time.

---

## Configuring a Toolbox (Server-Side)

Toolboxes are normally configured in the Foundry portal. For end-to-end automation, use the `azure.ai.projects` SDK to create or replace a toolbox version:

```python
import os
from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import MCPTool, Tool
from azure.core.exceptions import ResourceNotFoundError
from azure.identity import AzureCliCredential


def create_sample_toolbox(name: str) -> str:
    """Create (or replace) a toolbox version. Returns the new version id."""
    with (
        AzureCliCredential() as credential,
        AIProjectClient(
            credential=credential,
            endpoint=os.environ["FOUNDRY_PROJECT_ENDPOINT"],
        ) as project_client,
    ):
        try:
            project_client.beta.toolboxes.delete(name)
        except ResourceNotFoundError:
            pass

        tools: list[Tool] = [
            MCPTool(
                server_label="api_specs",
                server_url="https://gitmcp.io/Azure/azure-rest-api-specs",
                require_approval="never",
            ),
        ]

        created = project_client.beta.toolboxes.create_version(
            name=name,
            description="Toolbox version with MCP require_approval set to 'never'.",
            tools=tools,
        )
        return created.version
```

Notes:
- `project_client.beta.toolboxes.*` is a beta API — surface and method names may evolve.
- `create_version` is idempotent on `(name, version)` but **creates a new version each call**; delete-then-create when you want to replace.
- Use `require_approval="never"` for unattended agents; otherwise the agent will be blocked waiting for human approval.

---

## Consuming a Toolbox from an Agent

Point `MCPStreamableHTTPTool` at the toolbox endpoint. The agent discovers and calls toolbox tools over MCP at runtime.

### With `FoundryChatClient`

> **Prerelease caveat.** `agent_framework.foundry` does **not** export `make_toolbox_header_provider` in the currently-shipped build — trying `from agent_framework.foundry import make_toolbox_header_provider` raises `ImportError`. Declare it as a local 5-line closure (see below) until the SDK ships the helper.

```python
import asyncio
import os
from collections.abc import Callable
from typing import Any

from agent_framework import Agent, MCPStreamableHTTPTool
from agent_framework.foundry import FoundryChatClient
from azure.core.credentials import TokenCredential
from azure.identity import DefaultAzureCredential, get_bearer_token_provider


# Declare this helper LOCALLY — it is NOT exported from `agent_framework.foundry`
# in the installed prerelease.
def make_toolbox_header_provider(
    credential: TokenCredential,
) -> Callable[[dict[str, Any]], dict[str, str]]:
    """Inject a fresh Azure AI bearer token on every MCP request."""
    get_token = get_bearer_token_provider(credential, "https://ai.azure.com/.default")

    def provide(_kwargs: dict[str, Any]) -> dict[str, str]:
        return {"Authorization": f"Bearer {get_token()}"}

    return provide


async def main() -> None:
    credential = DefaultAzureCredential()

    toolbox_tool = MCPStreamableHTTPTool(
        name="foundry_toolbox",
        description="Tools exposed by the configured Foundry toolbox",
        url=os.environ["FOUNDRY_TOOLBOX_ENDPOINT"],
        header_provider=make_toolbox_header_provider(credential),
        load_prompts=False,
    )

    async with Agent(
        client=FoundryChatClient(
            project_endpoint=os.environ["FOUNDRY_PROJECT_ENDPOINT"],
            model=os.environ["FOUNDRY_MODEL"],
            credential=credential,
        ),
        instructions="You are a helpful assistant. Use the toolbox tools to answer the user.",
        tools=toolbox_tool,
    ) as agent:
        result = await agent.run("What tools do you have access to?")
        print(result)


if __name__ == "__main__":
    asyncio.run(main())
```

### With `AzureAIAgentsProvider`

The same toolbox endpoint works for the persistent-agents provider:

```python
from agent_framework import MCPStreamableHTTPTool
from agent_framework.azure import AzureAIAgentsProvider
from azure.identity.aio import DefaultAzureCredential

async with (
    DefaultAzureCredential() as credential,
    MCPStreamableHTTPTool(
        name="foundry_toolbox",
        url=os.environ["FOUNDRY_TOOLBOX_ENDPOINT"],
        header_provider=make_toolbox_header_provider(credential),
        load_prompts=False,
    ) as toolbox_tool,
    AzureAIAgentsProvider(credential=credential) as provider,
):
    agent = await provider.create_agent(
        name="ToolboxAgent",
        instructions="Use the toolbox tools to answer the user.",
        tools=toolbox_tool,
    )
    result = await agent.run("List your available tools.")
    print(result.text)
```

---

## `header_provider` vs Static `headers`

For Foundry toolbox endpoints, **prefer `header_provider`** over a static `http_client`/`headers` dict:

| Approach | Token refresh | Use case |
|---|---|---|
| `header_provider=...` (callable) | Per-request via `get_bearer_token_provider` | Long-running agents, Azure AD tokens, any scoped credential |
| `http_client=AsyncClient(headers={...})` | None — header is fixed for the client's lifetime | Static API keys, opaque tokens that never expire |

`get_bearer_token_provider(credential, "https://ai.azure.com/.default")` returns a callable that caches and refreshes tokens automatically, so the header provider stays cheap to call.

---

## `FoundryChatClient` vs `AzureAIAgentsProvider`

Both target Foundry, but serve different shapes:

| | `FoundryChatClient` | `AzureAIAgentsProvider` |
|---|---|---|
| Import | `from agent_framework.foundry import FoundryChatClient` | `from agent_framework.azure import AzureAIAgentsProvider` |
| Agent construction | `Agent(client=FoundryChatClient(...), tools=...)` | `await provider.create_agent(name=..., tools=...)` |
| Server-side persistence | No (chat-completions style; ephemeral) | Yes (creates a persistent Azure AI agent resource) |
| Best for | Stateless agent calls, simple toolbox consumers | Multi-turn agents with managed threads, file search, vector stores |

The toolbox pattern works with both — choose the client based on whether you need a persistent agent resource on the service.

---

## Troubleshooting

- **`401 Unauthorized` from the toolbox URL** — verify the token scope is `https://ai.azure.com/.default` (not `https://cognitiveservices.azure.com/.default`) and that the principal has the **Azure AI User** role on the project.
- **`Toolbox not found` / `404`** — the `<name>` in `FOUNDRY_TOOLBOX_ENDPOINT` must match a created toolbox; check `project_client.beta.toolboxes.list()`.
- **Agent says "no tools available"** — the toolbox version may contain tools with `require_approval` set; either set `require_approval="never"` at toolbox creation, or handle approval in the agent loop.
- **MCP connection hangs on startup** — pass `load_prompts=False` to `MCPStreamableHTTPTool` to skip prompt-listing during initialization (toolboxes typically don't expose prompts).
