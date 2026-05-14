# Changelog

This file aggregates notable changes across all clients in this repository.
Per-client changelogs (used by their respective marketplaces) live under each
client folder:

- [`src/vscode/CHANGELOG.md`](src/vscode/CHANGELOG.md)
- (`src/vs2026/CHANGELOG.md` will be added once the VS 2026 client ships.)

Entries below use the format `[client] message`, where `client` is `vscode`
or `vs2026`.

## Unreleased

- `[repo]` Split the repository into a monorepo layout with `src/vscode` and
  `src/vs2026` client roots; added path-filtered CI workflows
  (`.github/workflows/vscode.yml`, `vs2026.yml`) and renamed the VS Code
  release workflow to `release-vscode.yml` (now triggered by `vscode-v*` tags).
- `[vs2026]` Initial scaffold of the Visual Studio 2026 extension using the
  Microsoft.VisualStudio.Extensibility SDK (out-of-process, .NET 8, WPF).

## VS Code client

See [`src/vscode/CHANGELOG.md`](src/vscode/CHANGELOG.md) for the full history
of the VS Code extension (latest: 0.1.1).
