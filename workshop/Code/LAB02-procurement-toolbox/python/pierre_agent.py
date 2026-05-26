"""LAB 2 — Pierre, the ZavaShop procurement agent.

Pierre answers procurement questions and submits purchase orders, but every
PO must clear two guardrails:

    1. An approval gate provided by `SkillsProvider(..., require_script_approval=True)`.
       The driver loop below auto-approves all script invocations; in a
       production-like setting you would surface this to a human approver.

    2. A contract-cap check enforced *inside* the skill script `submit_po`.
       Even when the outer loop approves the call, the script itself
       refuses to write a PO whose total exceeds the supplier's contract
       ceiling (e.g. `CT-2026-Q1-YIWU.max_single_po_usd == $100,000`).

The agent talks to a Foundry Toolbox over MCP. When no toolbox endpoint
is provisioned yet, we fall back to Microsoft Learn MCP so the shape of
the wiring is still correct — Pierre will still answer the supplier and
contract questions because we expose two read-only function tools
(`get_supplier`, `get_contract`) that read the canonical fixtures via
`workshop/data/zava_data.py`.
"""

from __future__ import annotations

import asyncio
import json
import os
import sys
from collections.abc import Callable
from pathlib import Path
from typing import Any

from agent_framework import (
    Agent,
    InlineSkill,
    MCPStreamableHTTPTool,
    SkillFrontmatter,
    SkillsProvider,
)
from agent_framework.foundry import FoundryChatClient
from azure.identity import get_bearer_token_provider
from azure.identity.aio import AzureCliCredential

# Make the shared data helpers importable -----------------------------------
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "data"))
from zava_data import find_contract, find_supplier  # noqa: E402


# ---------------------------------------------------------------------------
# Env loading (same walker pattern as LAB 1)
# ---------------------------------------------------------------------------

def load_env() -> None:
    env_path = Path(__file__).resolve().parents[1] / ".env"
    if not env_path.exists():
        return
    for raw_line in env_path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip().strip('"').strip("'")
        if key and key not in os.environ:
            os.environ[key] = value


# ---------------------------------------------------------------------------
# Local replacement for `agent_framework.foundry.make_toolbox_header_provider`
# (not exported in the installed prerelease).
# ---------------------------------------------------------------------------

def make_toolbox_header_provider(
    credential: AzureCliCredential,
) -> Callable[[dict[str, Any]], dict[str, str]]:
    """Return a per-request header builder that injects a fresh Foundry token."""
    get_token = get_bearer_token_provider(credential, "https://ai.azure.com/.default")

    def provide(_request_kwargs: dict[str, Any]) -> dict[str, str]:
        return {"Authorization": f"Bearer {get_token()}"}

    return provide


# ---------------------------------------------------------------------------
# Read-only function tools — these stand in for the "supplier OData/REST"
# and "contracts" connections that would be wired into the Foundry Toolbox.
# They are not gated by the approval policy because they only read data.
# ---------------------------------------------------------------------------

def get_supplier(supplier: str) -> str:
    """Look up a supplier record by id or name (case-insensitive)."""
    row = find_supplier(supplier)
    if row is None:
        return json.dumps({"error": f"Supplier '{supplier}' not found."})
    return json.dumps(row, ensure_ascii=False)


def get_contract(supplier: str) -> str:
    """Look up the active contract for a supplier (by supplier id or name)."""
    row = find_contract(supplier)
    if row is None:
        return json.dumps({"error": f"No active contract for supplier '{supplier}'."})
    return json.dumps(row, ensure_ascii=False)


# ---------------------------------------------------------------------------
# Approval-gated skill script: submit_po
# ---------------------------------------------------------------------------

po_skill = InlineSkill(
    frontmatter=SkillFrontmatter(
        name="procurement-actions",
        description=(
            "Submit purchase orders to ZavaShop suppliers. Every call is"
            " reviewed by the approval policy before it runs. Even after"
            " approval, the script enforces the supplier's contract cap"
            " (max_single_po_usd) and will refuse oversized POs."
        ),
    ),
    instructions=(
        "Use the `submit_po` script when the user explicitly asks to place"
        " or submit a purchase order. The script will validate the"
        " supplier's contract cap before booking the PO."
    ),
)


