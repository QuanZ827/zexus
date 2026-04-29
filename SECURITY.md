# Security Policy

## Supported Versions

| Version | Supported          |
|---------|--------------------|
| 0.2.x   | :white_check_mark: |
| 0.1.x   | :x:                |

## Reporting a Vulnerability

If you discover a security vulnerability in Zexus, **please do not open a public issue.**

Instead, report it privately:

1. **GitHub Security Advisory** (preferred): on the [Security tab](https://github.com/QuanZ827/zexus/security/advisories), click "Report a vulnerability".
2. **Email**: `QuanZ827@users.noreply.github.com`

Please include:

- Description of the vulnerability
- Steps to reproduce
- Potential impact
- Suggested fix (if any)

## Response timeline

- **Acknowledgment**: within 48 hours
- **Initial assessment**: within 1 week
- **Fix or mitigation**: depends on severity, typically within 2 weeks for critical issues

## Security considerations

### API key handling

- API keys are stored locally at `%APPDATA%\Zexus\config.json`.
- Keys are never logged, transmitted to third parties, or included in telemetry.
- Configuration files containing secrets are excluded by `.gitignore`.

### Data flow

- Zexus sends Revit model metadata (element names, parameter values, categories) to the configured LLM provider — Anthropic, OpenAI, or Google — for AI processing.
- No data is stored on external servers by Zexus itself.
- Local logs contain no API keys or authentication tokens.
- Provider selection is user-controlled; data only flows to the provider you explicitly configure.

### Code execution

- The `ExecuteCode` tool compiles and runs C# code inside the Revit process via Roslyn.
- All code execution happens through explicit AI agent invocation; there is no remote execution surface.
- Model-modifying operations require explicit user confirmation before execution.

## Best practices for users

1. **Never paste API keys into source files** — configure them in Settings only.
2. **Review AI-generated code before confirming write operations.**
3. **Save your Revit project before running AI-assisted automation.** Model edits are committed via Revit transactions and may be irreversible.
4. **Keep Zexus updated** — install the latest release for security patches.
