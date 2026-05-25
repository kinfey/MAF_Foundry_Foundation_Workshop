# Getting Started — Executors, Edges, Agents, Streaming

The three `_start-here/` samples walk through the foundational graph API.

## Step 1 — Executors and edges

An executor is a graph node. It has a stable `id`, one or more `@handler` methods, and consumes/produces typed values through a `WorkflowContext[T_Out, T_W_Out]`:

- `T_Out` = the type of messages this node sends to downstream nodes via `ctx.send_message(...)`.
- `T_W_Out` = the type of workflow output this node yields via `ctx.yield_output(...)`.
- Use `Never` (from `typing_extensions`) when the node does not produce that channel.

Two equivalent shapes:

```python
from agent_framework import Executor, WorkflowBuilder, WorkflowContext, executor, handler
from typing_extensions import Never


class UpperCase(Executor):
    def __init__(self, id: str):
        super().__init__(id=id)

    @handler
    async def to_upper(self, text: str, ctx: WorkflowContext[str]) -> None:
        await ctx.send_message(text.upper())


@executor(id="reverse_text")
async def reverse_text(text: str, ctx: WorkflowContext[Never, str]) -> None:
    await ctx.yield_output(text[::-1])
```

### Explicit types on `@handler`

Type introspection is the default. If you need union inputs or you want to decouple runtime types from annotations, pass explicit parameters — this disables introspection (all-or-nothing):

```python
class ExclamationAdder(Executor):
    @handler(input=str, output=str)
    async def add(self, msg, ctx) -> None:           # type: ignore[no-untyped-def]
        await ctx.send_message(f"{msg}!!!")          # type: ignore[arg-type]


# All three: @handler(input=str | int)
#            @handler(input=str, output=int)
#            @handler(input=str, output=int, workflow_output=bool)
```

### Building and running

`WorkflowBuilder` is fluent:

```python
def create_workflow():
    upper = UpperCase(id="upper")
    exclaim = ExclamationAdder(id="exclaim")
    return (
        WorkflowBuilder(start_executor=upper)
        .add_edge(upper, exclaim)
        .add_edge(exclaim, reverse_text)
        .build()
    )

events = await create_workflow().run("hello world")
print(events.get_outputs())            # ['!!!DLROW OLLEH']
print(events.get_final_state())        # WorkflowRunState.IDLE
```

> **Workflows complete when idle** — when no node has more work and no `request_info` is pending. Multiple `yield_output` calls from different terminal nodes are all collected into `get_outputs()`.

## Step 2 — Agents in a workflow

Any `Agent` (and any `AgentExecutor`-wrapped agent) is a workflow node. The framework normalizes string / `Message` inputs to `AgentExecutorRequest` for you when the start node is an agent.

```python
import os
from typing import cast
from agent_framework import Agent, AgentResponse, WorkflowBuilder
from agent_framework.foundry import FoundryChatClient
from azure.identity import AzureCliCredential

client = FoundryChatClient(
    project_endpoint=os.environ["FOUNDRY_PROJECT_ENDPOINT"],
    model=os.environ["FOUNDRY_MODEL"],
    credential=AzureCliCredential(),
)
writer = Agent(client=client, instructions="You write content.", name="writer")
reviewer = Agent(client=client, instructions="You review content.", name="reviewer")

workflow = WorkflowBuilder(start_executor=writer).add_edge(writer, reviewer).build()
events = await workflow.run("Slogan for an affordable electric SUV.")

for out in cast(list[AgentResponse], events.get_outputs()):
    print(f"{out.messages[0].author_name}: {out.text}\n")
```

> Use `AgentExecutor(agent)` explicitly when you need control over options (sessions, response format, kwargs).

## Step 3 — Streaming

`workflow.run(..., stream=True)` returns an async iterator of `WorkflowEvent`. Group by author when the same agent emits multiple token updates:

```python
from agent_framework import AgentResponseUpdate, Message

last_author = None
async for event in workflow.run(
    Message("user", ["Slogan for an electric SUV."]),
    stream=True,
):
    if event.type == "output" and isinstance(event.data, AgentResponseUpdate):
        update = event.data
        if update.author_name != last_author:
            if last_author is not None:
                print()
            print(f"{update.author_name}: {update.text}", end="", flush=True)
            last_author = update.author_name
        else:
            print(update.text, end="", flush=True)
```

### Event types you will see

| `event.type` | Notes |
| --- | --- |
| `executor_invoked` | Node started; `event.executor_id` identifies it |
| `executor_completed` | Node finished |
| `output` | `ctx.yield_output(...)` value or `AgentResponseUpdate` token |
| `intermediate` | Yield from an executor selected via `intermediate_output_from` |
| `request_info` | HITL pause; `event.data` is your request payload, `event.request_id` is the resume key |
| `status` | `WorkflowRunState.IDLE`, `IDLE_WITH_PENDING_REQUESTS`, etc. |
| `superstep_completed` | Safe boundary for checkpoints |

## State isolation pattern

Always wrap construction in a helper so a long-lived process can run the same topology many times without leaking executor state:

```python
def create_workflow() -> Workflow:
    upper = UpperCase(id="upper")
    return WorkflowBuilder(start_executor=upper).add_edge(upper, reverse_text).build()

for prompt in prompts:
    events = await create_workflow().run(prompt)
```
