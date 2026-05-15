# Pipelines Explorer for Visual Studio 2026

Browse Azure DevOps pipelines without leaving Visual Studio. Drill into the YAML graph, jump to referenced templates and PowerShell scripts, and open the matching files inside your loaded solution — all from a dedicated tool window.

## Features

- **Pipelines tree** grouped by Microsoft Entra tenant, Azure DevOps organization, project, and repository.
- **Multi-tenant sign-in** with Microsoft Entra (interactive + silent token cache); switch tenant from the toolbar.
- **PAT fallback** for Azure DevOps organizations that don't accept Entra tokens.
- **Per-repository branch override** (vertical scrollable picker) when you need to inspect a non-default branch.
- **Workspace ↔ pipeline linking**: bind a project/repo node to a folder under your loaded solution and open referenced YAML / PowerShell files with a single click.
- **Localized UI** in English (default), Italian, French, German, Spanish, and Swedish — follows the Visual Studio display language.
- **Accessible by design**: keyboard-only navigation, screen reader names, theme-aware brushes (light / dark / high-contrast).

## Getting started

1. Install the extension and restart Visual Studio 2026.
2. **View → Other Windows → Pipelines Explorer**.
3. Click **Sign in with Microsoft** (or paste a PAT).
4. Pick a tenant, then expand orgs / projects / pipelines.
5. (Optional) Right-click a project / repository → **Link to workspace folder** to enable jump-to-file.

## Requirements

- Visual Studio 2026 (any SKU).
- An Azure DevOps account with read access to the pipelines you want to browse.

## Links

- Source code & issues: https://github.com/rcappello/PipelinesExplorer
- License: MIT
