# Zexus

**An in-process Revit AI agent that compiles and runs C# at runtime to drive any Revit API operation through natural language.**

Zexus is a Revit add-in that runs entirely inside the Revit process. Describe what you want in plain English; the agent writes Revit API code on the fly, compiles it via Roslyn, and executes it against your active document — no script files to manage, no external service to deploy, no per-task tool to author. Read-only inspection runs without ceremony; write operations land behind explicit user confirmation.

## Features

- **Roslyn dynamic compilation, in-process** — every action the agent takes is real C# code compiled at runtime against the live Revit API. No predefined tool catalogue to maintain; the agent reaches whatever API surface it needs.
- **Multi-provider LLM** — choose Anthropic (Claude), OpenAI (GPT), or Google (Gemini). Bring your own API key; configure on first launch.
- **Guardrails for write operations** — the agent must present a plan and obtain explicit user confirmation before any model-modifying transaction.
- **Native Revit plugin** — installs as a per-user MSI, registers as a Revit add-in, ships in a Ribbon tab. Supports Revit 2022–2026 (.NET Framework 4.8 + .NET 8).

## Quick Start

1. Download `Zexus_Setup_v*.msi` from the [latest release](https://github.com/QuanZ827/zexus/releases).
2. Run the installer. It is **per-user** and does not require admin privileges.
3. Launch Revit. Open the **Zexus** Ribbon tab and click the agent button.
4. On first launch, open **Settings**, choose a provider (Anthropic / OpenAI / Google), and paste your API key.

For prerequisites and detailed instructions, see [docs/installation.md](docs/installation.md).

## License

Zexus is licensed under the [Apache License, Version 2.0](LICENSE).

## Author

[Zhequan Zhang](https://github.com/QuanZ827) — independent project. See [DISCLAIMER.md](DISCLAIMER.md) for affiliation notes.
