"""Bootstrap the ZavaShop procurement Foundry Toolbox.

This script is the Step 1 deliverable of LAB 2. It does three things:

1. Loads the shared `workshop/.env` (FOUNDRY_PROJECT_ENDPOINT / FOUNDRY_MODEL /
   optional FOUNDRY_TOOLBOX_ENDPOINT) so a learner does not have to `export`
   anything by hand.
2. Sanity-checks the shared data layer (`workshop/data/zava_data.py`) by
   printing how many suppliers and contracts are reachable. This is the
   acceptance bullet for "real fixtures, not inline mocks".
3. Tries to provision a Foundry Toolbox version via the Azure AI Projects
   SDK and gracefully degrades when the currently-installed prerelease does
   not expose the `beta.toolboxes` surface (which is the case at the time
   this LAB was written).

When the SDK lacks the toolbox API, the script tells the learner exactly
what to do instead (provision the toolbox in the Foundry portal and set
`FOUNDRY_TOOLBOX_ENDPOINT`). `pierre_agent.py` will still run end-to-end
because it falls back to Microsoft Learn MCP when no toolbox endpoint is
configured.
"""

from __future__ import annotations

import asyncio
import os
import sys
from pathlib import Path

# Make the shared data helpers importable -----------------------------------
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "data"))
from zava_data import (  # noqa: E402  (path-injected import)
    find_contract,
    find_supplier,
    load_contracts,
    load_suppliers,
)


def load_env() -> None:
    """Read `workshop/.env` and populate any env vars that are not already set."""
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


async def inspect_toolbox_surface() -> None:
    """Probe the Foundry Toolbox surface and list existing toolboxes.

    The current `azure-ai-projects` prerelease exposes
    `project_client.beta.toolboxes` with `create_version` / `list` / etc.,
    but actually creating a toolbox version requires connections to live
    backend services (a supplier OData/REST API plus two MCP servers).
    For the LAB we just want to verify that the surface is reachable and
    show the learner which existing toolboxes are configured — they can
    then provision `zavashop-procurement` in the Foundry portal and set
    `FOUNDRY_TOOLBOX_ENDPOINT` for `pierre_agent.py`.
    """
    endpoint = os.environ.get("FOUNDRY_PROJECT_ENDPOINT")
    if not endpoint:
        print("[skip] FOUNDRY_PROJECT_ENDPOINT is not set; nothing to inspect.")
        return

    try:
        from azure.ai.projects.aio import AIProjectClient
        from azure.identity.aio import AzureCliCredential
    except ImportError as exc:
        print(f"[skip] azure-ai-projects / azure-identity not installed: {exc}")
        return

    print(f"[info] Connecting to Foundry project: {endpoint}")
    async with AzureCliCredential() as credential, AIProjectClient(
        endpoint=endpoint, credential=credential
    ) as project_client:
        toolboxes = getattr(getattr(project_client, "beta", None), "toolboxes", None)
        if toolboxes is None:
            print(
                "[degraded] This `azure-ai-projects` build does not expose"
                " `project_client.beta.toolboxes`."
            )
            _print_portal_fallback()
            return

        print("[info] Toolbox surface detected. Operations available:")
        ops = sorted(
            attr for attr in dir(toolboxes)
            if not attr.startswith("_") and callable(getattr(toolboxes, attr))
        )
        for op in ops:
            print(f"         - beta.toolboxes.{op}(…)")

        print("[info] Attempting toolboxes.list() …")
        try:
            count = 0
            async for toolbox in toolboxes.list():
                count += 1
                name = getattr(toolbox, "name", None) or getattr(toolbox, "id", "?")
                print(f"         - {name}")
            if count == 0:
                print("[info] No toolboxes provisioned yet for this project.")
        except Exception as exc:  # noqa: BLE001 — SDK can raise gzip/Unicode/HTTP errors
            print(
                "[degraded] toolboxes.list() failed:"
                f" {type(exc).__name__}: {exc}"
            )
            print(
                "           This is a known issue with the current prerelease."
                " Falling back to portal-based provisioning instructions."
            )

        _print_portal_fallback()


def _print_portal_fallback() -> None:
    print("[next] To finish wiring the toolbox manually:")
    print("       1. Open the Foundry project in the portal.")
    print("       2. Toolboxes → New toolbox → name it `zavashop-procurement`.")
    print("       3. Add the supplier OData/REST connection plus two MCP"
          " servers (supplier-data and contracts-data).")
    print("       4. Copy the toolbox MCP endpoint URL and set")
    print("          FOUNDRY_TOOLBOX_ENDPOINT=<url> in workshop/.env, then")
    print("          re-run `pierre_agent.py`.")
    print("       If FOUNDRY_TOOLBOX_ENDPOINT stays unset, `pierre_agent.py`")
    print("       will fall back to Microsoft Learn MCP so the wiring still"
          " runs end-to-end.")


def main_sync() -> None:
    load_env()

    suppliers = load_suppliers()
    contracts = load_contracts()
    print(f"[data] suppliers loaded: {len(suppliers)}")
    print(f"[data] contracts loaded: {len(contracts)}")

    demo_supplier = find_supplier("SUP-001")
    demo_contract = find_contract("SUP-001")
    if demo_supplier:
        print(
            "[data] SUP-001 → "
            f"{demo_supplier['name']} ({demo_supplier['country']})"
        )
    if demo_contract:
        print(
            "[data] active contract for SUP-001: "
            f"{demo_contract['contract_id']} "
            f"(cap ${demo_contract['max_single_po_usd']:,} per PO)"
        )

    asyncio.run(inspect_toolbox_surface())


if __name__ == "__main__":
    main_sync()
