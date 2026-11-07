# Code signing policy

Windows releases of Valheim Builder Planner are built from this public repository by GitHub Actions
(`.github/workflows/build.yml`) and, once enabled, signed through SignPath.

**Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).**

## What is signed
Only binaries built from this repository, as listed in `.signpath/artifact-configuration.xml`:
- `ValheimBuilderPlanner.exe` (the Godot export of the app)
- `data_ValheimPlanner_windows_x86_64/ValheimPlanner.dll` (the app's compiled C# code)

Third-party files (the .NET runtime, AssetsTools.NET) are shipped as published by their authors and are not signed by this project.

## Team roles
- **Committers and reviewers:** [ICCUser](https://github.com/ICCUser)
- **Approvers** (approve each signing request): [ICCUser](https://github.com/ICCUser)

Every signed release comes from a tagged commit on `main`, built by the workflow above; no locally built binary is ever submitted for signing.

## Privacy policy
This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

In practice the planner makes no network connections at all. It reads the user's own Valheim installation (read-only) and stores its data, plans and settings locally in `%APPDATA%\ValheimBuilderPlanner`.
