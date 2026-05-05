# Project Instructions

This file provides context for AI assistants working on this project.

## Project Type

.NET 10 C# AI Agent SDK — a multi-model agent runtime with tool calling, state management,
event streaming, and MCP integration. Referenced by the KodaClaw product (`products/KodaClaw`).

## Build & Test

```bash
# Build the SDK (no restore needed if already done)
dotnet build src/Kode.Agent.Sdk/Kode.Agent.Sdk.csproj

# Run SDK unit + integration tests
dotnet test tests/Kode.Agent.Tests/Kode.Agent.Tests.csproj --no-restore

# Run a single test class
dotnet test tests/Kode.Agent.Tests/Kode.Agent.Tests.csproj \
    --filter "FullyQualifiedName~DeepSeekProviderTests" --no-restore

# Run KodaClaw unit tests
dotnet test products/KodaClaw/tests/KodaClaw.UnitTests/KodaClaw.UnitTests.csproj --no-restore
```

Integration tests under `tests/Kode.Agent.Tests/Integration/` are LLM-dependent and inherently
flaky — failing integration tests should be checked against a known baseline before treating
them as regressions.

## Documentation

- `README.md` — project overview, quick start, architecture diagrams
- `docs/ADVANCED_GUIDE.md` — architecture, event system, tool development, state management
- `docs/API_REFERENCE.md` — core types, Agent lifecycle, event models

## Version Control

This project uses Git. See `.gitignore` for excluded files.

## Guidelines

- Follow existing code style and patterns
- Write tests for new functionality
- Keep changes focused and atomic
- Document public APIs

## Workflow Conventions

1. **Baseline first** — before touching any code, run the relevant test suites and note
   the pass/fail state. After the change, compare. Failures that exist before your edit
   are *not* regressions.
2. **Acceptance criteria up front** — define "done" before starting. Minimum bar:
   root cause identified → fix applied → targeted tests pass → full SDK suite passes →
   KodaClaw unit tests pass.
3. **Atomic diffs** — one problem, one commit. Don't roll unrelated changes into the
   same patch. A reviewer (or another AI) should be able to read the diff and understand
   the change in one pass.
4. **Plan → Execute → Verify** — for multi-step work, lay out a checklist before acting.
   Every step produces observable evidence (a test run, a diff, a file read). No
   invisible progress.
5. **Flaky tests are not blockers** — integration tests under `tests/Kode.Agent.Tests/Integration/`
   depend on live LLM responses and will occasionally fail for reasons unrelated to
   your change. Don't treat them as regression signals unless they fail consistently
   across multiple runs or the failure message points directly at your code.

## Key Architectural Notes

### Model Providers

Located in `src/Kode.Agent.Sdk/Infrastructure/Providers/`:

| Provider | Class | Notes |
|---|---|---|
| Anthropic | `AnthropicProvider` | Uses official SDK |
| OpenAI | `OpenAIProvider` | Uses official SDK |
| DeepSeek | `DeepSeekProvider` | Raw HTTP (OpenAI-compatible chat/completions); handles `reasoning_content` natively |
| OpenAI Responses | `OpenAIResponsesProvider` | Raw HTTP (`/v1/responses` endpoint) |

### DeepSeek Provider — Tool Result Ordering

DeepSeek's API (like OpenAI) requires that every assistant message with `tool_calls` is
immediately followed by one `role: "tool"` message per `tool_call_id`. The SDK packs
multiple `ToolResultContent` blocks into a single `User` message (see `Agent.Step.cs:244`),
so `DeepSeekProvider.ConvertMessages()` expands them into individual `tool` messages.
**Do not collapse multiple tool results into a single message** without verifying this
expansion logic still works.

### Thinking Mode (`reasoning_content`)

DeepSeekProvider passes `reasoning_content` back verbatim to the API as
`ThinkingContent` / `StreamChunkType.ThinkingDelta` blocks. When `EnableThinking` is true,
the provider injects `{"thinking": {"type": "enabled"}}` and forces `temperature` to `1.0`.
Other providers (OpenAIProvider) use a marker-based workaround.
