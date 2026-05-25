# Orchestration Builders

Pre-built multi-agent patterns from `agent_framework.orchestrations`. Each builder produces a regular `Workflow`, so all workflow features (streaming, checkpoints, `request_info`, `output_from`, `as_agent()`) apply.

```python
from agent_framework.orchestrations import (
    SequentialBuilder,
    ConcurrentBuilder,
    HandoffBuilder,
    GroupChatBuilder,
    MagenticBuilder,
)
```

> Orchestration samples create agents locally with `FoundryChatClient` — they require `FOUNDRY_PROJECT_ENDPOINT` and `FOUNDRY_MODEL`.

## Sequential

Chain agents with a shared conversation context. Each agent appends its assistant message to the conversation; the last participant emits Workflow Output by default.

```python
from agent_framework.orchestrations import SequentialBuilder

workflow = SequentialBuilder(
    participants=[writer, reviewer],
    output_from="all",                  # also emit writer's response, not just reviewer's
).build()

result = await workflow.run("Write a tagline for an affordable eBike.")
conversation = [Message(role="user", contents=["Write a tagline for an affordable eBike."])]
for output in result.get_outputs():
    conversation.extend(output.messages)
```

Sequential adds small adapter nodes internally — `input-conversation`, `to-conversation:<participant>`, `complete`. They appear in `executor_invoked` / `executor_completed` streams; ignore them unless you're debugging plumbing.

## Concurrent

Fan-out the same user prompt to every participant in parallel; default aggregator joins their responses into a `list[Message]`.

```python
from agent_framework.orchestrations import ConcurrentBuilder

workflow = ConcurrentBuilder(participants=[researcher, marketer, legal]).build()
events = await workflow.run("We are launching a new budget-friendly electric bike.")

for output in events.get_outputs():
    for i, msg in enumerate(output, start=1):
        name = msg.author_name or "user"
        print(f"{i:02d} [{name}]:\n{msg.text}")
```

Variants: `concurrent_custom_aggregator.py` overrides the aggregator with an LLM call; `concurrent_custom_agent_executors.py` lets child executors own their own agents; `concurrent_request_info.py` mid-orchestration review via `.with_request_info()`.

## Handoff

Mesh topology — agents transfer control to one another via auto-injected handoff tools. A triage / start agent receives initial input and routes to specialists; `HandoffAgentUserRequest` carries user-facing prompts back to the caller.

```python
from agent_framework.orchestrations import HandoffBuilder, HandoffAgentUserRequest

# Every participant MUST set require_per_service_call_history_persistence=True
triage = Agent(client=client, instructions="…", name="triage_agent",
               require_per_service_call_history_persistence=True)

workflow = (
    HandoffBuilder(
        name="customer_support",
        participants=[triage, refund_agent, order_agent, return_agent],
        termination_condition=lambda convo: len(convo) > 0 and "welcome" in convo[-1].text.lower(),
    )
    .with_start_agent(triage)
    .build()
)
```

Drive the request/response loop:

```python
workflow_result = workflow.run(initial_message, stream=True)
pending = [e async for e in workflow_result if e.type == "request_info"]
while pending:
    responses = {
        req.request_id: HandoffAgentUserRequest.create_response(next_user_input)
        for req in pending
    }
    events = await workflow.run(responses=responses)
    pending = [e for e in events if e.type == "request_info"]
```

Use `HandoffAgentUserRequest.terminate()` to end the loop programmatically. Configure specialist-to-specialist routes with `.add_handoff(source, [targets])`. For autonomous chains where specialists iterate without returning to the user, use `.with_autonomous_mode()`.

## GroupChat

A manager (agent or function) selects the next speaker each round.

```python
from agent_framework.orchestrations import GroupChatBuilder

workflow = (
    GroupChatBuilder(participants=[philosopher, scientist, skeptic])
    .with_orchestrator(agent=manager_agent)        # or selector=callable
    .build()
)
```

Variants: `group_chat_simple_selector.py` (function selector), `group_chat_philosophical_debate.py` (long-form multi-round debate), `group_chat_request_info.py` (periodic guidance via `.with_request_info()`).

## Magentic

A planner/manager coordinates worker agents through plan generation, progress ledgers, and re-planning. Built-in events: `magentic_orchestrator` (plan / progress ledger), `group_chat` (`GroupChatRequestSentEvent`).

```python
from agent_framework.orchestrations import MagenticBuilder, MagenticProgressLedger, GroupChatRequestSentEvent

workflow = MagenticBuilder(
    participants=[researcher_agent, coder_agent],
    intermediate_output_from=[researcher_agent, coder_agent],   # see their work in the stream
    manager_agent=manager_agent,
    max_round_count=10,
    max_stall_count=3,
    max_reset_count=2,
).build()

async for event in workflow.run(task, stream=True):
    if event.type == "magentic_orchestrator":
        if isinstance(event.data.content, Message):
            print("Plan:\n", event.data.content.text)
        elif isinstance(event.data.content, MagenticProgressLedger):
            print("Ledger:", event.data.content.to_dict())
    elif event.type == "group_chat" and isinstance(event.data, GroupChatRequestSentEvent):
        print(f"[REQUEST → {event.data.participant_name}]")
    elif event.type == "output":
        print("Final:", event.data)
```

For human plan review use `.with_plan_review()` — the workflow pauses with a typed plan-review request the caller answers. For durability, pass `checkpoint_storage=...` and treat `participants` keys as stable identifiers (mandatory for resume).

## Builder output selection

Orchestration builders use **participant-oriented** allow-lists. Defaults vary:

| Builder | Default Workflow Output |
| --- | --- |
| Sequential | Last participant |
| Concurrent | Synthetic aggregator |
| Handoff | Participants |
| GroupChat | Synthetic orchestrator |
| Magentic | Synthetic manager |

Override with `output_from=[participant_a, ...]` and `intermediate_output_from=[...]` exactly like with the graph API. Allowed magic values: `output_from="all"`, `intermediate_output_from="all_other"`. Rejected: `output_from="all_other"`, `intermediate_output_from="all"`.

## Workflow as agent

Every builder's output can be wrapped as an `Agent`:

```python
agent = workflow.as_agent("sequential_team")
response = await agent.run("Write a tagline.")
print(response.text)                      # Workflow Output as agent text
# Intermediate Output rides along as text_reasoning content on response.messages
```

Use this to drop a whole orchestration into another workflow as a single node, or to expose it through any agent-friendly surface.

## Tips

- `pip install agent-framework` already includes orchestrations; or pin `agent-framework-orchestrations`.
- Sequential's adapter nodes (`input-conversation`, `to-conversation:<name>`, `complete`) are intentional — filter them from logs if needed.
- Handoff requires `require_per_service_call_history_persistence=True` on every participant; `.build()` raises `ValueError` otherwise. This prevents history mismatches when handoff middleware short-circuits tool calls.
- Magentic resume from checkpoint requires the rebuilt workflow to reuse the same `participants` keys.
- For tool approval in builder workflows, see the `tool-approval/` samples (`sequential_builder_tool_approval.py`, `concurrent_builder_tool_approval.py`, `group_chat_builder_tool_approval.py`).
