# Human-in-the-Loop

Workflows pause cleanly for external input via `ctx.request_info(...)`. The caller drives the resume loop by inspecting events and calling `run(responses={...})`.

## The lifecycle

1. An executor calls `await ctx.request_info(request_data=..., response_type=..., request_id="…")`.
2. The workflow becomes idle with `WorkflowRunState.IDLE_WITH_PENDING_REQUESTS` and emits a `request_info` event whose `data` is your payload and whose `request_id` is the resume key.
3. The application captures the event, prompts the user (or any external system), and collects answers.
4. The application calls `workflow.run(stream=True, responses={request_id: reply, ...})`.
5. The workflow resumes; the executor's `@response_handler` for the matching original request is invoked.

## Pattern — request → response_handler

```python
from dataclasses import dataclass
from agent_framework import (
    Agent, AgentExecutorRequest, AgentExecutorResponse,
    Executor, Message, WorkflowBuilder, WorkflowContext,
    handler, response_handler,
)
from typing_extensions import Never

@dataclass
class HumanFeedbackRequest:
    prompt: str


class TurnManager(Executor):
    @handler
    async def start(self, _: str, ctx: WorkflowContext[AgentExecutorRequest]) -> None:
        user = Message("user", contents=["Start by making your first guess."])
        await ctx.send_message(AgentExecutorRequest(messages=[user], should_respond=True))

    @handler
    async def on_agent_response(self, result: AgentExecutorResponse, ctx: WorkflowContext) -> None:
        guess = result.agent_response.value.guess          # Pydantic-validated structured output
        await ctx.request_info(
            request_data=HumanFeedbackRequest(prompt=f"The agent guessed: {guess}. (higher/lower/correct)"),
            response_type=str,
        )

    @response_handler
    async def on_human_feedback(
        self,
        original_request: HumanFeedbackRequest,
        feedback: str,
        ctx: WorkflowContext[AgentExecutorRequest, str],
    ) -> None:
        reply = feedback.strip().lower()
        if reply == "correct":
            await ctx.yield_output("Guessed correctly!")
            return
        await ctx.send_message(AgentExecutorRequest(
            messages=[Message("user", contents=[f"Feedback: {reply}. Adjust and try again."])],
            should_respond=True,
        ))
```

## Driving the loop

```python
async def process_event_stream(stream) -> dict[str, str] | None:
    requests: list[tuple[str, HumanFeedbackRequest]] = []
    async for event in stream:
        if event.type == "request_info" and isinstance(event.data, HumanFeedbackRequest):
            requests.append((event.request_id, event.data))
        elif event.type == "output":
            print(event.data)
    if not requests:
        return None
    return {rid: input(f"{req.prompt}\n> ") for rid, req in requests}


stream = workflow.run("start", stream=True)
pending = await process_event_stream(stream)
while pending is not None:
    stream = workflow.run(stream=True, responses=pending)
    pending = await process_event_stream(stream)
```

`responses` is a `dict[str, Any]` mapping `request_id` → reply. Each pending `request_id` must be answered before the workflow can resume.

## Approval requests on `@tool`

For function-tool approval, set `approval_mode="always_require"` on a `@tool`. The framework emits a `request_info` event whose `data` is a `FunctionApprovalRequestContent`:

```python
from typing import Annotated
from agent_framework import tool, Content

@tool(approval_mode="always_require")
async def send_email(
    to:      Annotated[str, "Recipient"],
    subject: Annotated[str, "Subject"],
    body:    Annotated[str, "Body"],
) -> str:
    ...
    return "Email sent."


events = await workflow.run(incoming_email)
while events.get_request_info_events():
    responses: dict[str, Content] = {}
    for evt in events.get_request_info_events():
        data = evt.data
        if not isinstance(data, Content) or data.type != "function_approval_request":
            raise ValueError(f"Unexpected request type: {type(data)}")
        # Auto-approve in this demo; show data.function_call.parse_arguments() to a human in production
        responses[evt.request_id] = data.to_function_approval_response(approved=True)
    events = await workflow.run(responses=responses)
```

The response payload type is `function_approval_response` Content — obtain it via `data.to_function_approval_response(approved=True | False)`.

## Declaration-only tools (client-side execution)

When a `@tool` has `func=None`, the workflow emits a request the caller answers with the tool's return value. Use this pattern when the actual call happens outside the agent runtime (e.g., a browser, a UI, or another service).

## Combining HITL with checkpoints

Use `FileCheckpointStorage` or `CosmosCheckpointStorage` so a HITL pause survives an entire process restart:

1. Run until the workflow emits `request_info`.
2. Exit the program (the checkpoint already persists `awaiting human response`).
3. Later, start a new process, list checkpoints, and resume with `workflow.run(checkpoint_id=..., responses={...})`.

Keep request payload dataclasses primitive (str/int/bool) so checkpoint serialization can rehydrate them.

## Tips

- Always use a typed `request_data` (a `@dataclass` or Pydantic model) — the caller depends on its shape.
- One `request_id` per logical question; let the framework pick it (default) unless you need a stable key.
- For agent-step caching during HITL, wrap expensive functions with `@step` (functional API) or save/restore executor state via `on_checkpoint_save` / `on_checkpoint_restore` (graph API).
- `result.get_request_info_events()` returns pending requests after a non-streaming run.
- Sub-workflow requests bubble up as `SubWorkflowRequestMessage` — intercept in the parent or let the outer caller receive them.
