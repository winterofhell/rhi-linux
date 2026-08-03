# Known Limitations

## RenoDX catalog matching

- Steam titles that use Roman numerals (for example `Remnant II`) only match wiki titles that use Arabic digits (for example `Remnant 2`) when an exact-game addon row exists and identity forms overlap. Verified Unreal/Unity list rows without direct addon URLs still resolve through the generic engine addon.
- Anti-cheat flagged games remain blocked even when the official wiki lists a downloadable addon (for example Warhammer 40,000: Space Marine 2).
- Contributor tip-block addons such as DLSSFIX are intentionally not treated as the generic Unreal engine addon.

## RenoDX Discussion artifact resolution

- Official GitHub Discussions are fetched without authentication. When a Discussion only names an addon in prose and distributes binaries through Discord or Nexus, the result is `Official page available, no direct addon` rather than automatic Install.
- Filename mentions in Discussion text never synthesize download URLs.
- GitHub Discussion HTML structure varies; author badges may not always parse, but the original post remains trusted as the first authoritative comment body.

## Managed archive extraction

- Official ReShade full-addon installers are PE executables with a ZIP overlay. RHI Linux reads the PE overlay in-process, extracts only the required `ReShade32.dll` or `ReShade64.dll`, and never executes the installer.
- OptiScaler official release archives are extracted with SharpCompress. External tools such as `bsdtar` or `7z` are optional and are not required for supported formats.
- If an upstream release switches to an unsupported compression method, RHI Linux reports that the release cannot be inspected safely and leaves game files unchanged.

## RenoDX snapshot release resolution

- Exact addon resolution prefers validated wiki direct URLs, then official `clshortfuse/renodx` snapshot-release structured metadata / exact release assets, then official Discussion attachments, then approved generic engine addons.
- Games listed only through Discussions without a matching snapshot release asset remain manual-only.
- Snapshot release asset enumeration depends on the GitHub Releases API. When unauthenticated rate limits are hit, RHI Linux falls back to the last cached release index when available.

## Component stack detection

- Component cards derive from a single `StackSnapshot`. Independent per-component heuristics are no longer combined after the fact.
- OptiScaler/ReShade PE markers are scanned across the full binary. A previous 2 MiB prefix scan missed markers in large OptiScaler proxies such as `version.dll` and incorrectly reported `The OptiScaler proxy DLL is missing.`
- Update availability requires positive proof that the installed immutable runtime differs from a newer compatible official artifact (SHA-256, version, release/asset identity). Missing ownership, unknown versions, manual installs, URL/ETag/timestamp changes, and mutable INI edits never create Update.
- Manually installed files that match the current official hash are shown as Installed / Up to date without adoption. Unmatched manual installs show Installed manually / No action needed.
- Repair requires a concrete missing/invalid Required or RequiredForSelectedMode runtime file, architecture mismatch, broken chain, or unusable configuration. Optional/conditional OptiScaler support files, documentation, setup scripts, and stale alternate proxy ownership paths do not create Repair when another accepted active proxy is healthy.
- Remove actions delete only ownership-proven managed files for the selected component. Removing ReShade while RenoDX remains requires explicit confirmation because RenoDX depends on ReShade.
- RenoDX shows optional Linux Proton HDR launch-option guidance (`PROTON_ENABLE_WAYLAND=1 DXVK_HDR=1`) separately from the required `WINEDLLOVERRIDES` value. Required recommendations never include unrelated Steam arguments such as `gamemoderun`, `LD_PRELOAD`, MangoHud, or AMD/performance tuning variables. Detected user arguments remain visible in Steam launch options and are preserved when merging managed overrides.
