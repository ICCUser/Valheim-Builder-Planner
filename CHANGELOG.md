# Changelog

## 1.0.1
- Releases are now built by GitHub Actions from the public source, with a GitHub build provenance attestation (`gh attestation verify`), and published automatically on GitHub and Nexus Mods.
- Clearer explanation of the two ways to rebuild a plan in game: vanilla by hand (achievements stay on) or with the PlanBuild mod (achievements off while mods are loaded).
- No change to the app's features.

## 1.0.0 — first release
- Plan builds with every Hammer piece of Valheim (≈ 390), read from the player's own install on first launch (shapes, sizes, snap points, costs, stations, names in 36 languages). No game data is shipped.
- Game-accurate snapping and 22.5° rotation steps, manual snap point choice (Ctrl+wheel).
- Structural stability using the game's rules for every material; collapse warning; stability preview before placing.
- Floor view with per-floor piece lists and materials; build list export (text checklist).
- Rebuild guide: step-by-step order (floors → poles → walls → doors → beams → roof → furniture), current step highlighted, free navigation.
- Copy/paste groups, selection, undo, autosave, save/open plans.
- Optional PlanBuild support: one-click export to detected PlanBuild installs (manual, r2modman, Thunderstore) and import of PlanBuild `.blueprint` / BuildShare `.vbuild` files.
- Norse-style interface, piece thumbnails, procedural materials.
- Interface in English and French; translation template for the community.
