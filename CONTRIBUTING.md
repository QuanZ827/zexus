# Contributing to Zexus

Zexus is licensed under the Apache License 2.0. Contributions — bug reports, feature requests, pull requests — are welcome.

## Reporting issues

Open an [issue](https://github.com/QuanZ827/zexus/issues) with:

- Revit version (2022–2026)
- Zexus version
- LLM provider in use
- Steps to reproduce / minimal example
- Expected vs. actual behavior

For **security vulnerabilities**, do not open a public issue. See [SECURITY.md](SECURITY.md).

## Pull requests

1. Open an issue first for non-trivial changes — alignment before code saves rework.
2. Branch from `main`. Keep PRs focused; one logical change per PR.
3. Both targets must build cleanly:

   ```
   dotnet build Zexus.csproj -f net48          -c Release
   dotnet build Zexus.csproj -f net8.0-windows -c Release
   ```

4. Match the existing code style. Avoid refactoring outside the scope of the change.

## Code of conduct

Be respectful. Disagree on technical merits, not on people.
