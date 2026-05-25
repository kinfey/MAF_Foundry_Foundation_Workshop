# Functional API — `@workflow`, `@step`, streaming, HITL, agents

Write workflows as plain async Python — no graph, no executor classes, no edges. Use normal `if`/`else`, loops, and `asyncio.gather`. `@workflow` turns a function into a `FunctionalWorkflow` object with `.run()`, `.run(stream=True)`, and `.as_agent()`.

## Basic pipeline

```python
import asyncio
from agent_framework import workflow

async def fetch_data(url: str) -> dict:
    return {"url": url, "content": f"Data from {url}", "status": 200}

async def transform_data(data: dict) -> str:
    return f"[{data['status']}] {data['content']}"

@workflow
async def data_pipeline(url: str) -> str:
    raw = await fetch_data(url)
    summary = await transform_data(raw)
    is_valid = "[200]" in summary
    return f"[{'VALID' if is_valid else 'INVALID'}] {summary}"

result = await data_pipeline.run("https://example.com/api/data")
print(result.get_outputs()[0])        # returned value is auto-emitted as output
print(result.get_final_state())       # WorkflowRunState.IDLE
```

A plain async function returns a value; `@workflow` captures that return as a workflow output and adds `.run`, streaming, events, and `.as_agent()`.

## Streaming

```python
@workflow
async def data_pipeline(url: str) -> str:
    return f"[200] {await transform_data(await fetch_data(url))}"

stream = data_pipeline.run("https://example.com/api/data", stream=True)
async for event in stream:
    if event.type == "output":
        print("Output:", event.data)
result = await stream.get_final_response()      # finalize after iteration
```

## Parallelism via `asyncio.gather`

```python
@workflow
async def research_pipeline(topic: str) -> str:
    web, papers, news = await asyncio.gather(
        research_web(topic),
        research_papers(topic),
        research_news(topic),
    )
    return "Research Summary:\n" + "\n".join(f"  - {s}" for s in (web, papers, news))
```

No framework primitive needed for parallelism — this is native Python.

## `@step` — per-step checkpointing and observability

`@step` saves a function's return value. On HITL resume or checkpoint restore, completed steps return their saved result instead of re-executing. Cheap functions can skip `@step`; expensive ones (API calls, LLM calls) usually want it.

```python
from agent_framework import InMemoryCheckpointStorage, step, workflow

storage = InMemoryCheckpointStorage()

@step
async def fetch_data(url: str) -> dict:
    """Expensive — @step prevents re-execution on resume."""
    return {"url": url, "content": f"Data from {url}", "status": 200}

@step
async def transform_data(data: dict) -> str:
    return f"[{data['status']}] {data['content']}"

async def validate_result(summary: str) -> bool:    # cheap → no @step
    return "[200]" in summary

@workflow(checkpoint_storage=storage)
async def data_pipeline(url: str) -> str:
    raw = await fetch_data(url)
    summary = await transform_data(raw)
    is_valid = await validate_result(summary)
    return f"{summary} (valid={is_valid})"
```

`@step` functions emit `executor_invoked` / `executor_completed` events so you get observability for free. Plain functions can coexist alongside `@step` in the same workflow.

## Human-in-the-loop

Add a `ctx: RunContext` parameter only when you need HITL, state, or custom events. `ctx.request_info(...)` suspends the workflow; resume with `run(responses={request_id: value})`:

```python
from agent_framework import RunContext, WorkflowRunState, step, workflow

@step
async def write_draft(topic: str) -> str:
    return f"Draft about '{topic}': ..."

@step
async def revise_draft(draft: str, feedback: str) -> str:
    return f"Revised: {draft[:50]}... [Applied: {feedback}]"

@workflow
async def review_pipeline(topic: str, ctx: RunContext) -> str:
    draft = await write_draft(topic)

    feedback = await ctx.request_info(
        {"draft": draft, "instructions": "Please review this draft"},
        response_type=str,
        request_id="review_request",
    )

    return await revise_draft(draft, feedback)


# Phase 1 — runs until request_info pauses it
result1 = await review_pipeline.run("AI Safety")
assert result1.get_final_state() == WorkflowRunState.IDLE_WITH_PENDING_REQUESTS
print(result1.get_request_info_events()[0].request_id)   # 'review_request'

# Phase 2 — resume with the human's answer
result2 = await review_pipeline.run(responses={"review_request": "Add alignment detail"})
print(result2.get_outputs()[0])
```

If `write_draft` were not `@step`, the resume would re-execute it. With `@step`, it returns its saved result instantly.

## Calling agents inside `@workflow`

Agent calls work inline — no decorator needed. Wrap with `@step` if you want per-call caching across resume:

```python
from agent_framework import Agent, step, workflow
from agent_framework.foundry import FoundryChatClient
from azure.identity import AzureCliCredential

client = FoundryChatClient(credential=AzureCliCredential())
classifier = Agent(name="Classifier", instructions="Classify documents.", client=client)
writer = Agent(name="Writer", instructions="Summarize in one sentence.", client=client)
reviewer = Agent(name="Reviewer", instructions="Review the summary.", client=client)

@workflow
async def simple_pipeline(doc: str) -> str:
    classification = (await classifier.run(f"Classify: {doc}")).text
    summary = (await writer.run(f"Summarize: {doc}")).text
    review = (await reviewer.run(f"Review: {summary}")).text
    return f"Classification: {classification}\nSummary: {summary}\nReview: {review}"


# Cached variant
@step
async def classify(doc: str) -> str:
    return (await classifier.run(f"Classify: {doc}")).text

@step
async def summarize(doc: str) -> str:
    return (await writer.run(f"Summarize: {doc}")).text

@workflow
async def cached_pipeline(doc: str) -> str:
    return f"{await classify(doc)} / {await summarize(doc)}"
```

## When to choose functional vs. graph

| Functional (`@workflow`) | Graph (`WorkflowBuilder`) |
| --- | --- |
| Linear or lightly branched pipelines | Complex DAGs, loops, switch-case, fan-out/fan-in |
| Native Python control flow | Declarative edges, conditions, orchestration builders |
| Per-step caching with `@step` | Per-executor state via `on_checkpoint_save` / `on_checkpoint_restore` |
| Direct `result.get_outputs()` from `return` | Multiple terminal nodes via `ctx.yield_output` |
| HITL via `ctx: RunContext` (optional) | HITL via `Executor` + `@response_handler` |
