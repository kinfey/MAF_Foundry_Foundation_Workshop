# Agent + `AGUIChatClient` — Hybrid Tool Execution

Wrapping `AGUIChatClient` in an `Agent` unlocks three things:

1. **Client-side tool execution** — `@tool`s passed to the `Agent` run locally when the server LLM requests them. The function-invocation middleware lives on `Agent`, not on the raw chat client.
2. **Client-managed history** — `agent.create_session()` stores the conversation, and every `agent.run(...)` forwards the full history to the server.
3. **Hybrid execution** — server tools and client tools coexist in the same conversation; the server LLM transparently picks the right one.

## The pattern

```python
import asyncio
import os
from agent_framework import Agent, tool
from agent_framework.ag_ui import AGUIChatClient


@tool(description="Get the current weather for a location.")
def get_weather(location: str) -> str:
    print(f"[CLIENT] get_weather({location})")           # runs locally
    weather = {
        "seattle":       "Rainy, 55°F",
        "san francisco": "Foggy, 62°F",
        "new york":      "Sunny, 68°F",
        "london":        "Cloudy, 52°F",
    }
    return weather.get(location.lower(), f"No data for {location}")


async def main() -> None:
    server_url = os.environ.get("AGUI_SERVER_URL", "http://127.0.0.1:5100/")
    async with AGUIChatClient(endpoint=server_url) as remote:
        agent = Agent(
            name="remote_assistant",
            instructions="You are a helpful assistant. Remember details across turns.",
            client=remote,
            tools=[get_weather],                          # client-side tool
        )
        session = agent.create_session()

        # Turn 1 — establish context
        async for chunk in agent.run("My name is Alice and I live in Seattle.",
                                     stream=True, session=session):
            if chunk.text:
                print(chunk.text, end="", flush=True)
        print()

        # Turn 2 — tests history (no tool needed)
        async for chunk in agent.run("Where do I live?", stream=True, session=session):
            if chunk.text:
                print(chunk.text, end="", flush=True)
        print()

        # Turn 3 — invokes client-side tool
        async for chunk in agent.run("What's the weather here?", stream=True, session=session):
            if chunk.text:
                print(chunk.text, end="", flush=True)
        print()

        # Turn 4 — invokes server-side tool (e.g. get_time_zone) without any client wiring
        async for chunk in agent.run("And what time zone am I in?", stream=True, session=session):
            if chunk.text:
                print(chunk.text, end="", flush=True)
        print()


asyncio.run(main())
```

Run it against the server from [server.md](server.md) — `get_weather` executes in the client; `get_time_zone` executes on the server; both results blend in the final answer.

## Why `agent.create_session()`?

`AgentSession` (same shape as the .NET `AgentSession`) tracks conversation context locally:

- Every `agent.run(..., session=session)` reads/writes the session's message store.
- The agent sends the **full** history to the server on each call — so even a stateless server can support multi-turn dialog.
- Two parallel sessions on the same `Agent` are independent — useful for serving many users from one process.

```python
async def handle_user(agent, query: str) -> str:
    session = agent.create_session()
    return await agent.run(query, session=session)

results = await asyncio.gather(*(handle_user(agent, q) for q in user_queries))
```

## Why hybrid?

You're free to pick the side that owns each capability:

| Tool runs on… | Pick this side | Examples |
| --- | --- | --- |
| **Client** | Browser-only APIs, OS shells, local files, user secrets you must not ship to the server | `get_weather` against a cached file, `read_local_file`, `take_screenshot` |
| **Server** | Heavy work, paid APIs, shared databases, anything privileged or behind a firewall | `get_time_zone` lookup, vector DB query, image generation |

The server LLM sees **all** tool definitions (client + server) and decides which to invoke per turn. The function-invocation mixin on `Agent` intercepts only the calls whose `name` matches a client tool — the rest are handled server-side.

### Wiring rules

| Side | Definition | Execution |
| --- | --- | --- |
| Server | `Agent(tools=[server_only_tools])` | In-process inside FastAPI |
| Client | `Agent(client=AGUIChatClient(...), tools=[client_tools])` | In-process inside the client app |
| **Never** | Same tool on both sides | — |

If both sides register the same tool, behavior is undefined — pick one.

## Direct `agent.run()` vs. raw `client.get_response()`

| | `agent.run(...)` (recommended for tools) | `client.get_response(...)` (raw) |
| --- | --- | --- |
| Client-side `@tool` execution | ✅ via function-invocation mixin | ❌ definitions only |
| Conversation history | ✅ via `AgentSession` | Server thread only (`metadata={"thread_id": ...}`) |
| Streaming | ✅ `stream=True` returns chunk iterator | ✅ `stream=True` returns `ResponseStream` |
| Per-call options | `agent.run(..., options={...})` | `client.get_response(..., options={...})` |
| When to use | Building an interactive agent UI | Lightweight protocol probes, server-tools-only flows |

## Streaming inside `agent.run`

```python
async for chunk in agent.run("question", stream=True, session=session):
    if chunk.text:                                # text delta
        print(chunk.text, end="", flush=True)
```

Chunks expose `.text` for convenience, but also `.contents` (full `Content` list — function calls, approval requests, etc.) if you need to render structured events.

## Running parallel sessions on one agent

```python
agent = Agent(name="hub", client=remote, tools=[get_weather])
sessions = {user_id: agent.create_session() for user_id in user_ids}

async def reply(user_id: str, query: str) -> str:
    return await agent.run(query, session=sessions[user_id])

await asyncio.gather(*[reply(uid, q) for uid, q in pending_messages])
```

One `Agent` + one `AGUIChatClient` can fan out across many users — reuse them, don't recreate them per request.

## Debugging

Enable verbose logging to see request/response wiring and tool dispatch:

```python
import logging
logging.basicConfig(level=logging.DEBUG, format="%(asctime)s - %(name)s - %(levelname)s - %(message)s")
```

Look for `[CLIENT]` prints in your tools to confirm client-side execution, and matching `[SERVER]` prints in server logs to confirm hybrid routing.
