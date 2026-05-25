# AG-UI Server — Hosting an Agent via FastAPI

`add_agent_framework_fastapi_endpoint(app, target, path, *, dependencies=None, ...)` registers a streaming HTTP route on a FastAPI app. `target` can be an `Agent`, a `Workflow`, an `AgentFrameworkAgent`, or an `AgentFrameworkWorkflow`.

## Minimal server

```python
import os
from agent_framework import Agent
from agent_framework.openai import OpenAIChatCompletionClient
from agent_framework.ag_ui import add_agent_framework_fastapi_endpoint
from fastapi import FastAPI

agent = Agent(
    name="AGUIAssistant",
    instructions="You are a helpful assistant.",
    client=OpenAIChatCompletionClient(
        azure_endpoint=os.environ["AZURE_OPENAI_ENDPOINT"],
        model=os.environ["AZURE_OPENAI_MODEL"],
        api_key=os.environ.get("AZURE_OPENAI_API_KEY"),
    ),
)

app = FastAPI(title="AG-UI Server")
add_agent_framework_fastapi_endpoint(app, agent, "/")

# uvicorn server:app --host 127.0.0.1 --port 5100
```

The path can be anything — mount multiple endpoints to host several agents in one app:

```python
add_agent_framework_fastapi_endpoint(app, support_agent, "/support")
add_agent_framework_fastapi_endpoint(app, sales_agent,   "/sales")
```

## Where configuration comes from

`OpenAIChatCompletionClient` reads environment variables when constructor args are omitted. Both styles work:

```python
# Explicit
client = OpenAIChatCompletionClient(
    azure_endpoint=os.environ["AZURE_OPENAI_ENDPOINT"],
    model=os.environ["AZURE_OPENAI_MODEL"],
    api_key=os.environ.get("AZURE_OPENAI_API_KEY"),
)

# Environment-only — same result if AZURE_OPENAI_* env vars are set
client = OpenAIChatCompletionClient()
```

If `AZURE_OPENAI_API_KEY` is unset, `DefaultAzureCredential` is used — run `az login` and grant the **Cognitive Services OpenAI Contributor** role on the resource.

## Server-side tools

A server-side `@tool` runs in the FastAPI process. Register it on the `Agent`:

```python
from agent_framework import tool

@tool(description="Get the time zone for a location.")
def get_time_zone(location: str) -> str:
    timezones = {
        "seattle":  "Pacific Time (UTC-8)",
        "new york": "Eastern Time (UTC-5)",
        "london":   "Greenwich Mean Time (UTC+0)",
    }
    return timezones.get(location.lower(), f"No timezone data for {location}")


agent = Agent(
    name="AGUIAssistant",
    instructions="Use get_weather for weather and get_time_zone for time zones.",
    client=OpenAIChatCompletionClient(...),
    tools=[get_time_zone],          # ONLY server tools — never include client-side ones
)
```

Do not list client tools here. The server LLM still hears about them — the client forwards their definitions on every request — but execution must stay on the client. Duplicating implementations leads to ambiguous routing.

## Authentication

The endpoint is unauthenticated by default. Pass a list of FastAPI dependencies to enforce auth before the handler runs:

```python
import os
from fastapi import Depends, FastAPI, HTTPException, Security
from fastapi.security import APIKeyHeader

API_KEY_HEADER = APIKeyHeader(name="X-API-Key", auto_error=False)
EXPECTED_KEY   = os.environ.get("AG_UI_API_KEY")


async def verify_api_key(api_key: str | None = Security(API_KEY_HEADER)) -> None:
    if not EXPECTED_KEY:
        # Dev mode — warn but allow. Refuse to start without a key in production.
        return
    if not api_key:
        raise HTTPException(status_code=401, detail="Missing API key. Provide X-API-Key header.")
    if api_key != EXPECTED_KEY:
        raise HTTPException(status_code=403, detail="Invalid API key.")


add_agent_framework_fastapi_endpoint(
    app, agent, "/",
    dependencies=[Depends(verify_api_key)],
)
```

`dependencies` accepts any FastAPI `Depends(...)`. Real-world options:

| Mechanism | How |
| --- | --- |
| OAuth 2.0 / OIDC | `fastapi.security.OAuth2PasswordBearer` + token validation |
| JWT | `python-jose` to verify signatures and claims |
| Microsoft Entra ID | `azure-identity` (`OnBehalfOfCredential`, `ClientSecretCredential`) |
| Per-IP rate limiting | `slowapi` or a custom dependency |
| Anything custom | Any callable that raises `HTTPException` on failure |

For production: store the secret in Key Vault, rotate regularly, and refuse to start the server if `EXPECTED_KEY` is unset.

## Running the server

```bash
# Direct
python server.py

# Or with uvicorn (recommended)
uvicorn server:app --host 127.0.0.1 --port 5100

# With debug-level logs
uvicorn server:app --host 127.0.0.1 --port 5100 --log-level debug
```

Inside `server.py`, gate the uvicorn invocation on `__main__` so the module remains importable:

```python
if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="127.0.0.1", port=5100, log_level="debug", access_log=True)
```

## What the endpoint emits

The handler streams Server-Sent Events whose `data:` payload encodes AG-UI events: `RUN_STARTED`, `MESSAGES_SNAPSHOT`, `TEXT_MESSAGE_CONTENT`, `TOOL_CALL_START` / `TOOL_CALL_END` / `TOOL_CALL_RESULT`, `STATE_SNAPSHOT` / `STATE_DELTA`, `CUSTOM`, `RUN_FINISHED`, etc. Event types are `UPPERCASE_WITH_UNDERSCORES`; field names are `camelCase`.

`AGUIChatClient` translates these into `ChatResponseUpdate` / `Message` / `Content` objects for you. Hand-rolled SSE clients must:

1. Set `Accept: text/event-stream`.
2. Parse lines beginning with `data:` as JSON.
3. Capture `threadId` from the first `RUN_STARTED` and echo it on follow-up requests.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| `Connection refused` from client | Server not running, or wrong `AGUI_SERVER_URL` |
| `401` / `403` from client | API key dependency missing / mismatched `AG_UI_API_KEY` |
| Streaming silently stops mid-response | HTTP timeout — raise `httpx.AsyncClient(timeout=60.0)` on the client side |
| Thread context lost across calls | Client not echoing `thread_id` in `metadata` |
| Same tool runs twice | Same `@tool` registered on both sides — keep them disjoint |
