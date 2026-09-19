# Solvent

Solvent is a Windows system-maintenance utility built with WPF and [WPF-UI](https://github.com/lepoco/wpfui) (Fluent Design). It brings health checks, cleanup, startup management, performance tuning, and scheduled maintenance into a single Fluent-styled app, in English and Turkish.

## Features

- **Dashboard** — at-a-glance system health and a one-click "Run All" for the routine maintenance tasks.
- **Health Check** — surfaces issues by severity (info / good / warning / critical).
- **Repair & Security** — common Windows repair and security actions.
- **Performance** — resource monitor and performance-boost actions.
- **Cleanup** — disk cleanup, including duplicate-file detection (moves to Recycle Bin, not permanent delete).
- **Startup Programs** — manage what launches at logon, with signature/risk indicators.
- **Schedule** — recurring maintenance on a schedule.
- **Settings** — theme (Light / Dark / follows Windows), language (EN / TR), launch-at-startup, run-in-background with tray icon.

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows desktop workload)
- Administrator privileges — the app's manifest requests `requireAdministrator`, since several maintenance/repair actions need elevation. Expect a UAC prompt on launch.

## Building & running

```powershell
dotnet build
dotnet run --project SolventUI.csproj
```

### Publishing a single-file executable

```powershell
dotnet publish -r win-x64 -c Release
```

This bundles the .NET 8 + WPF runtime into one `.exe` (see the `RuntimeIdentifier`-conditioned properties in the `.csproj`) — no separate runtime install needed on the target machine. Trimming is intentionally left off, since WPF's reflection-heavy XAML loading (resource dictionaries, data templates, converters resolved by type name) doesn't trim safely.

## Project structure

```
SolventUI/
├── Views/           # Pages + user controls (XAML + code-behind)
├── Services/         # System/maintenance/cleanup/performance/tray logic
├── Models/           # Plain data models and enums
├── Converters/        # WPF value converters
├── Themes/           # Colors, animations, and EN/TR string resources
└── app.manifest       # requireAdministrator execution level
```

## Contributing

Issues and pull requests are welcome. Please keep changes focused and include a short description of what changed and why.

## License

MIT — see [LICENSE](LICENSE).
