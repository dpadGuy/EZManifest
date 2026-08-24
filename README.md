# EZManifest

<p align="center">
  <img src="EZManifest/Assets/EZManifestLogo.png" alt="EZManifest" width="360" />
</p>

Windows desktop app for importing Steam depot manifests, downloading game files from Steam CDN, and managing a local library.

## Features

- **Library** — browse installed/imported games with cover art
- **Downloads** — import a manifest `.zip`, pick depots, download with pause/cancel
- **CDN region** — choose a Steam content cell (or Auto) in Settings
- **Install path** — set a default download/install root
- **Play** — launch a saved executable with the game folder as working directory
- **Context menu** — open install folder, patch with Goldberg, uninstall
- **Theme** — light / dark

## Technologies

- [.NET 8](https://dotnet.microsoft.com/) (`net8.0-windows10.0.19041.0`)
- [WinUI 3](https://learn.microsoft.com/windows/apps/winui/winui3/) / [Windows App SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/) 2.4
- [SteamKit2](https://github.com/SteamRE/SteamKit) 3.4 — depot manifests & CDN chunk processing
- [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm) 8.4
- [Microsoft.Extensions.DependencyInjection](https://learn.microsoft.com/dotnet/core/extensions/dependency-injection) 9.0
- C# / XAML (unpackaged Win32 desktop app)

## Requirements

- Windows 10 version 1903+ (build 18362+) recommended; targets `net8.0-windows10.0.19041.0`
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
- Visual Studio 2022 with **Windows application development** workload (WinUI) recommended

## Build

```bash
dotnet build EZManifest\EZManifest.csproj -c Debug -p:Platform=x64
```

Open `EZManifest.slnx` in Visual Studio and run (x64).

## Publish

From the repo root:

```bat
publish.bat
```

Or:

```bash
dotnet publish EZManifest\EZManifest.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:WindowsAppSDKSelfContained=true -p:WindowsPackageType=None -o publish
```

Output: `publish\EZManifest.exe` (self-contained single-file).

Runtime data is created next to the exe when needed:

| Path | Purpose |
|------|---------|
| `settings.json` | Download path + CDN cell |
| `items.json` | Library entries |
| `Manifests\` | Extracted manifest archives |
| `Goldberg\` | Cached Goldberg emulator files (on first patch) |

## Usage

1. Set an install location in **Settings** (prompted on first launch if missing).
2. Open **Downloads**, browse to a manifest `.zip`.
3. Select depots that have local `depotId_manifestId.manifest` files and a matching key in the `.lua`.
4. Download; the game appears in **Library**.
5. **Play** picks an `.exe` the first time and remembers it.

Depot list is driven by **on-disk `.manifest` files** and keys from `addappid(...)` in the lua — `setManifestid(...)` is ignored.

## Project layout

```
EZManifest/
  EZManifest.slnx
  publish.bat
  EZManifest/
    EZManifest.csproj
    Views/Pages/     Library, Downloads, Settings
    Services/        Download engine, Steam metadata, settings, …
    Models/
```
