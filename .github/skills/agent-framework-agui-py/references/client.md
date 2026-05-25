# AG-UI Client — Direct `AGUIChatClient` Usage

`AGUIChatClient` implements the Agent Framework's `BaseChatClient` interface against an AG-UI HTTP server. Use it directly for stateless chat, or wrap it in an `Agent` for client-side tools and history (see [agent_integration.md](agent_integration.md)).

## Lifecycle

```python
from agent_framework.ag_ui import AGUIChatClient

async with AGUIChatClient(endpoint="http://127.0.0.1:5100/") as client:
    ...
```

Always use the async context manager. It owns the underlying `httpx.AsyncClient` connection pool — leaking it leads to socket exhaustion.

## Streaming response

```python
import asyncio
import os
from typing import cast
from agent_framework import ChatResponse, ChatResponseUpdate, Message, ResponseStream
from agent_framework.ag_ui import AGUIChatClient


async def main() -> None:
    server_url = os.environ.get("AGUI_SERVER_URL", "http://127.0.0.1:5100/")
    async with AGUIChatClient(endpoint=server_url) as client:
        thread_id: str | None = None
        while True:
            message = input("\nUser (:q to quit): ").strip()
            if message.lower() in (":q", "quit"):
                break
            if not message:
                continue

            metadata = {"thread_id": thread_id} if thread_id else None
            stream = client.get_response(
                [Message(role="user", contents=[message])],
                stream=True,
                options={"metadata": metadata} if metadata else None,
            )
            stream = cast(ResponseStream[ChatResponseUpdate, ChatResponse], stream)

            print("Assistant: ", end="", flush=True)
            async for update in stream:
                if not thread_id and update.additional_properties:
                    thread_id = update.additional_properties.get("thread_id")
                    print(f"\n[Thread: {thread_id}]\nAssistant: ", end="", flush=True)

                for content in update.contents:
                    if content.type == "text" and content.text:
                        print(content.text, end="", flush=True)

                if update.finish_reason:
                    print(f"\n[Finished: {update.finish_reason}]", end="", flush=True)
            print()


asyncio.run(main())
```

Highlights:

- `get_response(messages, stream=True)` returns a `ResponseStream` async iterator.
- `update.additional_properties["thread_id"]` arrives on the first update; cache it and pass it back via `metadata` to stay on the same server thread.
- `update.contents` is a list of `Content` objects — text comes through `content.type == "text"` items; function calls / approvals come through `content.type == "function_call"`, `"function_approval_request"`, etc.
- `update.finish_reason` is populated on the final update.

## Non-streaming response

```python
response = await client.get_response(
    [Message(role="user", contents=["What is 2 + 2?"])],
    metadata={"thread_id": thread_id} if thread_id else None,
)
print(response.text)                                # Concatenated text
thread_id = response.additional_properties.get("thread_id")
```

`response` is a `ChatResponse`; `response.text` joins every text `Content` in order. Inspect `response.messages[*].contents` for tool calls or richer payloads.

## Sending tool **definitions** (server-side execution)

When you don't wrap the client in `Agent`, you can still advertise tools to the server:

```python
from agent_framework import tool

@tool
def calculate(a: float, b: float, operation: str) -> str:
    ...

response = await client.get_response(
    [Message(role="user", contents=["What is 7 * 8?"])],
    tools=[calculate],                             # definitions only — server executes
    metadata={"thread_id": thread_id} if thread_id else None,
)

for message in response.messages:
    for content in message.contents:
        if content.type == "function_call":
            print(f"[Server invoked tool: {content.name}({content.arguments})]")
```

> Direct `AGUIChatClient` does **not** execute tools locally. The server must have matching implementations. If you need client-side execution, see [agent_integration.md](agent_integration.md).

## Multi-turn conversation

```python
# First turn
r1 = await client.get_response([Message(role="user", contents=["My name is Alice"])])
thread_id = r1.additional_properties.get("thread_id")

# Second turn — same thread
r2 = await client.get_response(
    [Message(role="user", contents=["What's my name?"])],
    options={"metadata": {"thread_id": thread_id}},
)
assert "alice" in r2.text.lower()
```

Thread state lives on the server. Two patterns:

| Pattern | When | Note |
| --- | --- | --- |
| **Server-managed thread** (this example) | Server keeps full history; client sends only the latest message + `thread_id` | Lighter network payload, but the server must persist history. |
| **Client-managed history** | Client sends the whole message log on every call | Works against any AG-UI server (stateful or stateless). Use `Agent` for this — see [agent_integration.md](agent_integration.md). |

## Updates vs. messages

Inside the stream loop, `update` is a `ChatResponseUpdate` (delta), not a full `ChatResponse`. Concatenate text incrementally; do not assume one update equals one logical message.

```python
buffer = ""
async for update in stream:
    for content in update.contents:
        if content.type == "text" and content.text:
            buffer += content.text
            print(content.text, end="", flush=True)
```

## Headers, timeouts, and custom HTTP options

```python
async with AGUIChatClient(
    endpoint="https://my-server.example.com/",
    headers={"X-API-Key": os.environ["AG_UI_API_KEY"]},
    timeout=60.0,                       # raise for long-running tools
) as client:
    ...
```

For long agents or expensive tool chains, bump `timeout` accordingly. A stalled `read` is the most common cause of "streaming not working".

## Error handling

```python
import httpx

try:
    async with AGUIChatClient(endpoint=server_url) as client:
        response = await client.get_response(messages)
except ConnectionError:
    # Server not reachable
    ...
except httpx.ReadTimeout:
    # Tool ran longer than `timeout`
    ...
except Exception:
    # Bubble up everything else — AGUIChatClient raises on protocol errors too
    raise
```

For interactive CLIs, catch `KeyboardInterrupt` cleanly around the input loop so the context manager has a chance to close the HTTP pool.
