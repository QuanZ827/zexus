# Installation

## Prerequisites

- **Revit**: 2022, 2023, 2024, 2025, or 2026 (Windows).
- **.NET runtime**: bundled with Revit.
  - Revit 2022 / 2023 / 2024 → .NET Framework 4.8.
  - Revit 2025 / 2026 → .NET 8 Desktop Runtime (x64).
- **LLM API key** for one of the supported providers (you only need one):
  - Anthropic (Claude) — [console.anthropic.com](https://console.anthropic.com/)
  - OpenAI (GPT) — [platform.openai.com/api-keys](https://platform.openai.com/api-keys)
  - Google (Gemini) — [aistudio.google.com/apikey](https://aistudio.google.com/apikey)

## Install via MSI (recommended)

1. Download `Zexus_Setup_v*.msi` from the [latest release](https://github.com/QuanZ827/zexus/releases).
2. Double-click the MSI. The installer is **per-user** and does not require admin privileges.
3. Files are deployed to `%APPDATA%\Autodesk\Revit\Addins\{2022..2026}\Zexus\`. The addin manifest is registered automatically for each Revit version installed on your machine.

Silent install:

```
msiexec /i Zexus_Setup_v0.2.0.msi /qn
```

## First launch

1. Start Revit. Open the **Zexus** ribbon tab.
2. Click the agent button — the chat window opens.
3. Open **Settings** (gear icon).
4. Choose a provider (Anthropic / OpenAI / Google), paste your API key, and pick a model.
5. Settings are saved to `%APPDATA%\Zexus\config.json`. Close Settings and start a conversation.

## Migrating from ZexusPureEC

If you previously installed `ZexusPureEC` (the testing build that preceded Zexus v0.2.0), the first launch of Zexus will automatically copy your config and session data from `%APPDATA%\ZexusPureEC\` to `%APPDATA%\Zexus\` and write a `.migrated_from_pureec` flag in the new directory. The old data is preserved as a fallback. Once the new install is verified, you can uninstall the old `ZexusPureEC` MSI from **Settings → Apps & features**.

## Uninstall

Remove the `Zexus for Revit` MSI from **Settings → Apps & features**. User data at `%APPDATA%\Zexus\` is **not** removed; delete it manually if you want a clean uninstall.

## Build from source

If you prefer building from source instead of installing the MSI, see [CONTRIBUTING.md](../CONTRIBUTING.md) for the required commands. The build deploys directly to your local Revit Addins folders for the Revit versions detected on your machine.
