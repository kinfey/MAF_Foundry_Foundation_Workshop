"""LAB 5 — Control Tower smoketest client.

Walks the AG-UI endpoint through all 7 AG-UI features:

  1. Agentic chat            — streaming SSE responses
  2. Backend tool rendering  — list_exceptions / quote_freight returns
  3. Human-in-the-loop       — fulfill_order over $1000 awaits approval
  4. Generative UI           — Markdown streamed from the agent
  5. Tool-based UI           — state_update tool_result payloads
  6. Shared state            — state_update state mutations
  7. Predictive state        — predict_state_config on fulfill_order

Run (after `uvicorn server:app --port 5100` is already up):
    conda activate agentdev
    python client_smoketest.py
"""

from __future__ import annotations

import asyncio
import os
from typing import Any

import httpx
from agent_framework import Agent, tool
from agent_framework.ag_ui import AGUIChatClient

SERVER_URL = os.environ.get("AGUI_SERVER_URL", "http://127.0.0.1:5100/")
API_KEY = os.environ.get("AG_UI_API_KEY", "zava-control-tower-demo-key")


@tool
def play_alert_sound(level: str = "info") -> str:
    """Play a desk-alert sound when the agent reports a high-severity exception.

    This is a CLIENT-side tool — the server emits a tool-call event and the
    smoketest prints it inline so the operator can verify the wiring.
    """
    bell = "🚨🚨🚨" if level == "high" else "🔔"
    print(f"\n  {bell} [client.play_alert_sound] level={level} — wall display buzzed.")
    return f"alert_played:{level}"


async def _converse(agent: Agent, session: Any, prompt: str, label: str) -> None:
    print(f"\n=== Turn {label} ===")
    print(f"Operator: {prompt}")
    print("Control Tower: ", end="", flush=True)
    async for chunk in agent.run(prompt, stream=True, session=session):
        text = getattr(chunk, "text", None)
        if text:
            print(text, end="", flush=True)
    print()


async def main() -> None:
    print(f"[smoketest] connecting to {SERVER_URL}")
    async with httpx.AsyncClient(headers={"X-API-Key": API_KEY}, timeout=120.0) as http_client:
        async with AGUIChatClient(endpoint=SERVER_URL, http_client=http_client) as remote:
            async with Agent(
                name="control-tower-smoke",
                instructions=(
                    "You are the operations manager driving the ZavaShop control tower. "
                    "Use the remote tools naturally and surface any approvals you need."
                ),
                client=remote,
                tools=[play_alert_sound],
            ) as agent:
                session = agent.create_session()

                # 1. Exceptions + client-side alert sound
                await _converse(
                    agent,
                    session,
                    "What exception orders are open right now? "
                    "If any are high severity please trigger play_alert_sound with level='high'.",
                    "1 — list_exceptions + play_alert_sound",
                )

                # 2. Freight quotes (server tool + tool-based UI + shared state)
                await _converse(
                    agent,
                    session,
                    "Get me freight quotes from US to EU for a 5 kg parcel. "
                    "Show carrier, price, and transit days.",
                    "2 — quote_freight",
                )

                # 3. Under-threshold fulfillment (completes straight through)
                await _converse(
                    agent,
                    session,
                    "Please fulfill ORD-20260524-001 — that one is well under the approval threshold.",
                    "3 — fulfill_order (auto)",
                )

                # 4. HITL: over-threshold fulfillment — first call awaits approval
                await _converse(
                    agent,
                    session,
                    "Now please fulfill ORD-20260524-002 for VIP_003 — that one was flagged.",
                    "4 — fulfill_order (HITL: awaiting approval)",
                )

                # 5. Operator approves; agent retries with supervisor_approval=True
                await _converse(
                    agent,
                    session,
                    "Approved by operations manager. Please re-run fulfill_order on "
                    "ORD-20260524-002 with supervisor_approval=True so dispatch can proceed.",
                    "5 — fulfill_order (HITL resolved)",
                )

    print("\n[smoketest] done.")


if __name__ == "__main__":
    asyncio.run(main())
