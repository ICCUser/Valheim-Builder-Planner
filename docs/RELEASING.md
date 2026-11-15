# Releasing

Every push to `main` builds the Windows zip (Actions → Build → artifacts). A version tag publishes it:

```
git tag v1.0.1
git push origin v1.0.1
```

The workflow then:
1. builds and exports the app, checks no game data is inside;
2. signs the exe and the app DLL with SignPath Foundation (if configured);
3. publishes the zip as a GitHub release;
4. uploads the same zip to Nexus Mods as a new version of the main file (if configured).

Every build also gets a GitHub build provenance attestation for the zip and the exe. Anyone can check that a file
was built by this workflow from the public source (needs the GitHub CLI):

```
gh attestation verify ValheimBuilderPlanner-v1.0.1-win64.zip -R ICCUser/Valheim-Builder-Planner
```

Attestations are listed at https://github.com/ICCUser/Valheim-Builder-Planner/attestations

Bump `Version` in `app/src/AppInfo.cs` and `project.godot`, and update `CHANGELOG.md`, before tagging.

## One-time setup (repo Settings → Secrets and variables → Actions)

### Nexus Mods upload
Uses the official [Nexus-Mods/upload-action](https://github.com/Nexus-Mods/upload-action) (v3 API, still tagged beta).

| Kind | Name | Value |
|---|---|---|
| Secret | `NEXUSMODS_API_KEY` | Personal API key from https://www.nexusmods.com/settings/api-keys |
| Variable | `NEXUSMODS_FILE_ID` | ID of the existing main file of mod 4881 (mod page → Files tab → tick "Advanced", or Manage files → edit) |

Each upload still goes through Nexus' own file checks. The previous version is archived automatically.

### SignPath Foundation signing
After the project is accepted on https://signpath.org (see `docs/CODE_SIGNING.md`):

| Kind | Name |
|---|---|
| Secret | `SIGNPATH_API_TOKEN` |
| Variable | `SIGNPATH_ORGANIZATION_ID` |
| Variable | `SIGNPATH_PROJECT_SLUG` |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` |
| Variable | `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` |

Paste `.signpath/artifact-configuration.xml` as the project's artifact configuration. Each signing request must be approved on signpath.io before the workflow continues.

Without these secrets the steps are skipped and the release is unsigned / GitHub-only.
