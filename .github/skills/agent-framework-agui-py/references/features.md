# The Seven AG-UI Features

The integration ships first-class support for the seven canonical AG-UI features. Each is a small composition on top of `Agent` / `@tool` / `AgentFrameworkAgent`.

| # | Feature | Pattern |
| --- | --- | --- |
| 1 | Agentic chat | Bare `Agent` |
| 2 | Backend tool rendering | Server-side `@tool` returning `state_update(...)` |
| 3 | Human-in-the-loop | `@tool(approval_mode="always_require")` |
| 4 | Agentic generative UI | Long-running async `@tool` that streams progress |
| 5 | Tool-based generative UI | `state_update(tool_result={"component": "..."})` |
| 6 | Shared state | `AgentFrameworkAgent(state_schema=..., predict_state_config=...)` |
| 7 | Predictive state updates | `predict_state_config={"field": {"tool": "...", "tool_argument": "..."}}` |

The `agent-framework-ag-ui-examples` package contains complete factory functions for each feature — `simple_agent`, `weather_agent`, `human_in_the_loop_agent`, `task_steps_agent_wrapped`, `ui_generator_agent`, `recipe_agent`, `document_writer_agent`, `research_assistant_agent`, `task_planner_agent`, `subgraphs_agent`.

```bash
pip install agent-framework-ag-ui-examples
python -m agent_framework_ag_ui_examples
```

Mounts seven endpoints (`/agentic_chat`, `/backend_tool_rendering`, `/human_in_the_loop`, `/agentic_generative_ui`, `/tool_based_generative_ui`, `/shared_state`, `/predictive_state_updates`, `/subgraphs`).

---

## Feature 1 — Agentic chat

The default: just stream chat responses.

```python
from agent_framework import Agent
from agent_framework.openai import OpenAIChatCompletionClient
from agent_framework.ag_ui import add_agent_framework_fastapi_endpoint

agent = Agent(
    name="simple",
    instructions="You are a helpful assistant.",
    client=OpenAIChatCompletionClient(model="gpt-4o"),
)
add_agent_framework_fastapi_endpoint(app, agent, "/agentic_chat")
```

---

## Feature 2 — Backend tool rendering

A server-side `@tool` executes in-process; the integration emits `ToolCallStartEvent`, `ToolCallEndEvent`, and `ToolCallResultEvent` so the UI can render progress and a final card.

```python
from agent_framework import Content, tool
from agent_framework.ag_ui import state_update

@tool
async def get_weather(city: str) -> Content:
    data = await fetch_weather(city)
    return state_update(
        text=f"{city}: {data['temp']}°C and {data['conditions']}",
        tool_result={
            "component": "weather-card",
            "city": city,
            "temperature": data["temp"],
            "conditions": data["conditions"],
        },
    )
```

`state_update(text=..., tool_result=..., state=None)` splits the payload by audience:

- `text` → the LLM tool result (what the model reads to plan the next turn).
- `tool_result` → the AG-UI `ToolCallResultEvent.content` (what the UI renders).
- `state` → merged into shared AG-UI state (Feature 6).

Always set `text` — it's what the LLM reasons on. `tool_result` and `state` are optional.

---

## Feature 3 — Human-in-the-loop

Mark a tool `approval_mode="always_require"`. The integration intercepts the call, emits an approval request to the client, waits for a decision, and only then executes (or rejects):

```python
from agent_framework import tool

@tool(approval_mode="always_require")
def cancel_subscription(user_id: str) -> str:
    """Cancel the user's subscription. Requires confirmation."""
    return cancel_in_billing_system(user_id)
```

On the wire, this surfaces as `FunctionApprovalRequest` content with the proposed call + arguments. The client renders a confirmation UI and replies with an approval response. If the user denies, the model receives a "denied" tool error and adapts.

For a multi-step custom flow (e.g. "approve, then run, then re-confirm"), wrap the agent with `AgentFrameworkAgent(orchestrators=[MyCustomOrchestrator(), DefaultOrchestrator()])` — see the bottom of this file.

---

## Feature 4 — Agentic generative UI

Long-running async tools that stream progress. Make the tool `async` and yield interim `Content` updates; the integration converts them into UI progress events.

```python
@tool
async def run_long_task(steps: int) -> str:
    """Run a multi-step task with progress."""
    for i in range(steps):
        await asyncio.sleep(1.0)
        # Implementation streams progress messages via the agent's event channel.
    return "Done."
```

The `task_steps_agent_wrapped(client)` factory in the examples package shows a full step-execution UI driven by this pattern.

---

## Feature 5 — Tool-based generative UI

