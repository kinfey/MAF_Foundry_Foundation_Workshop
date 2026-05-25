# Checkpointing — Pause, Resume, Durable Workflows

Checkpoints let a workflow survive process restarts and crash recovery. The runtime writes a checkpoint after each super-step, capturing pending messages, executor state, and request_info data.

## Plug a storage backend in

| Storage | Module / class | Use for |
| --- | --- | --- |
| In-memory | `InMemoryCheckpointStorage` | Tests, demos |
| File-based | `FileCheckpointStorage(storage_path=Path(...))` | Local dev, single-host recovery |
| Cosmos DB NoSQL | `CosmosCheckpointStorage(...)` | Durable production checkpoints in Azure Cosmos DB |

Graph API:

```python
from agent_framework import InMemoryCheckpointStorage, WorkflowBuilder

storage = InMemoryCheckpointStorage()
workflow = (
    WorkflowBuilder(start_executor=start, checkpoint_storage=storage)
    .add_edge(start, worker)
    .add_edge(worker, worker)             # self-loop for iterative processing
    .build()
)
```

Functional API:

```python
from agent_framework import InMemoryCheckpointStorage, workflow

storage = InMemoryCheckpointStorage()

@workflow(checkpoint_storage=storage)
async def pipeline(url: str) -> str:
    ...
```

## Per-executor state hooks

Subclass `Executor` and override the hooks to persist instance state across restarts:

```python
import sys
from dataclasses import dataclass
from typing import Any
from agent_framework import Executor, WorkflowContext, handler

if sys.version_info >= (3, 12):
    from typing import override
else:
    from typing_extensions import override


class WorkerExecutor(Executor):
    def __init__(self, id: str) -> None:
        super().__init__(id=id)
        self._composite_number_pairs: dict[int, list[tuple[int, int]]] = {}

    @handler
    async def compute(self, task, ctx: WorkflowContext) -> None:
        ...

    @override
    async def on_checkpoint_save(self) -> dict[str, Any]:
        return {"composite_number_pairs": self._composite_number_pairs}

    @override
    async def on_checkpoint_restore(self, state: dict[str, Any]) -> None:
        self._composite_number_pairs = state.get("composite_number_pairs", {})
```

Keep state JSON-friendly (primitive types, dataclasses, lists, dicts) so the storage layer can serialize it.

## Resume loop with crash simulation

```python
from random import random
from agent_framework import WorkflowCheckpoint

latest: WorkflowCheckpoint | None = None
while True:
    workflow = workflow_builder.build()                # fresh runtime, persistent state from storage

    event_stream = (
        workflow.run(message=10, stream=True)
        if latest is None
        else workflow.run(checkpoint_id=latest.checkpoint_id, stream=True)
    )

    output = None
    async for event in event_stream:
        if event.type == "output":
            output = event.data
            break
        if event.type == "superstep_completed" and random() < 0.5:
            # simulate a crash on a safe boundary
            break

    if output is not None:
        print("Done:", output)
        break

    latest = await storage.get_latest(workflow_name=workflow.name)
    if not latest:
        raise RuntimeError("No checkpoints to resume from.")
    print(f"Resuming from checkpoint {latest.checkpoint_id} (iter={latest.iteration_count})")
```

Only `superstep_completed` is a safe interrupt point — checkpoints exist between super-steps. Interrupting mid-super-step rewinds to the previous boundary.

## Checkpoint + HITL

Combining `FileCheckpointStorage` with `ctx.request_info(...)` produces a durable HITL pipeline. The pause state (`awaiting human response`) lives in the checkpoint until you resume.

```python
from pathlib import Path
from datetime import datetime
from agent_framework import FileCheckpointStorage

TEMP_DIR = Path(__file__).parent / "tmp" / "checkpoints_hitl"
TEMP_DIR.mkdir(parents=True, exist_ok=True)
storage = FileCheckpointStorage(storage_path=TEMP_DIR)

# Run until request_info is emitted, then optionally exit the process.
# Later, restart, pick a checkpoint, and supply the human's answer on resume.
checkpoints = await storage.list_checkpoints(workflow_name=workflow.name)
sorted_cps = sorted(checkpoints, key=lambda cp: datetime.fromisoformat(cp.timestamp))
chosen = sorted_cps[-1]

new_workflow = create_workflow(checkpoint_storage=storage)
stream = new_workflow.run(checkpoint_id=chosen.checkpoint_id, stream=True)
# … collect pending requests, prompt user, then:
await new_workflow.run(stream=True, responses={request_id: "approve"})
```

Use simple primitives in `request_data` dataclasses (`prompt: str`, `draft: str`, `iteration: int`) so the `pending_requests_from_checkpoint` helper can rebuild them on resume.

## Sub-workflow checkpoints

Wrap a child workflow with `WorkflowExecutor(child, id="...")` and give the parent a checkpoint storage. The sub-workflow's intermediate state is captured along with the parent's — resume restores both layers.

## Cosmos DB checkpoint storage

`CosmosCheckpointStorage` accepts a Cosmos DB account endpoint, database name, container name, and an `azure-identity` credential. It writes checkpoints as documents and is suitable for distributed workers — any process that can read the container can resume a workflow:

```python
from agent_framework import CosmosCheckpointStorage
from azure.identity import AzureCliCredential

storage = CosmosCheckpointStorage(
    endpoint="https://<account>.documents.azure.com:443/",
    database_name="workflows",
    container_name="checkpoints",
    credential=AzureCliCredential(),
)
```

See `checkpoint/cosmos_workflow_checkpointing.py` and `checkpoint/cosmos_workflow_checkpointing_foundry.py` for full examples that combine durable checkpoints with `FoundryChatClient`-backed agents.

## Tips

- `await storage.list_checkpoints(workflow_name=...)` returns all checkpoints for a workflow name; sort by `timestamp` to find the latest.
- `await storage.get_latest(workflow_name=...)` is a convenience for "give me the most recent."
- Checkpoint names track the workflow name; rebuilt workflows must reuse the same `start_executor` id graph or the checkpoint can't be applied. For Magentic specifically, the `participants` keys are the stable identifiers.
- The functional `@workflow` API caches results via `@step`; pair it with `checkpoint_storage=...` so a crash mid-pipeline resumes without re-running expensive steps.
- Keep per-executor state small. Large blobs belong in `ctx.set_state` (also persisted) or external storage referenced by id.
