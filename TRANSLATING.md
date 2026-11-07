# Translating Valheim Builder Planner

The app shows two kinds of text:

| Text | Where it comes from | Translators needed? |
|---|---|---|
| Piece, material, station and category names | The player's own copy of Valheim (36 languages) | No, they follow the language picked in the app |
| The app's own interface (buttons, help, panels, build list) | `app/i18n/*.po` files | Yes |

## Add or update a language

1. Copy `app/i18n/messages.pot` to `app/i18n/<code>.po` (for example `de.po`, `pt_BR.po`, `zh_CN.po`).
   The codes are listed in `app/src/Loc.cs` (`Languages`).
2. Translate it with any gettext tool: [Poedit](https://poedit.net), Weblate or Crowdin.
   - Keep placeholders such as `{0}` and `{1}` (you may move them in the sentence).
   - `{0:0}` means "number without decimals": keep it as it is.
   - Plural texts have `msgstr[0]`, `msgstr[1]`…: set the `Plural-Forms` header for your language (Poedit does it for you).
   - `{0}` in the help line is replaced by the movement keys of the player's keyboard (WASD, ZQSD…).
3. Add the file to `locale/translations` in `app/project.godot`, e.g.
   `PackedStringArray("res://i18n/fr.po", "res://i18n/de.po")`.

Untranslated texts fall back to English automatically.

## For developers

- Write UI texts in English and wrap them: `Loc.T("Save plan")`, `Loc.F("Cost: {0}", x)`,
  `Loc.N("{0} piece", "{0} pieces", n)`. Plain `Text = "..."` on controls is translated by Godot automatically.
- Regenerate the template after adding texts: `tools/i18n/make_pot.ps1`.
- French is kept in `tools/i18n/make_fr.ps1` and rebuilt with it (it refuses to run if a text is missing).