Have the tool return a `tool_result` describing a UI component. The front-end renders the component instead of (or alongside) the tool's text result.

```python
@tool
def draw_chart(series: list[float], title: str) -> Content:
    return state_update(
        text=f"Charted {len(series)} points titled '{title}'.",
        tool_result={
            "component": "line-chart",
            "title": title,
            "series": series,
        },
    )
```

Convention: the `component` key names a front-end registered component (e.g. `weather-card`, `recipe-form`, `line-chart`). Everything else in `tool_result` is component props.

---

## Feature 6 — Shared state

`AgentFrameworkAgent` wraps a base `Agent` and adds bidirectional shared state, declared up front via `state_schema`. The client sees `StateSnapshotEvent` on connect, then `StateDeltaEvent` as the server updates fields:

```python
from agent_framework import Agent
from agent_framework.openai import OpenAIChatCompletionClient
from agent_framework.ag_ui import AgentFrameworkAgent, add_agent_framework_fastapi_endpoint

agent = Agent(
    name="recipe_agent",
    client=OpenAIChatCompletionClient(model="gpt-4o"),
)

state_schema = {
    "recipe": {
        "type": "object",
        "properties": {
            "name":        {"type": "string"},
            "ingredients": {"type": "array"},
        },
    }
}

# Map a tool argument → state field (Feature 7).
predict_state_config = {
    "recipe": {"tool": "update_recipe", "tool_argument": "recipe_data"},
}

wrapped = AgentFrameworkAgent(
    agent=agent,
    state_schema=state_schema,
    predict_state_config=predict_state_config,
)
add_agent_framework_fastapi_endpoint(app, wrapped, "/shared_state")
```

State changes can also be driven from tools via `state_update(state={...})`:

```python
@tool
def set_recipe(name: str, ingredients: list[str]) -> Content:
    return state_update(
        text=f"Saved recipe '{name}'.",
        state={"recipe": {"name": name, "ingredients": ingredients}},
    )
```

The client sends state updates via the AG-UI `RUN_AGENT_INPUT.state` field; the server sees them as system context.

---

## Feature 7 — Predictive state updates

Stream a tool's **arguments** as optimistic state updates **while** the model is still emitting them. This lets the UI react before the tool actually executes.

```python
predict_state_config = {
    "current_title":   {"tool": "write_document", "tool_argument": "title"},
    "current_content": {"tool": "write_document", "tool_argument": "content"},
}

wrapped = AgentFrameworkAgent(
    agent=agent,
    state_schema={
        "current_title":   {"type": "string"},
        "current_content": {"type": "string"},
    },
    predict_state_config=predict_state_config,
    require_confirmation=True,           # User can approve/reject the proposed state
)
```

Match `tool_argument` exactly to the `@tool` parameter name. As the model streams `write_document(title=..., content=...)`, the integration emits `StateDeltaEvent`s mapping each argument fragment to its target state field.

`require_confirmation=True` adds a user-approval step: the proposed state is shown to the user, who can accept (the tool runs) or reject (the change is discarded).

---

## Custom orchestrators (advanced)

Add execution flows beyond the defaults by implementing an `Orchestrator`:

```python
from agent_framework.ag_ui._orchestrators import Orchestrator, ExecutionContext

class MyCustomOrchestrator(Orchestrator):
    def can_handle(self, context: ExecutionContext) -> bool:
        return context.input_data.get("custom_mode") is True

    async def run(self, context: ExecutionContext):
        yield RunStartedEvent(...)
        # ... custom flow ...
        yield RunFinishedEvent(...)

wrapped = AgentFrameworkAgent(
    agent=your_agent,
    orchestrators=[MyCustomOrchestrator(), DefaultOrchestrator()],
)
```

Orchestrators are tried in order; the first whose `can_handle(context)` returns `True` runs. `DefaultOrchestrator` is the fallback — keep it last unless you fully replace it.

---

## Choosing the right feature

| Need | Use |
| --- | --- |
| Just stream chat | Feature 1 |
| Tool runs on server, plain text result | Plain `@tool` |
| Tool result needs a custom UI card | Feature 5 — `state_update(tool_result={...})` |
| Long-running tool with progress | Feature 4 — async `@tool` streaming `Content` |
| Persistent app state visible to both sides | Feature 6 — `AgentFrameworkAgent(state_schema=...)` |
| Stream tool args into state before execution | Feature 7 — `predict_state_config` |
| Require user approval before a destructive call | Feature 3 — `@tool(approval_mode="always_require")` |
| Multi-step custom flow | Custom `Orchestrator` |
