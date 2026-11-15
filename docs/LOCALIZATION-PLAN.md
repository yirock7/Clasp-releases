# Localization plan — Clasp for Windows — 2026-11-14

**Picking this up:** Clasp's UI was English-only, with every user-facing string hardcoded in XAML or C#. This plan
adds translations into ten languages. **Status: phases 0 and 1 are done** (see the notes at the end of each);
phases 2 to 6 are not started. Work through the phases in order; each one ends in a mergeable state with tests passing.

**Branch / commit:** plan written against `main` at `d8e9b63`. Do the work on a new branch, e.g. `claude/localization`.
**Intended models:** Sonnet for phases 0–3 and 5 (careful, mechanical, multi-file work), Opus for phase 4
(translation quality matters, and it's cheap per string). Haiku isn't a good fit for phase 1: separating UI text
from log text takes judgment.

## Target languages

| Resource folder | Language | Notes |
|---|---|---|
| (neutral, `en-US`) | English | Source language, stays the fallback |
| `ja` | Japanese | |
| `zh-Hans` | Chinese, Simplified | |
| `zh-Hant` | Chinese, Traditional | Taiwan and Hong Kong vocabulary (Taiwan first) |
| `de` | German | Longest strings; the layout test case |
| `fr` | French | Space before `%`, `:`, `?`, `!` (narrow no-break space) |
| `es` | Spanish (Mexican vocabulary) | Neutral `es` so es-ES, es-US and es-419 all get it. *audífonos*, *computadora*, *celular*; formal *usted* |
| `pt` | Portuguese (Brazilian) | Neutral `pt` so pt-PT falls back to it rather than to English |
| `ko` | Korean | |
| `it` | Italian | |
| `fil` | Filipino | Natural, conversational tone; keep common tech words in English (Bluetooth, battery %) |

Use neutral folders (`es`, `pt`) rather than regional ones (`es-MX`, `pt-BR`) so that every regional variant
falls back to them. A regional override can be added later if users ask.

## Architecture decisions

The app is **WinUI 3, unpackaged, self-contained, Native AOT** (`src/AppleDeviceHub.App/AppleDeviceHub.App.csproj`).
User-facing text comes from two places:

1. **XAML** (`Views/*.xaml`): about 245 attribute strings, most in `SettingsView.xaml` (~170), `FlyoutView.xaml`,
   `WelcomeView.xaml` and `BatteryHistoryView.xaml`.
2. **Core view models** (`src/AppleDeviceHub.Core/Presentation/*`, `Ble/PairingInstructions.cs`,
   `Updates/UpdateViewModel.cs` and others): status lines, automation names, battery descriptions.
   Core has no WinUI dependency and must not gain one.

Decisions:

- **XAML text → `.resw` + `x:Uid`** (the standard WinUI/MRT Core approach). Files go in
  `src/AppleDeviceHub.App/Strings/<lang>/Resources.resw`. The `.pri` file is already copied to the publish output
  (`CopyCompiledWinUiResourcesToPublish` target), so check that every language's resources end up inside it.
- **Core and code-behind text → `.resx` in Core** (`src/AppleDeviceHub.Core/Resources/Strings.resx` +
  `Strings.<lang>.resx`), using the generated strongly typed `Strings` class. App code-behind strings (tray menu,
  notifications, `FlyoutView.xaml.cs`, `SettingsView.xaml.cs`, `App.xaml.cs`) use the same Core class, so
  there are only two string stores, split by where the text lives.
- **Logs and diagnostics stay English.** Anything sent to `Log(...)`, `LogText`, exception messages, engine/transport
  protocol text and command-line parsing (`ClaspCommandParser`, `HotkeyBinding` key names) must not be translated,
  because support relies on readable logs. Most of the hardcoded strings in `Interop/`, `Ble/*Transport*`,
  `Engine/` and `Connection/` are of this kind.
- **Never translate:** "Clasp", "AirPods", "AirPods Pro", "AirPods Max", "Bluetooth", "Windows", "iPhone", model
  names, and Apple feature names *in English UI* — but see the glossary rule in phase 4.
- **Formatted strings** use `string.Format` with numbered placeholders (`{0}`) in the resource, never string
  concatenation. Each sentence is one resource, because word order differs between languages. Avoid plurals that
  depend on a count. If a count is unavoidable, rephrase ("Battery: {0}%") rather than adding plural logic.
- **Numbers, percentages and times** use `CultureInfo.CurrentCulture` (`P0` already used in
  `FlyoutView.xaml.cs:410` is the model). Replace hand-built `$"{n}%"` in `BatteryItemViewModel.cs:78` and similar
  with culture-aware formatting.
- **No right-to-left languages in this round.** Arabic and Hebrew would need layout mirroring, so that's a later
  project.

## Phases

### Phase 0 — Spike: prove the plumbing under Native AOT (small, do first)

Add one `.resx` string (with a `de` version) in Core and one `x:Uid` string (with a `de` `.resw`) in the App. Then
`dotnet publish` and run the **published** exe with Windows display language, or the override below, set to German.
Confirm:

1. The `.resx` satellite resources survive Native AOT (no `UseSystemResourceKeys` trimming; check that `ResourceManager`
   returns German in the published exe, not only in `dotnet run`).
2. MRT Core resolves the German `.resw` in the unpackaged, published build.
3. How to **override the language at startup** for both systems at once: set `CultureInfo.DefaultThreadCurrentUICulture`
   (for `.resx`) and `Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride` (for MRT).
   Check that the latter API exists and works unpackaged in the WinAppSDK version we reference (WinUI 2.3.9 package).
   If it doesn't, find the MRT Core equivalent (`ResourceContext` / `ResourceManager` qualifier) and document it.

Write the findings into the Traps section below before starting phase 1. If either system fails under AOT, stop and
report back. The architecture changes in that case.

### Phase 1 — Extract English strings (no visible change) — DONE

Done 2026-11-14: 759 Core/code-behind strings in `Strings.resx`, 247 XAML entries in `Resources.resw`, 1782 Core tests
unchanged and passing, and `tools\Check-Localization.ps1` reports no findings. See "Phase 1 results" in Traps for what
to know before phases 2 to 4.

- Give every user-facing XAML element an `x:Uid` and move its text to `Strings/en-US/Resources.resw`
  (`<Uid>.Text`, `.Header`, `.Content`, `.PlaceholderText`, `[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name`,
  `[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip`). Remove the literal from the XAML so a missing
  resource shows up as blank rather than silently staying English.
- Move user-facing Core and code-behind strings to `Strings.resx`. Key names describe *where and what*
  (`Flyout_Status_LookingForAirPods`, `Battery_LeftEarbud_Name`), not the English wording.
- **Every resource gets a `<comment>`** saying where it appears and any length limit ("Flyout headline, one
  line, ~30 chars visible"). Translations depend on these comments.
- Make the Core test project run under `en-US` UI culture (set it in a module initializer or test fixture) so
  the ~34 test files that assert English text keep passing unchanged.
- Installer (`installer/Clasp.iss`): leave it for phase 5.
- **Done when:** the app looks identical in English, all tests pass, and a grep of `Views/*.xaml` finds no
  user-facing literal text (decorative "Clasp" brand text is fine).

### Phase 2 — Pseudo-localization and layout

- Add a build-time or test-time generator that writes a pseudo-locale (`qps-ploc`): every string padded ~40% with
  accented characters, e.g. `[Ŝéţţîñĝš ~~~~]`. Don't ship it in release builds.
- Run the app in pseudo-locale and fix truncation and clipping in the flyout, settings, welcome and pairing help.
  Prefer wrapping and `TextTrimming` with a tooltip over fixed widths.
- Check that CJK text renders (Segoe UI falls back to Yu Gothic UI, Microsoft YaHei UI, Malgun Gothic). Look
  for any custom `FontFamily` that would break fallback.

### Phase 3 — Language setting

- Settings → General: **Language** dropdown. The first entry is "Windows default"; the rest list each language
  in its own name ("日本語", "Deutsch", "Español", "Filipino"…). Store it in `HubSettings`.
- Apply it at startup using the mechanism found in phase 0. A change takes effect after restart; show
  "Restart Clasp to change the language" and a Restart button.
- Language names in the dropdown are **not** translated (each is always shown in its own language).

### Phase 4 — Translations

- First write `docs/localization/GLOSSARY.md`: for each language, Apple's own published term for each AirPods
  feature (Noise Cancellation, Transparency, Adaptive Audio, Conversation Awareness, Spatial Audio, Personalized
  Volume, Ear Detection, Charging Case) plus Windows' own terms (Settings, Bluetooth & devices, Sign in). Match
  Apple's and Microsoft's terms, not literal translations. Flag any term you're unsure of rather than guessing.
- Translate one language per commit, all keys, respecting the comments and placeholders.
- Add a **coverage test** in Core.Tests: every key in the neutral `.resx` and `.resw` exists in every language, no
  extra keys, and the `{n}` placeholders match exactly. It runs in CI so a new English string without translations
  fails the build. If that's too strict for day-to-day work, make missing keys a warning and placeholder
  mismatches an error.
- Screenshot the flyout and settings in each language (German, Japanese and Filipino at minimum) and check for clipping.
- Mark every language **"machine-translated, review welcome"** in the docs until a native speaker reviews it.

### Phase 5 — Installer

- Add an Inno Setup `[Languages]` section. Inno ships official message files for German, French, Spanish,
  BrazilianPortuguese, Italian, Japanese and Korean (check what the installed version includes). Chinese
  (Simplified and Traditional) and Filipino need unofficial or custom `.isl` files. Translate the custom
  `[Messages]`/task descriptions in `Clasp.iss` with `[CustomMessages]`.
- Inno Setup isn't installed on the owner's dev machine. Check that `Build-Installer.ps1` and the CI path still work.

### Phase 6 — Docs and release

- Follow `docs/RELEASING-UPDATES.md` (README, docs/, screenshots, checker tag). Add a "Languages" section to the README
  and a `CONTRIBUTING.md` section on how to fix or add a translation (where the files are, the coverage test, the
  glossary).
- Release notes: list the languages and that they welcome native-speaker corrections.

## Open questions and owner actions

- **Owner:** find native-speaker reviewers (community, after open-sourcing). Optionally choose a translation
  platform (Weblate is free for open source and reads `.resx`/`.resw` directly).
- **Owner:** confirm the language set above before phase 4 starts. Adding a language later is cheap once phases 1–3 exist.
- **Code:** whether the tray tooltip and toast notifications go through the same `Strings` class. They should;
  check during phase 1.

## Traps

- **Don't translate log text.** It's the biggest category of hardcoded strings and looks like UI text. If a string
  feeds `Log(...)`, a transport, the engine, or a parser, leave it alone.
- **`x:Uid` resource keys are case-sensitive**, and a key such as `Foo.Text` silently does nothing if the
  property name is wrong. Phase 1's "no literal text left" check is what catches this.
- **Native AOT and trimming**: anything that works in `dotnet run` must be re-checked in the published exe (phase 0).
  See also `Design/BindableItems.cs` / `Design/XamlCastTypes.cs` for AOT-specific XAML rules.
- **Launching the app from an agent shell** is sandboxed and must never touch the owner's running Clasp. Read the
  project's UI-testing notes before any injected input.
- **Phase 0 findings (2026-11-14, WinAppSDK.WinUI 2.3.9, .NET SDK 10.0.404, Release win-x64 AOT publish, ~1m13s).**
  Both systems work in the *published* AOT exe with **no csproj changes**; the architecture stands.
  - **`.resx` (Core):** `Resources/Strings.resx` + `Strings.<lang>.resx` are picked up by the SDK default glob. The
    publish folder has no `de\*.resources.dll`, but ILC embeds the satellite resources in the exe and
    `ResourceManager` returns German. Fallbacks behave as normal .NET (`de-DE`→`de`, `zh-CN`→`zh-Hans`, `pt-PT`→`pt`,
    no resx for the language → neutral English). Don't set `UseSystemResourceKeys`. Core's analyzers warn CA1707
    (underscored names) and CA1304 (`GetString` without culture) on a `Strings` accessor, so suppress them there.
  - **`.resw` + `x:Uid` (App):** `Strings\<lang>\Resources.resw` compile into the app `.pri` automatically (no
    `PRIResource` items), and the existing `CopyCompiledWinUiResourcesToPublish` target already ships it. The published
    `.pri` has a `Language` qualifier per folder (`DE`, `PT`, `ZH-HANS`, `FIL`, …), and a view built from XAML with
    `x:Uid` shows the German text. Neutral folders (`pt`, `zh-Hans`, `fil`) match `pt-PT`, `zh-CN` and `fil-PH`; a language
    with no folder (e.g. `es` before it exists) falls back to `en-US`. Extra languages add almost nothing to pri size.
    Code lookups: `new ResourceLoader().GetString("<Uid>/<Property>")` (e.g. `SpikeText/Text`).
  - **Language override at startup (both systems):**
    ```csharp
    var ci = CultureInfo.GetCultureInfo(lang);
    CultureInfo.DefaultThreadCurrentUICulture = ci;  // .resx, all threads
    CultureInfo.CurrentUICulture = ci;               // .resx, this thread
    Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = lang;  // MRT + x:Uid
    ```
    The override is **not persisted**, so set it on every start. Use the **WinAppSDK** type
    `Microsoft.Windows.Globalization.ApplicationLanguages`; the OS type `Windows.Globalization.ApplicationLanguages` throws
    `InvalidOperationException` 0x80073D54 in an unpackaged app. Set it before any XAML with `x:Uid` loads (the `App()`
    constructor before `InitializeComponent()`, or `OnLaunched` right after `HubSettingsStore.Load` and before any view
    is built). Once set, per-context `QualifierValues["Language"]` changes are ignored, so don't rely on them.
  - Not covered by the spike: a real German Windows display language (only the override was exercised) and a visible window
    screenshot (the `TextBlock.Text` was read after load instead).
- **Phase 1 results (2026-11-14).**
  - **Typed accessor:** `Core.csproj` makes MSBuild generate the public class `AppleDeviceHub.Core.Resources.Strings` from
    `Resources/Strings.resx` at build time (into `obj\`, nothing checked in). `NeutralResourcesLanguage` is `en-US`, and CA1863
    (cache a `CompositeFormat`) is off because strings are looked up at run time. Call sites with placeholders use
    `string.Format(CultureInfo.CurrentCulture, Strings.X, a, b)`. Add a key by editing the resx; it must have a `<comment>`.
  - **Tests** run under en-US through a `[ModuleInitializer]` in `tests/AppleDeviceHub.Core.Tests/TestCulture.cs`.
  - **`tools\Check-Localization.ps1`** (about 2 s, exit 1 on findings) checks that every `x:Uid` has resw keys, no resw key is
    unused, every `Strings.X` exists in the resx, every key has a `<comment>`, and no user-facing literal is left in
    `Views/*.xaml`/`App.xaml`. Exceptions go in `tools\localization-allowlist.txt`. Run it before committing string changes.
  - **Key naming:** resx keys are `Area_Where_What` (`Flyout_`, `Settings_`, `Hub_`, `Companion_`, `AudioOutput_`, `Ble_PairingGuide_`,
    `Pairing_`, `Update_`, `Tray_`, `Toast_`, `Win*_` for App controllers…). `x:Uid` names are PascalCase, prefixed by the view
    (`Settings…`, `Flyout…`, `Welcome…`, `History…`) and global across the app. Attached properties use the form
    `Uid.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name` and
    `Uid.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip`; both resolve in the published AOT exe (verified in German).
  - **Verification:** a script compared the moved XAML literals with the original text (259 exact matches, no mismatches). A
    throwaway hook then dumped the text of the real windows (Settings, Flyout, Welcome, Handoff, history chart, pairing help;
    ~15k lines, ~10 demo/hardware scenarios) from the old build, the new Debug build and the new AOT publish. English was identical
    in all three, apart from timing noise. Not covered: tray menu and tooltip, toasts, `LinkConfirmWindow`.
  - **Things phases 2 to 4 must handle:**
    - Text set from code on the Welcome pages (titles, next button, empty states) has no `x:Uid`; the XAML literal is gone and the
      constructor or `Refresh` sets it from `Strings`.
    - `PairingInstructions.All` and a few other tables are built once on first use. That is correct only because the language
      is applied at startup and changes need a restart (phase 3); keep it that way.
    - The theme radio buttons and the update-frequency picker were `x:String` lists and are now `RadioButton`/`ComboBoxItem`
      elements with `x:Uid` (`SelectedIndex` bindings unchanged).
    - Update failure reasons (`UpdateReason_*`) are clause fragments ("because {0}"), written lower-case with a trailing full stop
      and slotted into `Update_Error_VerificationFailed`. Translators must keep that shape, or rework it into whole sentences.
    - Media action names (Play, Pause, …) are inserted into sentences as `{0}`; the stem-press and "seconds" strings have
      separate singular/plural keys.
    - Left in English on purpose: everything logged, engine/transport text (`Engine\`, `Interop\`, `NtpodsDaemonTransport`
      reasons, reachable only with `--transport=hardware`), command-line errors, `HotkeyBinding` key names, the demo transport
      text, ear/stem decision reasons, `LastDecision` text. `AudioOutputViewModel` `LastActionText` still shows the English
      audio role name (`Multimedia`, `Console`) via `"{Role}: {Detail}"`; localize it in phase 4 if it matters.
    - `"Clasp"` brand text, the `© 2026 …` licence line and URL link texts in Settings stay literal; the per-app rule placeholder
      `spotify.exe=airpods` is a resource whose comment says not to translate it.

