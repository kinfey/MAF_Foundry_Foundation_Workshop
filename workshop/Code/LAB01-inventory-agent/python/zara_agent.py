from __future__ import annotations

import asyncio
import os
import sys
from pathlib import Path

from agent_framework import Agent, MCPStreamableHTTPTool
from agent_framework.foundry import FoundryChatClient
from azure.identity.aio import AzureCliCredential

# Make the shared workshop fixtures importable for this LAB script.
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "data"))
from zava_data import find_po, find_stock, load_purchase_orders


def load_env() -> None:
    """Load workshop/.env into process environment when available."""
    env_path = Path(__file__).resolve().parents[1] / ".env"
    if not env_path.exists():
        return

    for line in env_path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue

        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip().strip('"').strip("'")
        if key and key not in os.environ:
            os.environ[key] = value


def get_stock(sku: str, warehouse: str = "SEA-01") -> str:
    """Get current on-hand stock of a SKU in a warehouse."""
    row = find_stock(sku, warehouse)
    if row is None:
        return f"{sku} is not tracked at warehouse {warehouse}."
    return (
        f"{sku} @ {warehouse}: on_hand={row['on_hand']}, "
        f"reserved={row['reserved']}, reorder_point={row['reorder_point']}"
    )


async def get_po_status(po_number: str) -> dict:
    """Query the status of an inbound Purchase Order."""
    po = find_po(po_number)
    if po is None:
        return {"po_number": po_number, "status": "unknown"}
    return po


def find_open_pos_by_sku(sku: str, warehouse: str | None = None) -> list[dict]:
    """List open (not-yet-delivered) purchase orders for a SKU, newest ETA first.

    Set ``warehouse`` to filter by destination fulfillment center (e.g. ``SEA-01``).
    """
    open_pos = [
        po
        for po in load_purchase_orders()
        if po["sku"] == sku
        and po["status"] != "delivered"
        and (warehouse is None or po["destination_warehouse"] == warehouse)
    ]
    open_pos.sort(key=lambda po: po["eta"], reverse=True)
    return open_pos


async def main() -> None:
    load_env()
    endpoint = os.environ["FOUNDRY_PROJECT_ENDPOINT"]
    model = os.environ["FOUNDRY_MODEL"]

    async with (
        AzureCliCredential() as credential,
        MCPStreamableHTTPTool(
            name="learn-mcp",
            url="https://learn.microsoft.com/api/mcp",
        ) as learn_mcp,
        Agent(
            client=FoundryChatClient(
                project_endpoint=endpoint,
                model=model,
                credential=credential,
            ),
            name="Zara",
            instructions=(
                "You are Zara, the warehouse assistant for ZavaShop's Seattle fulfillment center. "
                "Always answer using real data from the tools and never invent stock or PO numbers. "
                "To find the latest unarrived PO for a SKU, call find_open_pos_by_sku (it returns "
                "open POs sorted by ETA, newest first); then call get_po_status with that PO number "
                "for fuller detail when needed."
            ),
            tools=[
                get_stock,
                get_po_status,
                find_open_pos_by_sku,
                learn_mcp,
            ],
        ) as agent,
    ):
        session = agent.create_session()

        prompts = [
            "How many SKU-7421 do we have left at SEA-01?",
            "What is the most recent PO for that SKU that hasn't arrived yet?",
            "Search the Microsoft Learn MCP for best practices on Azure AI Foundry.",
        ]

        for index, prompt in enumerate(prompts, start=1):
            result = await agent.run(prompt, session=session)
            print(f"Turn {index} prompt: {prompt}")
            print(f"Turn {index} reply: {result.text}\n")


if __name__ == "__main__":
    asyncio.run(main())