# RHI Linux

RHI Linux is an unofficial Linux fork of [RankFTW/RHI](https://github.com/RankFTW/RHI) for Steam games running through Proton.

It finds installed games and helps set up ReShade, RenoDX, and OptiScaler using files downloaded from their official sources.

## Project status

**Version 0.3.0 - Alpha**

Version 0.3.0 improves Steam library scanning, startup time, Linux diagnostics, and the desktop interface. The project is still in alpha, so some games may need manual setup and installation bugs are still possible.

The supported OptiScaler setup is intended for AMD GPUs. NVIDIA-specific features are not supported.

RHI Linux is not affiliated with the maintainers of RHI, RenoDX, ReShade, or OptiScaler.

> [!WARNING]
> Use these mods only with single-player games. ReShade, RenoDX, and OptiScaler inject code at runtime and may trigger anti-cheat systems.
>
> Check the installation plan before applying it, and keep backups of any game files you care about.

## Quick start

### Run from source on Arch Linux

```sh
sudo pacman -S --needed git dotnet-sdk-8.0

git clone --branch linux --single-branch \
  https://github.com/winterofhell/rhi-linux.git

cd rhi-linux

dotnet run \
  --project src/RhiLinux.Gui/RhiLinux.Gui.csproj \
  -c Release
```

After installing a mod setup, copy the launch option shown by RHI Linux into the game's Steam launch options.

## AppImage

If an AppImage is included with version 0.3.0, download `RHI-Linux-0.3.0-x86_64.AppImage` from the [releases page](https://github.com/winterofhell/rhi-linux/releases), then make it executable:

```sh
chmod +x RHI-Linux-0.3.0-x86_64.AppImage
./RHI-Linux-0.3.0-x86_64.AppImage
```

Not every release includes an AppImage.

## Requirements

- Linux x86_64
- .NET 8 SDK when building from source
- Native or Flatpak Steam
- A Windows game installed through Steam and run with Proton
- An AMD GPU for the supported OptiScaler setup

On Arch Linux, install the SDK with `sudo pacman -S dotnet-sdk-8.0`. An up-to-date Plasma or GNOME installation should already provide the desktop libraries needed by Avalonia.

## Build and test

```sh
dotnet restore RhiLinux.sln
dotnet build RhiLinux.sln -c Release --no-restore
dotnet test RhiLinux.sln -c Release --no-build
```

## CLI

```sh
dotnet run --project src/RhiLinux.Cli -- scan
dotnet run --project src/RhiLinux.Cli -- games
dotnet run --project src/RhiLinux.Cli -- candidates --game 123456
dotnet run --project src/RhiLinux.Cli -- status --game 123456
dotnet run --project src/RhiLinux.Cli -- profile --game 123456
dotnet run --project src/RhiLinux.Cli -- proxy-diagnostics --game 123456
dotnet run --project src/RhiLinux.Cli -- artifacts --game 123456
dotnet run --project src/RhiLinux.Cli -- stack-status --game 123456
dotnet run --project src/RhiLinux.Cli -- diagnostics --game 123456 --offline
dotnet run --project src/RhiLinux.Cli -- diagnostics --game 123456 --download
dotnet run --project src/RhiLinux.Cli -- plan --game 123456 --recommended
dotnet run --project src/RhiLinux.Cli -- cache verify
dotnet run --project src/RhiLinux.Cli -- plan --game 123456 --component renodx --source /path/to/original.addon64
dotnet run --project src/RhiLinux.Cli -- install --game 123456 --component renodx --source /path/to/original.addon64 --apply --yes
dotnet run --project src/RhiLinux.Cli -- remove --game 123456 --component renodx
dotnet run --project src/RhiLinux.Cli -- launch-option --game 123456
```

Use `--steam-root <path>` for a nonstandard Steam installation or test fixture. Add `--json` for machine-readable output.

The `diagnostics` command does not change game files or application state. Its `--download` option allows verified files from official sources to be added to the shared cache. Commands that modify files stay in dry-run mode unless both `--apply` and `--yes` are present. Games with anti-cheat markers also require `--allow-anti-cheat`.

To save a different executable or deployment directory, run `candidates --game <game> --exe <path> [--deployment-dir <path>]`. Both paths must stay inside the game installation.

## Desktop application

```sh
dotnet run --project src/RhiLinux.Gui
```

The desktop app has a searchable game list and a summary of the selected game's support and installation state. For supported games, **Install recommended setup** downloads and verifies the required files, then shows the full plan before anything is changed. More detailed profile, file, cache, and executable information is available under **Advanced details**.

Update checks run in the background after startup and can also be started with **Check for updates**. Local files can be selected from **Advanced troubleshooting > Use local artifact**.

Install, update, repair, and remove actions always show a confirmation first. If an operation fails, RHI Linux rolls back the changes it already made. Cache verification and cleanup are available in Settings.

Use `--steam-root <path>` one or more times to limit scanning to specific Steam installations. Press `F5` to refresh, `Ctrl+L` to focus the search box, or `Ctrl+Shift+R` to review a restore plan for the selected game.

## Steam launch option

For a `dxgi.dll` proxy, RHI Linux generates this launch option:

```text
WINEDLLOVERRIDES="dxgi=n,b" %command%
```

The DLL name changes with the selected proxy. Copy the generated value into the game's Steam launch options.

## Components and downloads

- ReShade with full add-on support hosts RenoDX.
- RenoDX add-ons keep their original `.addon64` or `.addon32` names.
- OptiScaler can load alongside a managed ReShade proxy through `ReShade64.dll`.
- OptiScaler keeps its upstream bundle layout. Only the main DLL is renamed to the selected proxy name.
- OptiPatcher is not installed or downloaded. RHI Linux can remove an older managed copy after checking ownership.

Third-party binaries are not stored in this repository or included in its packages. RHI Linux downloads them over HTTPS from official upstream hosts, checks published SHA-256 hashes when available, validates archives and PE files, and moves valid files into the cache atomically.

Reviewed AppID profiles and clear matches from official RenoDX or RHI metadata are handled automatically. Verified Unity and modern Unreal games may use an architecture-matched generic fallback. RHI Linux does not guess for legacy Unreal games, unknown engines, ambiguous layouts, native Linux games, or games marked as using anti-cheat.

## Data locations

RHI Linux follows the XDG base directory specification:

- configuration: `$XDG_CONFIG_HOME/rhi-linux` or `~/.config/rhi-linux`
- data: `$XDG_DATA_HOME/rhi-linux` or `~/.local/share/rhi-linux`
- cache: `$XDG_CACHE_HOME/rhi-linux` or `~/.cache/rhi-linux`

The cache stores files by SHA-256 hash, so identical ReShade, RenoDX, and OptiScaler files are kept only once. Its default size limit is 5 GiB and can be changed in Settings. For modified games, RHI Linux stores its manifest and transaction backups in the game's `.rhi-linux` directory.

## License

RHI Linux is licensed under the [GNU General Public License v3.0](LICENSE). Upstream attribution is listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). No third-party mod binaries are redistributed.