@po_skill.script
def submit_po(supplier: str, sku: str, qty: int, unit_price: float) -> str:
    """Submit a PO for `qty` units of `sku` to `supplier` at `unit_price` USD.

    Returns either `[OK] Submitted PO …` on success or `[REJECTED] …` when
    the total exceeds the supplier's contract ceiling.
    """
    supplier_row = find_supplier(supplier)
    if supplier_row is None:
        return f"[REJECTED] Supplier '{supplier}' is not on the approved-vendor list."

    contract_row = find_contract(supplier_row["supplier_id"])
    total = round(qty * unit_price, 2)

    if contract_row is None:
        return (
            f"[REJECTED] No active contract found for {supplier_row['supplier_id']}."
            " Negotiate a contract before submitting a PO."
        )

    cap = float(contract_row["max_single_po_usd"])
    if total > cap:
        return (
            f"[REJECTED] PO total ${total:,.0f} exceeds contract"
            f" {contract_row['contract_id']} ceiling ${int(cap):,}."
            " Suggest splitting the order across multiple POs or renegotiating the cap."
        )

    return (
        f"[OK] Submitted PO to {supplier_row['supplier_id']} for {sku}"
        f" x {qty} units @ ${unit_price:.2f} (total ${total:,.2f})."
        f" Booked against contract {contract_row['contract_id']}."
    )


# ---------------------------------------------------------------------------
# The 3-turn demo conversation (verbatim from the LAB README)
# ---------------------------------------------------------------------------

DEMO_PROMPTS: list[str] = [
    "What is the latest contract with SUP-001 (YiwuClay)? Quote me the"
    " negotiated unit price for SKU-3055.",
    "Good. Submit a PO to SUP-001 for SKU-3055 x 200 at the negotiated price.",
    "Add another one: same supplier, SKU-7421 x 5000 units at $25 each.",
]


PIERRE_INSTRUCTIONS = (
    "You are Pierre, the ZavaShop procurement agent based in Paris.\n"
    "Workflow:\n"
    "  - When a user asks about a supplier or a contract, call get_supplier"
    " or get_contract to fetch the canonical data — never guess prices or"
    " contract terms.\n"
    "  - When the user asks you to submit a purchase order, call the"
    " `submit_po` skill script. The approval policy may pause the call;"
    " that is expected.\n"
    "  - If `submit_po` returns a `[REJECTED] …` message, do NOT retry the"
    " same call. Instead, summarise the reason for the user and propose a"
    " compliant alternative (for example, splitting the PO so each part"
    " stays under the contract cap).\n"
    "Always quote the contract id, the unit price, and the resulting PO"
    " total in USD."
)


async def run_turn(agent: Agent, session: Any, turn_index: int, prompt: str) -> None:
    print(f"\n=== Turn {turn_index} ===")
    print(f"[user] {prompt}")

    result = await agent.run(prompt, session=session)

    # Approval loop: auto-approve every script invocation. The contract-cap
    # rejection happens *inside* `submit_po`, not here, which is what the
    # LAB's "two layers of guardrail" framing calls for.
    while result.user_input_requests:
        for request in result.user_input_requests:
            call = getattr(request, "function_call", None)
            call_name = getattr(call, "name", "<unknown>") if call else "<unknown>"
            call_args = getattr(call, "arguments", "") if call else ""
            print(f"[approval] auto-approving {call_name}({call_args})")
            approval_response = request.to_function_approval_response(approved=True)
            result = await agent.run(approval_response, session=session)

    print(f"[pierre] {result.text}")


async def main() -> None:
    load_env()

    endpoint = os.environ.get("FOUNDRY_PROJECT_ENDPOINT")
    model = os.environ.get("FOUNDRY_MODEL")
    if not endpoint or not model:
        raise SystemExit(
            "FOUNDRY_PROJECT_ENDPOINT and FOUNDRY_MODEL must be set"
            " (see workshop/.env)."
        )

    toolbox_endpoint = os.environ.get("FOUNDRY_TOOLBOX_ENDPOINT")
    if toolbox_endpoint:
        toolbox_name = "zavashop-procurement"
        toolbox_url = toolbox_endpoint
        print(f"[mcp] Using Foundry Toolbox endpoint: {toolbox_url}")
    else:
        toolbox_name = "learn-mcp"
        toolbox_url = "https://learn.microsoft.com/api/mcp"
        print(
            "[mcp] FOUNDRY_TOOLBOX_ENDPOINT is not set. Falling back to"
            " Microsoft Learn MCP so the wiring still runs end-to-end."
        )

    async with AzureCliCredential() as credential:
        mcp_kwargs: dict[str, Any] = {
            "name": toolbox_name,
            "url": toolbox_url,
            "load_prompts": False,
        }
        if toolbox_endpoint:
            mcp_kwargs["header_provider"] = make_toolbox_header_provider(credential)

        async with MCPStreamableHTTPTool(**mcp_kwargs) as toolbox_tool, Agent(
            client=FoundryChatClient(
                project_endpoint=endpoint,
                model=model,
                credential=credential,
            ),
            name="Pierre",
            instructions=PIERRE_INSTRUCTIONS,
            tools=[toolbox_tool, get_supplier, get_contract],
            context_providers=[
                SkillsProvider(po_skill, require_script_approval=True),
            ],
        ) as agent:
            session = agent.create_session()
            for index, prompt in enumerate(DEMO_PROMPTS, start=1):
                await run_turn(agent, session, index, prompt)


if __name__ == "__main__":
    asyncio.run(main())
