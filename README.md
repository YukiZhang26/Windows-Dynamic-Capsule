# Windows Dynamic Capsule

**English** | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md)

Windows Dynamic Capsule is an open-source desktop status capsule for
Windows 10 and 11. It provides a compact, always-available surface at the top
of the screen for media playback, synchronized lyrics, Windows notifications,
local task progress, downloads, countdown timers, and stopwatches. It also
supports full-screen hiding, a manual top tuck-away mode, and privacy controls.

> This project is currently in preview. Its interface, configuration format,
> and system integrations may continue to change.

## Features

- A non-activating, always-on-top WPF capsule window;
- System media sessions, artwork, playback controls, and seekable progress;
- Line-synchronized lyrics through LRCLIB, with an optional QQ Music fallback
  and a 30-day local cache;
- User-authorized Windows notification summaries with an independent Do Not
  Disturb mode;
- Local tasks, browser downloads, countdown timers, stopwatches, and Windows
  Clock status;
- Primary and secondary multi-task regions, multi-monitor support, high-DPI
  support, full-screen hiding, and top tuck-away mode;
- A current-user-only Named Pipe interface for task events.

## Requirements

- Windows 10 version 2004 (build 19041) or later;
- .NET SDK `10.0.302`;
- PowerShell 5.1 or later.

## Build locally

```powershell
dotnet restore .\DynamicCapsule.slnx
dotnet build .\DynamicCapsule.slnx -c Release
dotnet run --project .\src\DynamicCapsule\DynamicCapsule.csproj
```

Run the core verification probe:

```powershell
dotnet run --project .\tests\DynamicCapsule.CoreProbe\DynamicCapsule.CoreProbe.csproj -c Release
```

## MSIX and Microsoft Store

The repository includes a Partner Center identity template, but it does not
publish the maintainer's local submission configuration, signing private keys,
or certificate passwords. To prepare a package with your own Store identity:

```powershell
Copy-Item `
  .\packaging\store-submission.template.json `
  .\packaging\store-submission.json
```

Replace the placeholders in the local file with your own Partner Center
identity. The file is excluded by `.gitignore`. See
[packaging/STORE_SUBMISSION.md](packaging/STORE_SUBMISSION.md) for the complete
workflow.

The Microsoft Store product identity associated with this repository remains
under the maintainer's control. Forking, modifying, or rebuilding the source
does not grant permission to update that Store product. Third-party releases
must use their own app name, package identity, publisher, and signing material.

## Privacy and security

See [PRIVACY.en.md](PRIVACY.en.md) for how notifications, local tasks, and settings
are handled. Report security issues privately by following
[SECURITY.md](SECURITY.md). Public screenshots and sample data must not contain
private notifications, access tokens, or unauthorized lyrics, album artwork,
or wallpapers.

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) before submitting a fix or feature.
The complete product scope, interaction rules, and technical design are in
[PROJECT.md](PROJECT.md).

## License

The source code is available under the [MIT License](LICENSE). Windows Dynamic
Capsule is an independent community project and is not sponsored, endorsed by,
or affiliated with Microsoft or Apple. Third-party services, trademarks, and
content remain subject to their respective terms.
