# Hosting `Workflow`s on AG-UI

`add_agent_framework_fastapi_endpoint` accepts a built `Workflow` (or an `AgentFrameworkWorkflow` wrapper) just like an `Agent`. Workflow events — `executor_invoked`, `executor_completed`, `output`, `intermediate`, `request_info`, `status` — are translated into AG-UI events (`RUN_STARTED`, `STEP_STARTED`, `ACTIVITY_*`, `TOOL_CALL_*`, `CUSTOM`, `RUN_FINISHED`) and streamed as SSE.

## Stateless workflow — direct hosting

```python
from agent_framework import WorkflowBuilder, WorkflowContext, executor
from agent_framework.ag_ui import add_agent_framework_fastapi_endpoint
from fastapi import FastAPI


@executor(id="start")
async def start(message: str, ctx: WorkflowContext) -> None:
    await ctx.yield_output(f"Workflow received: {message}")


workflow = WorkflowBuilder(start_executor=start).build()

app = FastAPI()
add_agent_framework_fastapi_endpoint(app, workflow, "/")
```

Every request runs against the same `Workflow` instance. This is fine when the workflow has no per-conversation state — for example, a pure pipeline of pure executors with no `ctx.request_info`, no per-thread accumulators, and no executor instance fields that mutate during a run.

## Stateful workflow — `workflow_factory`

When the workflow carries per-conversation state, you must build a fresh `Workflow` per AG-UI thread. Use `AgentFrameworkWorkflow(workflow_factory=...)`:

```python
from agent_framework import Workflow, WorkflowBuilder
from agent_framework.ag_ui import AgentFrameworkWorkflow, add_agent_framework_fastapi_endpoint
from fastapi import FastAPI


def build_workflow_for_thread(thread_id: str) -> Workflow:
    # Return a fresh, isolated Workflow for this thread.
    # Use thread_id only to bind external resources (storage prefix, telemetry tags).
    return (
        WorkflowBuilder(start_executor=start)
        .add_edge(start, worker)
        .add_edge(worker, finisher)
        .build()
    )


app = FastAPI()
thread_scoped = AgentFrameworkWorkflow(
    workflow_factory=build_workflow_for_thread,
    name="my_workflow",
)
add_agent_framework_fastapi_endpoint(app, thread_scoped, "/")
```

Use the factory when any of the following are true:

- An executor reads/writes instance fields between handlers.
- The workflow contains a `ctx.request_info(...)` interrupt that must wait for a reply on the same thread.
- The workflow accumulates per-conversation state (counters, buffers, partial results) on executor instances.
- The workflow has cycles whose iteration count is part of conversation state.

> Without the factory, two parallel AG-UI threads share one `Workflow` instance and will trip over each other's state.

## What gets emitted

Mapping (approximate, simplified):

| Agent Framework event | AG-UI event |
| --- | --- |
| `executor_invoked` | `STEP_STARTED` / `ACTIVITY_STARTED` |
| `executor_completed` | `STEP_FINISHED` / `ACTIVITY_FINISHED` |
| `output` (`ctx.yield_output`) | `TEXT_MESSAGE_CONTENT` / `MESSAGES_SNAPSHOT` |
| `intermediate` (selected by `intermediate_output_from`) | `CUSTOM` (or reasoning content if appropriate) |
| `request_info` (`ctx.request_info`) | `RUN_FINISHED` with `interrupt` payload + `availableInterrupts` |
| `status` | Internal — used to gate completion |
| `superstep_completed` | Internal — used as a checkpoint boundary |

For HITL: the `RUN_FINISHED.interrupt` payload includes the original `request_data`; the client resumes by re-issuing the request with a `resume` block referencing the same `thread_id`.

## Combining workflow + AG-UI features

Because the host is just an HTTP endpoint that streams the underlying workflow's events, all the standard workflow patterns apply unchanged:

- **HITL** — emit `ctx.request_info(...)` from an executor; the client gets `availableInterrupts`, supplies `resume` on its next call.
- **Sub-workflows** — `WorkflowExecutor(child, id="...")` works the same; child events bubble up through the bridge.
- **Checkpoints** — pair `WorkflowBuilder(checkpoint_storage=...)` with a `workflow_factory` that loads the latest checkpoint for the thread id.
- **Output selection** — `output_from` / `intermediate_output_from` decide which yields become text messages vs. reasoning content on the wire.
- **Orchestration builders** — `SequentialBuilder`, `ConcurrentBuilder`, `HandoffBuilder`, `GroupChatBuilder`, `MagenticBuilder` all produce a `Workflow` and can be mounted directly.

## A factory that loads checkpoints

```python
from agent_framework import FileCheckpointStorage, WorkflowBuilder, Workflow

storage = FileCheckpointStorage(storage_path=Path("./checkpoints"))

def build_workflow_for_thread(thread_id: str) -> Workflow:
    workflow = (
        WorkflowBuilder(start_executor=start_executor, checkpoint_storage=storage, name=f"thread-{thread_id}")
        .add_edge(start_executor, worker)
        .build()
    )
    return workflow

# Caller drives resume by passing `resume` metadata; the bridge maps it to
# workflow.run(checkpoint_id=..., responses={...}).
```

The bridge handles the protocol-side; your factory returns the right `Workflow`, and the `responses` payload carried in the AG-UI `resume` block lands in `workflow.run(responses=...)`.

## Mounting an `Agent` and a `Workflow` together

```python
add_agent_framework_fastapi_endpoint(app, chat_agent,        "/chat")
add_agent_framework_fastapi_endpoint(app, planner_workflow,  "/planner")
add_agent_framework_fastapi_endpoint(app, thread_scoped,     "/research")
```

Each path is independent. A single FastAPI process can expose many agents and workflows, each behind its own auth dependency if needed.
