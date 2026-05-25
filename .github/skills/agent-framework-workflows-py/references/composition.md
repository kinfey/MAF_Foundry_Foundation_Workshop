# Composition — Sub-Workflows, `as_agent()`, Shared Sessions, State, kwargs

## Sub-workflows with `WorkflowExecutor`

Wrap any built `Workflow` as a single executor node. The parent can route messages to it and receive its outputs like any other handler:

```python
from agent_framework import (
    Executor, Workflow, WorkflowBuilder, WorkflowContext, WorkflowExecutor, handler,
)
from typing_extensions import Never


def build_child() -> Workflow:
    processor = TextProcessor()                # yields TextProcessingResult
    return WorkflowBuilder(start_executor=processor).build()


class Orchestrator(Executor):
    results: list = []
    expected: int = 0

    @handler
    async def start(self, texts: list[str], ctx: WorkflowContext) -> None:
        self.expected = len(texts)
        for i, text in enumerate(texts):
            await ctx.send_message(
                TextProcessingRequest(text=text, task_id=f"task_{i+1}"),
                target_id="child_workflow",
            )

    @handler
    async def collect(self, result, ctx: WorkflowContext[Never, list]) -> None:
        self.results.append(result)
        if len(self.results) == self.expected:
            await ctx.yield_output(self.results)


main_workflow = (
    WorkflowBuilder(start_executor=Orchestrator())
    .add_edge(Orchestrator(), WorkflowExecutor(build_child(), id="child_workflow"))
    .add_edge("child_workflow", Orchestrator())   # results flow back
    .build()
)
```

A child completes (and emits an output to the parent) when it becomes idle — i.e. when one of its terminals calls `ctx.yield_output(...)`.

## Intercepting sub-workflow requests

When a child calls `ctx.request_info(...)`, the parent receives a `SubWorkflowRequestMessage`. Handle it to resolve the request **inside the parent** instead of propagating it to the outermost caller:

```python
from agent_framework import (
    SubWorkflowRequestMessage, SubWorkflowResponseMessage,
    WorkflowContext, handler, response_handler,
)

class SmartEmailOrchestrator(Executor):
    @handler
    async def handle_domain_validation_request(
        self,
        request: SubWorkflowRequestMessage,
        ctx: WorkflowContext[SubWorkflowResponseMessage],
    ) -> None:
        # request.source_event.data is the original request payload from the child
        if not isinstance(request.source_event.data, str):
            raise TypeError("Expected domain string")
        domain = request.source_event.data
        is_valid = domain in self._approved_domains
        # Return SubWorkflowResponseMessage targeting the requesting child executor
        await ctx.send_message(request.create_response(is_valid), target_id=request.executor_id)
```

Variants: `composition/sub_workflow_parallel_requests.py` shows multiple specialized interceptors handling different request types from the same sub-workflow; `composition/sub_workflow_kwargs.py` passes user tokens/config from parent to child agents.

## `workflow.as_agent()` — reflection pattern

A workflow can present itself as a single `Agent`. Combined with a cyclic graph, this becomes the reflection pattern: a Worker generates, a Reviewer evaluates, and only approved outputs leave the workflow.

```python
from uuid import uuid4
from dataclasses import dataclass
from pydantic import BaseModel
from agent_framework import (
    AgentResponse, Executor, Message, SupportsChatGetResponse,
    WorkflowBuilder, WorkflowContext, handler,
)


@dataclass
class ReviewRequest:
    request_id: str
    user_messages: list[Message]
    agent_messages: list[Message]


@dataclass
class ReviewResponse:
    request_id: str
    feedback: str
    approved: bool


class Reviewer(Executor):
    def __init__(self, id: str, client: SupportsChatGetResponse):
        super().__init__(id=id)
        self._client = client

    @handler
    async def review(self, request: ReviewRequest, ctx: WorkflowContext[ReviewResponse]) -> None:
        class _R(BaseModel):
            feedback: str
            approved: bool

        messages = [
            Message("system", ["Approve only if relevant, accurate, clear, and complete."]),
            *request.user_messages,
            *request.agent_messages,
            Message("user", ["Please review the agent's responses."]),
        ]
        response = await self._client.get_response(messages=messages, options={"response_format": _R})
        parsed = _R.model_validate_json(response.messages[-1].text)
        await ctx.send_message(ReviewResponse(request.request_id, parsed.feedback, parsed.approved))


class Worker(Executor):
    def __init__(self, id: str, client: SupportsChatGetResponse):
        super().__init__(id=id)
        self._client = client
        self._pending: dict[str, tuple[ReviewRequest, list[Message]]] = {}

    @handler
    async def from_user(self, user_messages: list[Message], ctx: WorkflowContext[ReviewRequest]) -> None:
        messages = [Message("system", ["You are a helpful assistant."]), *user_messages]
        response = await self._client.get_response(messages=messages)
        request = ReviewRequest(str(uuid4()), user_messages, response.messages)
        self._pending[request.request_id] = (request, [*messages, *response.messages])
        await ctx.send_message(request)

    @handler
    async def from_reviewer(
        self,
        review: ReviewResponse,
        ctx: WorkflowContext[ReviewRequest, AgentResponse],
    ) -> None:
        request, messages = self._pending.pop(review.request_id)
        if review.approved:
            await ctx.yield_output(AgentResponse(messages=request.agent_messages))
            return
        messages.append(Message("system", [review.feedback, "Please incorporate the feedback."]))
        messages.extend(request.user_messages)
        response = await self._client.get_response(messages=messages)
        new_request = ReviewRequest(review.request_id, request.user_messages, response.messages)
        self._pending[new_request.request_id] = (new_request, [*messages, *response.messages])
        await ctx.send_message(new_request)


agent = (
    WorkflowBuilder(start_executor=worker)
    .add_edge(worker, reviewer)
    .add_edge(reviewer, worker)
    .build()
    .as_agent()
)
response = await agent.run("Write code for parallel reading 1M files and writing a sorted output.")
print(response.text)              # Approved Workflow Output only
```

