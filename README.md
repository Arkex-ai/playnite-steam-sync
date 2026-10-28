# Playnite Steam Sync

Playnite Steam Sync is a community Playnite extension for syncing eligible games and artwork from your Playnite library to Steam non-Steam shortcuts. Keep Playnite as your game library, then launch supported PC, storefront, and emulator games from Steam Big Picture with Steam overlay and Steam Input support.

This is an independent development fork of [hikaps/non-steam-sync](https://github.com/hikaps/non-steam-sync). It is not affiliated with or endorsed by Playnite or Valve.

## Automatic Playnite to Steam sync

The extension starts with one filtered export. After that baseline, it uses Playnite library events and a persistent background queue to add eligible new games and refresh artwork for games it already tracks. A clean Playnite launch does not trigger a full library scan.

You can filter by Playnite source, installed state, emulator actions, and URL actions. The initial export and later reconciliation show which games qualify before changing Steam shortcuts.

## Features

- **Automatic game sync:** new eligible Playnite games are queued after the initial filtered export.
- **Automatic artwork sync:** covers, posters, icons, logos, and backgrounds are checked when Playnite updates a tracked game.
- **Idempotent background queue:** snapshots are coalesced, sequenced, persisted, retried, and recovered after a Playnite restart.
- **Steam shortcut safety:** artwork is staged and verified by SHA-256; existing files are backed up before atomic replacement.
- **Safe shortcuts.vdf updates:** the extension validates the VDF and waits for Steam to close before changing shortcut fields.
- **Big Picture launch support:** supported shortcuts launch through Steam; Steam Input layouts and emulator controller mappings remain user-managed.
- **Conservative failure handling:** ambiguous launch actions go to NeedsAttention; the automatic worker does not open dialogs or choose a generic launcher.

Removing a game from Playnite does not automatically remove its Steam shortcut. The extension does not install games, transfer purchases, or configure controller mappings.

## How synchronization works

1. Choose Steam profile, Playnite sources, and eligibility filters.
2. Run the initial export and confirm the filtered game count. A successful export establishes the baseline.
3. Enable automatic Playnite-to-Steam sync.
4. Playnite AddedItems events queue new eligible games. ItemUpdated events refresh tracked game data and artwork.
5. A serial worker processes the durable queue. Repeated identical snapshots do not produce repeated writes.

Artwork files can be copied while Steam is open. If a shortcut field in shortcuts.vdf must change, that write waits until Steam exits normally. The extension does not force-close Steam.

## Validation status

The public `main` branch contains the idempotent event-sync implementation. It has passed 318 automated tests and a Release build. Live validation used Playnite 10.63 and a Steam installation:

- 48 filtered shortcuts were present after the initial export.
- All 240 artwork targets for those shortcuts matched their Playnite source files by SHA-256.
- The 48 VDF icon paths pointed to the prepared Steam grid files; executable paths, working directories, and launch arguments matched the pre-artwork snapshot.
- A controlled Playnite LibraryPlugin test exercised the real import event and asynchronous worker, then launched a harmless test shortcut through Steam Big Picture.

The live test has not yet covered a new game imported by a commercial storefront connector, extended background stability, a Windows reboot, or a prolonged visual review in Big Picture. See the [validation report](docs/VALIDATION_REPORT.md).

## Build and test

Run these commands from the repository root:

- dotnet test tests/ShortcutsTests/ShortcutsTests.csproj
- dotnet build src/SteamShortcutsImporter/SteamShortcutsImporter.csproj --configuration Release

Creating a .pext package also requires the compatible Playnite Toolbox. Run tools/pack.ps1 -Configuration Release.

## Development and distribution status

The public GitHub fork is linked to its upstream, and its `main` branch contains the incremental event-sync implementation. No installer, `.pext` package, or release is available.

As of 2026-10-28, the upstream repository does not declare a license. GitHub permits viewing and forking through its service, but that does not establish a general license to redistribute a `.pext` package or release outside GitHub. See [development notes](docs/DEVELOPMENT.md).

## Documentation

- [User guide](docs/USER_GUIDE.md)
- [Product specification](docs/PRODUCT_SPEC.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Development and testing](docs/DEVELOPMENT.md)
- [Validation report](docs/VALIDATION_REPORT.md)
- [Roadmap](docs/ROADMAP.md)

## Resumo em português

Extensão comunitária do Playnite para sincronizar jogos elegíveis e capas, ícones e fundos com os atalhos não-Steam da biblioteca Steam. Após uma exportação inicial filtrada, uma fila assíncrona e idempotente processa novos jogos e atualizações de arte recebidas pelos eventos do Playnite. O projeto está em desenvolvimento; ainda não há instalador ou release público.
