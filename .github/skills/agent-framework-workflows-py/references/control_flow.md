# Control Flow — Conditions, Switch-Case, Loops, Outputs

## Edge conditions

Attach a predicate to `add_edge(src, dst, condition=lambda msg: ...)` to gate routing. The predicate receives whatever the upstream node sent.

```python
from typing import Any
from pydantic import BaseModel
from agent_framework import AgentExecutorResponse

class Detection(BaseModel):
    is_spam: bool
    reason: str
    email_content: str

def is_spam(expected: bool):
    def cond(msg: Any) -> bool:
        if not isinstance(msg, AgentExecutorResponse):
            return True            # let unrelated payloads pass to avoid dead ends
        try:
            return Detection.model_validate_json(msg.agent_response.text).is_spam == expected
        except Exception:
            return False           # fail closed on parse errors
    return cond

workflow = (
    WorkflowBuilder(start_executor=detector)
    .add_edge(detector, to_email_writer, condition=is_spam(False))
    .add_edge(to_email_writer, writer)
    .add_edge(writer, send_reply)
    .add_edge(detector, handle_spam,    condition=is_spam(True))
    .build()
)
```

Validate agent JSON with Pydantic — never parse free text for routing decisions.

## Switch-case edge group

When you need exactly one of N branches, use `add_switch_case_edge_group`. Cases evaluate in order; `Default` fires if nothing matches.

```python
from agent_framework import Case, Default

workflow = (
    WorkflowBuilder(start_executor=store_email)
    .add_edge(store_email, detector)
    .add_edge(detector, to_detection_result)
    .add_switch_case_edge_group(
        to_detection_result,
        [
            Case(condition=get_case("NotSpam"), target=submit_to_email_assistant),
            Case(condition=get_case("Spam"),    target=handle_spam),
            Default(target=handle_uncertain),
        ],
    )
    .add_edge(submit_to_email_assistant, email_assistant_agent)
    .add_edge(email_assistant_agent, finalize_and_send)
    .build()
)
```

Tip: persist the bulk payload in `ctx.set_state(...)` and forward only a small typed pointer (`@dataclass DetectionResult(decision, email_id, reason)`) along the edges.

## Multi-selection edge group

`add_multi_selection_edge_group(src, targets, selection_func=...)` lets you dynamically choose any **subset** of targets at runtime — useful for partial fan-out where the set of recipients depends on the message.

## Loops

Graphs support cycles. Build a feedback loop by adding an edge back to a previous node. Use an `Enum` signal type on the loop edge for legibility:

```python
from enum import Enum

class NumberSignal(Enum):
    ABOVE = "above"
    BELOW = "below"
    MATCHED = "matched"
    INIT = "init"

class GuessNumberExecutor(Executor):
    def __init__(self, bound: tuple[int, int], id: str):
        super().__init__(id=id)
        self._lower, self._upper = bound

    @handler
    async def guess(self, feedback: NumberSignal, ctx: WorkflowContext[int, str]) -> None:
        if feedback == NumberSignal.INIT:
            self._guess = (self._lower + self._upper) // 2
            await ctx.send_message(self._guess)
        elif feedback == NumberSignal.MATCHED:
            await ctx.yield_output(f"Guessed: {self._guess}")
        elif feedback == NumberSignal.ABOVE:
            self._lower = self._guess + 1
            self._guess = (self._lower + self._upper) // 2
            await ctx.send_message(self._guess)
        else:                      # BELOW
            self._upper = self._guess - 1
            self._guess = (self._lower + self._upper) // 2
            await ctx.send_message(self._guess)

workflow = (
    WorkflowBuilder(start_executor=guess)
    .add_edge(guess, submit_to_judge)
    .add_edge(submit_to_judge, judge_agent)
    .add_edge(judge_agent, parse_judge)
    .add_edge(parse_judge, guess)               # loop edge
    .build()
)
```

Pass `WorkflowBuilder(max_iterations=N, ...)` to cap super-steps and prevent runaway loops.

## Intermediate vs. terminal outputs

Mark which executors emit Workflow Output (`type='output'`) vs. Intermediate Output (`type='intermediate'`):

```python
workflow = (
    WorkflowBuilder(
        start_executor=planner,
        output_from=[answerer],
        intermediate_output_from=[planner, researcher],
    )
    .add_edge(planner, researcher)
    .add_edge(researcher, answerer)
    .build()
)

async for event in workflow.run(initial, stream=True):
    if event.type == "intermediate":
        print(f"[intermediate] {event.executor_id}: {event.data}")
    elif event.type == "output":
        print(f"[output]       {event.executor_id}: {event.data}")
```

- `output_from` / `intermediate_output_from` are **allow-lists**; unselected yields are hidden from caller-facing streams and `result.get_outputs()`.
- `output_from="all"` is valid; `intermediate_output_from="all_other"` is valid; the inverse (`output_from="all_other"`, `intermediate_output_from="all"`) is rejected.
- The same executor cannot appear in both lists; duplicates / unknown ids / both lists empty are all rejected at build time.
- When the workflow is wrapped via `workflow.as_agent()`, Workflow Output becomes `AgentResponse.text` and Intermediate Output becomes `text_reasoning` content — `.text` keeps returning only the final answer.

## Workflow cancellation

Wrap `workflow.run(...)` in an `asyncio.Task` and cancel it; the framework propagates cancellation to in-flight handlers cleanly. See `control-flow/workflow_cancellation.py` for the full pattern.