Variants: `agents/workflow_as_agent_human_in_the_loop.py` adds HITL inside the wrapped workflow; `agents/workflow_as_agent_with_session.py` keeps conversation history across invocations via `AgentSession`; `agents/workflow_as_agent_kwargs.py` propagates kwargs to inner `@tool` tools.

## Shared sessions between agents

Multiple agents can share a session so they read each other's messages:

```python
from agent_framework import (
    Agent, AgentExecutor, AgentExecutorRequest, InMemoryHistoryProvider,
    WorkflowBuilder, executor,
)

writer = Agent(
    client=client, name="writer", instructions="…",
    context_providers=[InMemoryHistoryProvider()],
)
reviewer = Agent(
    client=client, name="reviewer", instructions="…",
    context_providers=[InMemoryHistoryProvider()],
)

shared = writer.create_session()
writer_exec   = AgentExecutor(writer,   session=shared)
reviewer_exec = AgentExecutor(reviewer, session=shared)


@executor(id="intercept")
async def intercept(_resp, ctx):
    # Send an empty request so the reviewer reads the shared thread instead of
    # the writer's response being injected as a second copy.
    await ctx.send_message(AgentExecutorRequest(messages=[]))


workflow = (
    WorkflowBuilder(start_executor=writer_exec)
    .add_chain([writer_exec, intercept, reviewer_exec])
    .build()
)
await workflow.run("Tagline for a budget-friendly eBike.", options={"store": False})

memory = shared.state.get(InMemoryHistoryProvider.DEFAULT_SOURCE_ID, {})
for m in memory.get("messages", []):
    print(f"{m.author_name or m.role}: {m.text}")
```

The `intercept` executor avoids duplicating the writer's reply when the reviewer reads the shared session. Not all agent types support shared sessions — usually only same-provider agents.

## Workflow state for large payloads

Persist big blobs once in `ctx.set_state(key, value)` and pass small typed pointers along edges:

```python
EMAIL_KEY = "email:"
CURRENT  = "current_email_id"

@executor(id="store_email")
async def store_email(text: str, ctx: WorkflowContext[AgentExecutorRequest]) -> None:
    email = Email(email_id=str(uuid4()), email_content=text)
    ctx.set_state(f"{EMAIL_KEY}{email.email_id}", email)
    ctx.set_state(CURRENT, email.email_id)
    await ctx.send_message(AgentExecutorRequest(
        messages=[Message("user", contents=[email.email_content])],
        should_respond=True,
    ))

@executor(id="submit_to_assistant")
async def submit_to_assistant(detection: DetectionResult, ctx: WorkflowContext[AgentExecutorRequest]) -> None:
    email: Email = ctx.get_state(f"{EMAIL_KEY}{detection.email_id}")
    await ctx.send_message(AgentExecutorRequest(
        messages=[Message("user", contents=[email.email_content])],
        should_respond=True,
    ))
```

State is per-run; it survives across handlers in the same run and is included in checkpoints.

## Kwargs to inner tools

`workflow.as_agent()` and `WorkflowBuilder` propagate kwargs through to inner agent `@tool` functions. Use `state-management/workflow_kwargs_global.py` for a single global context shared by every agent, or `workflow_kwargs_per_agent.py` to scope kwargs to specific agents.

## When to compose

| Pattern | Choose when |
| --- | --- |
| `WorkflowExecutor` sub-workflow | Reuse an existing workflow as a black-box step; intercept its `request_info` calls |
| `workflow.as_agent()` | Expose a workflow through an agent surface (e.g., to embed in another orchestration) |
| Shared sessions | Multiple agents need to see each other's messages without you forwarding them manually |
| `ctx.set_state` | Large or repeatedly-read payloads; avoid sending them on every edge |
| Kwargs propagation | Pass user tokens, configuration, or per-call context to tools deep inside the graph |
