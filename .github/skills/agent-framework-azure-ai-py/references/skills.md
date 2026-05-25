# Agent Skills (SDK Skill Abstractions)

The Agent Framework Python SDK ships a first-party **Agent Skills** runtime that lets an agent advertise a set of modular capabilities and load them on demand. It follows the [Agent Skills specification](https://agentskills.io/) and uses **progressive disclosure** to keep token cost low.

> The `agent_framework` Skills APIs are still experimental and emit a `FutureWarning` tagged `[SKILLS]`. To suppress it before importing the APIs:
>
> ```python
> import warnings
> warnings.filterwarnings("ignore", message=r"\[SKILLS\].*", category=FutureWarning)
> ```

## Progressive Disclosure Model

When you attach a `SkillsProvider` to an `Agent` (via `context_providers=[...]`), three tools are exposed to the model and three steps happen at runtime:

1. **Advertise** — every skill's `name` and `description` (~100 tokens each) are injected into the system prompt.
2. **Load** — the model calls `load_skill` to retrieve the full instructions for a chosen skill.
3. **Access** — the model calls `read_skill_resource` for supplementary content or `run_skill_script` for executable actions.

This means resources and scripts are never paid for in tokens unless the agent actually decides to use the skill.

## Three Ways to Define a Skill

| Aspect | File-Based | Code-Defined (`InlineSkill`) | Class-Based (`ClassSkill`) |
|--------|------------|-------------------------------|----------------------------|
| Defined in | `SKILL.md` files on disk | `InlineSkill` instance in Python | Subclass of `ClassSkill` |
| Resources | Static files under `references/` | `@skill.resource` callables or `InlineSkillResource(...)` static content | `@ClassSkill.resource` decorated methods |
| Scripts | Python files under `scripts/` (executed via subprocess) | `@skill.script` callables (in-process) | `@ClassSkill.script` decorated methods (in-process) |
| Discovery | Automatic via `FileSkillsSource` / `SkillsProvider.from_paths(...)` | Explicit (pass the instance into the provider) | Explicit (pass `MySkill()` into the provider) |
| Dynamic content | No (static files only) | Yes — functions can compute content at runtime | Yes — methods can compute content at runtime |
| Distribution | Copy directory | Inline / shared module | Package via PyPI / shared library |

All three types may be combined inside a single `SkillsProvider`.

### Script Execution

| | Code-Defined / Class-Based Scripts | File-Based Scripts |
|---|------------------------------------|--------------------|
| Defined via | `@skill.script` / `@ClassSkill.script` decorators | `.py` files in a `scripts/` directory |
| Executed | In-process (direct function call) | Delegated to a `script_runner` callable |
| `script_runner` required? | No | **Yes** — `SkillsProvider`/`FileSkillsSource` will not run file-based scripts without one |

## Environment Variables

```bash
export FOUNDRY_PROJECT_ENDPOINT="https://<account>.services.ai.azure.com/api/projects/<project>"
export FOUNDRY_MODEL="gpt-4o-mini"  # or your deployment name
```

All samples below use `AzureCliCredential` (run `az login` first). Swap in `DefaultAzureCredential` for production.

## 1. Code-Defined Skills (`InlineSkill`)

`InlineSkill` lets you build a skill entirely in Python. Static resources are passed at construction time; dynamic resources and scripts are attached via decorators.

```python
import asyncio, json, os
from textwrap import dedent
from typing import Any

from agent_framework import Agent, InlineSkill, InlineSkillResource, SkillFrontmatter, SkillsProvider
from agent_framework.foundry import FoundryChatClient
from azure.identity import AzureCliCredential

unit_converter_skill = InlineSkill(
    frontmatter=SkillFrontmatter(
        name="unit-converter",
        description="Convert between common units using a conversion factor",
    ),
    instructions=dedent("""\
        Use this skill when the user asks to convert between units.

        1. Review the conversion-tables resource to find the factor.
        2. Check the conversion-policy resource for rounding rules.
        3. Use the convert script, passing value and factor.
    """),
    # 1. Static resource — inline content passed at construction time
    resources=[
        InlineSkillResource(
            name="conversion-tables",
            content=dedent("""\
                | From       | To         | Factor   |
                |------------|------------|----------|
                | miles      | kilometers | 1.60934  |
                | kilograms  | pounds     | 2.20462  |
            """),
        ),
    ],
)


# 2. Dynamic resource — evaluated at runtime; **kwargs receives function_invocation_kwargs
@unit_converter_skill.resource(
    name="conversion-policy",
    description="Current rounding/formatting policy",
)
def conversion_policy(**kwargs: Any) -> str:
    precision = kwargs.get("precision", 4)
    return f"Decimal places: {precision}"


# 3. Dynamic script — runs in-process when the agent invokes it
@unit_converter_skill.script(name="convert", description="result = value × factor")
def convert_units(value: float, factor: float, **kwargs: Any) -> str:
    precision = kwargs.get("precision", 4)
    return json.dumps({"value": value, "factor": factor, "result": round(value * factor, precision)})


async def main() -> None:
    client = FoundryChatClient(
        project_endpoint=os.environ["FOUNDRY_PROJECT_ENDPOINT"],
        model=os.environ.get("FOUNDRY_MODEL", "gpt-4o-mini"),
        credential=AzureCliCredential(),
    )

    async with Agent(
        client=client,
        instructions="You are a helpful assistant that can convert units.",
        context_providers=[SkillsProvider(unit_converter_skill)],
    ) as agent:
        result = await agent.run(
            "How many kilometers is 26.2 miles?",
            function_invocation_kwargs={"precision": 2},  # forwarded to resource/script **kwargs
        )
        print(result)


asyncio.run(main())
```

Key points:

- `function_invocation_kwargs={...}` on `agent.run(...)` is forwarded to `**kwargs` on every resource/script that accepts it.
- A single `InlineSkill` may mix static resources (constructor) and dynamic resources/scripts (decorators).

## 2. Class-Based Skills (`ClassSkill`)

`ClassSkill` packages a skill as a reusable Python class. Resources and scripts are auto-discovered from decorators on the class. This is the preferred shape when you want to ship a skill in a library or on PyPI.

```python
import json
from textwrap import dedent
from agent_framework import Agent, ClassSkill, SkillFrontmatter, SkillsProvider
from agent_framework.foundry import FoundryChatClient
from azure.identity import AzureCliCredential


class UnitConverterSkill(ClassSkill):
    def __init__(self) -> None:
        super().__init__(
            frontmatter=SkillFrontmatter(
                name="unit-converter",
                description="Convert miles↔km, pounds↔kg using a multiplication factor.",
            ),
        )

    @property
    def instructions(self) -> str:
        return dedent("""\
            1. Read the conversion-table resource for the factor.
            2. Call the convert script with value and factor.
        """)

    # Bare @ClassSkill.resource with @property — name derived from method ("conversion_table" → "conversion-table")
    @property
    @ClassSkill.resource
    def conversion_table(self) -> str:
        return "| miles | kilometers | 1.60934 |"

    # Explicit name and description
    @ClassSkill.script(name="convert", description="Multiplies a value by a conversion factor.")
    def convert_units(self, value: float, factor: float) -> str:
        return json.dumps({"value": value, "factor": factor, "result": round(value * factor, 4)})


async def main() -> None:
    client = FoundryChatClient(
        project_endpoint=os.environ["FOUNDRY_PROJECT_ENDPOINT"],
        model=os.environ.get("FOUNDRY_MODEL", "gpt-4o-mini"),
        credential=AzureCliCredential(),
    )

    async with Agent(
        client=client,
        instructions="You are a helpful assistant that can convert units.",
        context_providers=[SkillsProvider(UnitConverterSkill())],
    ) as agent:
        print(await agent.run("How many km is 26.2 miles?"))
```

Decorator shapes supported:

- `@ClassSkill.resource` (bare) — name defaults to the method name (snake_case → kebab-case), no description.
- `@ClassSkill.resource(name="...", description="...")` — explicit metadata.
- `@ClassSkill.script` / `@ClassSkill.script(name="...", description="...")` — same conventions.

Place `@property` **before** `@ClassSkill.resource` when exposing a property-style resource.

## 3. File-Based Skills

A file-based skill is a directory containing a `SKILL.md` front-matter file plus optional `references/` and `scripts/` subdirectories.

### Directory layout

```text
skills/
└── unit-converter/
    ├── SKILL.md
    ├── references/
    │   └── CONVERSION_TABLES.md
    └── scripts/
        └── convert.py
```

### `SKILL.md` frontmatter

```markdown
---
name: unit-converter
description: Convert miles↔km, pounds↔kg using a multiplication factor.
license: MIT
compatibility: Works with any model that supports tool use.
allowed-tools: convert
metadata:
  author: agent-framework-samples
  version: "1.0"
---

## Usage

1. Read `references/CONVERSION_TABLES.md` to find the factor.
2. Run `scripts/convert.py --value <number> --factor <factor>`.
3. Present the result clearly.
```

### Loading file-based skills

```python
from pathlib import Path
from agent_framework import Agent, SkillsProvider
from agent_framework.foundry import FoundryChatClient
from azure.identity import AzureCliCredential
from subprocess_script_runner import subprocess_script_runner  # see below

skills_dir = Path(__file__).parent / "skills"

skills_provider = SkillsProvider.from_paths(
    skill_paths=str(skills_dir),
    script_runner=subprocess_script_runner,  # required for file-based scripts
)

async with Agent(
    client=FoundryChatClient(
        project_endpoint=os.environ["FOUNDRY_PROJECT_ENDPOINT"],
        model=os.environ.get("FOUNDRY_MODEL", "gpt-4o-mini"),
        credential=AzureCliCredential(),
    ),
    instructions="You are a helpful assistant.",
    context_providers=[skills_provider],
) as agent:
    print(await agent.run("How many km is 26.2 miles?"))
```

### Reference `subprocess_script_runner`

File-based scripts need an execution callback. The pattern below runs each script as a local Python subprocess and converts the model's `args` (a `list[str]`) to positional CLI arguments.

```python
# subprocess_script_runner.py
from __future__ import annotations
import subprocess, sys
from pathlib import Path
from typing import Any
from agent_framework import FileSkill, FileSkillScript


def subprocess_script_runner(
    skill: FileSkill,
    script: FileSkillScript,
    args: dict[str, Any] | list[str] | None = None,
) -> str:
    script_path = Path(script.full_path)
    if not script_path.is_file():
        return f"Error: Script file not found: {script_path}"

    cmd = [sys.executable, str(script_path)]
    if isinstance(args, list):
        for item in args:
            if not isinstance(item, str):
                raise TypeError("File-based skill scripts only accept string CLI arguments.")
        cmd.extend(args)
    elif args is not None:
        raise TypeError("Expected a list of CLI arguments.")

    try:
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=30,
                                cwd=str(script_path.parent))
        out = result.stdout + (f"\nStderr:\n{result.stderr}" if result.stderr else "")
        if result.returncode != 0:
            out += f"\nScript exited with code {result.returncode}"
        return out.strip() or "(no output)"
    except subprocess.TimeoutExpired:
        return f"Error: Script '{script.name}' timed out after 30 seconds."
```

> Treat the subprocess runner as a sample. In production you may sandbox it, run it in a container, or proxy execution to a remote service.

## 4. Composing Skills (`SkillsSource` Pipeline)

`SkillsProvider` accepts either a list of skills or a composable **skills source** pipeline. The building blocks:

| Source | Purpose |
|--------|---------|
| `InMemorySkillsSource([skill, ...])` | Wrap one or more in-memory (code/class) skills |
| `FileSkillsSource(path, script_runner=...)` | Discover file-based skills under a directory |
| `AggregatingSkillsSource([source1, source2, ...])` | Concatenate multiple sources |
| `DeduplicatingSkillsSource(inner)` | Drop later skills whose name was already seen |
| `FilteringSkillsSource(inner, predicate=lambda s: ...)` | Include/exclude skills by `frontmatter.name` or any custom logic |

### Mixed skills (code + class + file)

```python
from agent_framework import (
    AggregatingSkillsSource,
    DeduplicatingSkillsSource,
    FileSkillsSource,
    InMemorySkillsSource,
    SkillsProvider,
)

skills_provider = SkillsProvider(
    DeduplicatingSkillsSource(
        AggregatingSkillsSource([
            FileSkillsSource(str(skills_dir), script_runner=subprocess_script_runner),
            InMemorySkillsSource([volume_converter_skill, TemperatureConverterSkill()]),
        ])
    )
)
```

### Filtering which skills the agent sees

```python
from agent_framework import FilteringSkillsSource

source = DeduplicatingSkillsSource(
    FilteringSkillsSource(
        FileSkillsSource(str(skills_dir), script_runner=subprocess_script_runner),
        predicate=lambda s: s.frontmatter.name != "length-converter",  # hide this one
    )
)

skills_provider = SkillsProvider(source)
```

Use filtering to:

- Gate skills behind feature flags / tenant configuration.
- Hide internal/admin skills from end-user agents while keeping them available for back-office agents.
- Slice a large shared skills directory per-agent without copying files.

## 5. Human-in-the-Loop Script Approval

Set `require_script_approval=True` on `SkillsProvider` to require explicit approval before any `run_skill_script` call executes.

```python
from agent_framework import Agent, InlineSkill, SkillFrontmatter, SkillsProvider
from agent_framework.foundry import FoundryChatClient
from azure.identity import AzureCliCredential

deployment_skill = InlineSkill(
    frontmatter=SkillFrontmatter(
        name="deployment",
        description="Tools for deploying application versions to production",
    ),
    instructions="Use the deploy script with version and environment parameters.",
)


@deployment_skill.script
def deploy(version: str, environment: str = "staging") -> str:
    return f"Deployed version {version} to {environment}"


async def main() -> None:
    client = FoundryChatClient(
        project_endpoint=os.environ["FOUNDRY_PROJECT_ENDPOINT"],
        model=os.environ.get("FOUNDRY_MODEL", "gpt-4o-mini"),
        credential=AzureCliCredential(),
    )

    skills_provider = SkillsProvider(
        source=[deployment_skill],
        require_script_approval=True,
    )

    async with Agent(
        client=client,
        instructions="You are a deployment assistant.",
        context_providers=[skills_provider],
    ) as agent:
        session = agent.create_session()

        result = await agent.run(
            "Deploy version 2.5.0 to production",
            session=session,
        )

        # The agent pauses and surfaces approval requests instead of executing
        while result.user_input_requests:
            for request in result.user_input_requests:
                print(f"Approval needed: {request.function_call.name}"
                      f" args={request.function_call.arguments}")
                approved = True  # prompt the user in real apps
                approval_response = request.to_function_approval_response(approved=approved)
                result = await agent.run(approval_response, session=session)

        print(result)
```

How the loop works:

1. The agent calls `run_skill_script`. Because approval is required, execution is deferred.
2. `result.user_input_requests` contains one or more requests carrying the proposed `function_call` (name + JSON arguments).
3. Your application decides per request. Call `request.to_function_approval_response(approved=True|False)` to build the response.
4. Send the response back with `agent.run(approval_response, session=session)`. Approved scripts run; rejected scripts return an error to the agent so it can react.
5. Continue until `result.user_input_requests` is empty.

Use this for any skill that performs irreversible / privileged operations (deployments, payments, deletes).

## Skill vs Tool — When to Use Which

| | Function / Hosted Tool | Skill |
|---|------------------------|------|
| Granularity | Single function call | Bundle of instructions + resources + scripts |
| Token cost when idle | Schema always in context | ~100 tokens advertised, full content loaded on demand |
| Ideal for | Atomic capabilities (`get_weather`, code interpreter, web search) | Multi-step playbooks (deployment, unit conversion, runbooks) |
| Where it lives | Python function or hosted service | `SKILL.md` directory or `InlineSkill`/`ClassSkill` |
| Distribution | Inline in code | Inline, class, or copyable directory |

Reach for skills when the capability needs prose instructions, reference material, and/or multiple coordinated scripts. Reach for plain tools/functions for single-step actions.

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `FutureWarning: [SKILLS]...` floods the logs | Experimental API warning | `warnings.filterwarnings("ignore", message=r"\[SKILLS\].*", category=FutureWarning)` before importing |
| File-based script never runs | No `script_runner` passed to `FileSkillsSource` / `SkillsProvider.from_paths` | Pass `subprocess_script_runner` (or your own) |
| File-based script runner raises `TypeError` about `list[str]` | Model returned a dict instead of CLI args | Tighten the skill's instructions to specify positional `--flag value` CLI form |
| Duplicate skill names across sources | Two sources expose the same `frontmatter.name` | Wrap the outermost source in `DeduplicatingSkillsSource(...)` |
| Skill never advertised in the system prompt | Skill not reachable from any source attached to the provider, or filtered out | Verify `SkillsProvider(source)` graph and any `FilteringSkillsSource.predicate` |
| Script runs without approval despite `require_script_approval=True` | Approval response was sent without the original `session` | Always create `session = agent.create_session()` and pass `session=session` on every `agent.run(...)` in the approval loop |
| `function_invocation_kwargs` ignored inside a resource/script | Function signature missing `**kwargs` | Add `**kwargs: Any` to the resource/script signature |

## Public API Reference

```python
from agent_framework import (
    Agent,
    AggregatingSkillsSource,
    ClassSkill,
    DeduplicatingSkillsSource,
    FileSkill,
    FileSkillScript,
    FileSkillsSource,
    FilteringSkillsSource,
    InlineSkill,
    InlineSkillResource,
    InMemorySkillsSource,
    SkillFrontmatter,
    SkillsProvider,
)
from agent_framework.foundry import FoundryChatClient
```

- `Agent(client=..., context_providers=[SkillsProvider(...)])` — attach skills to any agent.
- `SkillsProvider(source, require_script_approval: bool = False)` — provider with optional approval gate.
- `SkillsProvider.from_paths(skill_paths, script_runner=...)` — shortcut for a single `FileSkillsSource`.

## Related Samples

- [Code-defined skill](https://github.com/microsoft/agent-framework/tree/main/python/samples/02-agents/skills/code_defined_skill)
- [Class-based skill](https://github.com/microsoft/agent-framework/tree/main/python/samples/02-agents/skills/class_based_skill)
- [File-based skill](https://github.com/microsoft/agent-framework/tree/main/python/samples/02-agents/skills/file_based_skill)
- [Mixed skills](https://github.com/microsoft/agent-framework/tree/main/python/samples/02-agents/skills/mixed_skills)
- [Script approval](https://github.com/microsoft/agent-framework/tree/main/python/samples/02-agents/skills/script_approval)
- [Skill filtering](https://github.com/microsoft/agent-framework/tree/main/python/samples/02-agents/skills/skill_filtering)
