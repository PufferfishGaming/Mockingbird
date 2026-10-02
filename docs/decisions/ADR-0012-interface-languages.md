# ADR-0012: The interface in five languages

Status: accepted (after 0.1.19, unreleased).

## Context
Features.md asks for an interface in the most common languages plus Hungarian, chosen at install and changeable in the app, with every element and message translated.

## Decision
- **Languages:** English, Hungarian (Magyar), German (Deutsch), Spanish (Español), French (Français). Chosen in a small window on the first start (Windows' language is listed first), changed in Settings → Appearance → Language, applied at once with no restart. The choice is `language` in settings.json; an empty value means "not asked yet" (so an existing installation is asked once after updating). A test or screenshot run can force a language with `TRIASR_LANGUAGE` without choosing it.
- **Texts are looked up by their English wording** (`Loc`): English needs no table; `Languages/xx.json` maps English text to the translation and is embedded in the app. A text without a translation is shown in English, never blank. This keeps the source readable and lets a test find every text mechanically. XAML uses `{local:T 'text'}` (a binding on `Loc.Version`, so it refreshes when the language changes), code uses `T("text {0}", x)`; values that stay English inside the program because they are saved or compared (themes, presets, export modes, job states, language names) are marked with `Loc.Key` and shown through `{local:Tr Path}`.
- **No sentence is assembled from pieces and no plural is computed in code.** Each variant is its own text with numbered placeholders (`{0:0.0}`), so a translator can reorder words.
- **Messages the view model built earlier are built again after a switch** (summaries, readiness list, banners, download state, tables), and a status line that still says its start-up text is translated. A one-off message shown before the switch (a past error, the last status) stays in the language it was written in until the next event replaces it.
- **Texts from lower layers** (stage names, language coverage, benchmark labels, fixed exception messages) are listed in `Languages/extra-keys.json` and translated where they are shown (`ReportError` and `Loc.T`'s arguments translate a text that matches exactly). Technical messages with variable content from the operating system or the engines stay as they come.
- **The 100 language names** (Whisper's catalogue) are translated too and shown as "Name (code)". The privacy policy is translated in `Languages/privacy/xx.md`. Number and date formats follow the Windows regional settings, not the interface language.
- **Enforced by tests** (`TranslationTests`): every used text has a translation in every language with identical placeholders and line breaks, tables hold nothing unused, no literal text is left in XAML, no message is shown without a lookup. The test project runs one test at a time because the language is one setting for the whole program. The shell smoke renders the main pages in every language and checks that the window really changes.

## Not done: language choice inside the installer
The installer is the standard WiX Burn wizard (ADR-0001). Its page texts live in one embedded localisation and Burn cannot offer a choice between languages at run time without a custom bootstrapper application, which would be a large, hard-to-test addition. The wizard stays English; the app asks for the language when it first starts, which the installer's "launch" option opens straight away. A multilingual installer would need either a custom bootstrapper (managed) or one Setup.exe per language.

## Translation quality
The translations were written by a language model and checked mechanically (placeholders, structure, completeness) but not by native speakers. They should be reviewed by someone who speaks each language before the release.
