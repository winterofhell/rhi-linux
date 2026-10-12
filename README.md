# RHI Linux

Unofficial Linux port of [RankFTW/RHI](https://github.com/RankFTW/RHI). Sets up ReShade, RenoDX and OptiScaler for Steam games running under Proton.

**0.4.0 · alpha** · AMD GPUs · native and Flatpak Steam

> [!WARNING]
> Single-player games only. These mods inject code at runtime and can trigger anti-cheat. Games with known anti-cheat are flagged and need explicit confirmation.

## Features

- Finds Steam games from every library folder, plus Heroic, Lutris and Bottles
- Detects the game executable, Proton prefix and correct DLL folder, with manual overrides
- Installs, updates, repairs and removes ReShade, RenoDX and OptiScaler
- Runs ReShade and OptiScaler side by side (`ReShade64.dll` + `LoadReshade=true`)
- Extras: ReLimiter, Display Commander, REFramework, OptiPatcher and 49 ReShade shader packs
- Generates the `WINEDLLOVERRIDES` Steam launch option for you
- Shows the full change plan before touching anything, backs up replaced files and rolls back on failure

## Install

**AppImage** — grab it from [releases](https://github.com/winterofhell/rhi-linux/releases):

```sh
chmod +x RHI-Linux-0.4.0-x86_64.AppImage
./RHI-Linux-0.4.0-x86_64.AppImage
```

**From source** (Arch):

```sh
sudo pacman -S --needed git dotnet-sdk-8.0
git clone --branch linux https://github.com/winterofhell/rhi-linux.git
cd rhi-linux
dotnet run --project src/RhiLinux.Gui -c Release
```

After installing a mod, paste the launch option shown in the app into the game's Steam properties, for example:

```text
WINEDLLOVERRIDES="dxgi=n,b" %command%
```

## CLI

```sh
alias rhi='./RHI-Linux-0.4.0-x86_64.AppImage --cli'   # or: dotnet run --project src/RhiLinux.Cli --
rhi games                                         # list detected games
rhi status --game 1091500                         # installed components
rhi plan --game 1091500 --recommended             # preview the recommended setup
rhi install --game 1091500 --component renodx --source file.addon64 --apply --yes
rhi enhance --game 1091500 --shaders Lilium,CrosireMaster
rhi shader-packs                                  # shader pack catalog
rhi launch-option --game 1091500
rhi doctor
```

Commands that change files only print the plan unless you pass `--apply --yes`. Run `help` for everything else. Useful flags: `--steam-root PATH`, `--json`, `--allow-anti-cheat`.

## Shortcuts

`Ctrl+L` search · `F5` rescan · `Ctrl+R` restore plan for the selected game

## Where things go

| What | Path |
| --- | --- |
| Config | `~/.config/rhi-linux` |
| Data | `~/.local/share/rhi-linux` |
| Download cache | `~/.cache/rhi-linux` (5 GiB limit, adjustable) |
| Per-game backups and manifest | `<game>/.rhi-linux` |

`XDG_*_HOME` overrides are respected.

## Downloads and safety

Nothing third-party ships with this project. Mods are downloaded over HTTPS from their official upstream sources. Published SHA-256 hashes are checked, archives and PE files are validated, and unknown DLLs are never overwritten. Downloaded Windows binaries are never executed.

## Build and test

```sh
dotnet build RhiLinux.sln -c Release
dotnet test RhiLinux.sln -c Release
```

## License

GPL-3.0. See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Not affiliated with RHI, RenoDX, ReShade or OptiScaler.
