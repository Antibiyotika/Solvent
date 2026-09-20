# Solvent

Solvent is a native Windows desktop utility (WPF / .NET 8) for diagnosing and fixing common Windows 10/11 problems, cleaning up disk space, and squeezing out extra performance — all from one Fluent-styled app.

## Features

- **Dashboard** — one-click "run all" health check with a live resource monitor (CPU, disk, memory).
- **Repair & Security** — Windows Update checks, malware scan trigger, memory diagnostics, driver update checks, printer repair, DNS/network fixes, and enabling Windows Defender.
- **Cleanup** — temp file, cache, and duplicate-file cleanup (with Recycle Bin support instead of permanent delete).
- **Performance** — startup impact boosting and general performance tuning.
- **Diagnostics** — system info, disk health (S.M.A.R.T./WMI), event log inspection, battery report, restore point management.
- **Startup Manager** — enable/disable startup programs.
- **Schedule Manager** — automate maintenance tasks on a schedule.
- **Settings** — dark/light theme with a live accent color picker, and full English/Turkish localization.

## Tech stack

- .NET 8 / WPF, MVVM via [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
- [WPF-UI](https://github.com/lepoco/wpfui) for the Fluent Design shell (NavigationView, Mica/Acrylic, SymbolIcon)
- `System.Management` for WMI/S.M.A.R.T. queries, `System.Diagnostics.PerformanceCounter` for live CPU stats
- Minimal DI via `Microsoft.Extensions.DependencyInjection`

## Requirements

- Windows 10 or 11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
- Administrator rights at runtime (required for Defender control, SFC/DISM, scheduled tasks, etc.)

## Build & run

```powershell
dotnet build
dotnet run
```

## Publish as a single .exe

```powershell
dotnet publish -r win-x64 -c Release --self-contained true
```

Output: `bin\Release\net8.0-windows\win-x64\publish\Solvent.exe` — a single self-contained executable, no separate .NET install needed on the target machine.

## Releases and auto-update

Pushing a tag like `v1.2.2` runs `.github/workflows/release.yml`, which builds `Solvent.exe`, computes its SHA-256 and attaches both `Solvent.exe` and `Solvent.exe.sha256` to the GitHub Release.

On startup Solvent checks the latest release and, if you agree, downloads the new exe. It installs it **only if the download's SHA-256 matches `Solvent.exe.sha256`** from the same release; a release without that file, or a mismatch, is ignored/discarded. Installing Solvent in a folder only administrators can write to (e.g. `Program Files`) gives the strongest protection.

## License

MIT — see [LICENSE](LICENSE).
