# ConnectionClue

ConnectionClue is a Windows desktop tool for investigating connection problems with measurements from this PC. It shows what it tested, what it observed, and practical next steps; it does not claim an ISP or device is at fault without supporting evidence. It changes a Windows setting only when you ask: the one-click DNS switch, which Windows confirms with an administrator prompt and which can be undone.

> **Preview:** Network findings are limited to the endpoints and measurements configured in the current build. Results are diagnostic evidence, not a guarantee of service quality or a provider fault determination.

## Features

- Quick and repeated checks for connectivity and latency, with an explicit warning and no new checks starting when Windows reports no connection. One copy runs at a time; starting it again brings the running window back.
- A disconnection warning is visible on every page and produces one desktop notification per episode. After reconnecting, one 10-second connectivity-only check runs once the app is idle, without speed tests, extra service probes or online advice review; a notification and an all-page notice report its result (no problems found, a problem, performance below the limits, or not enough data). Saved recurring-check settings are unchanged.
- Background checks notify every result with a connection problem or performance below the limits, saying when a continuing problem was first seen, and report recovery once. Notifications from automatic checks appear even while the window is open; results of checks the user started notify only when the window is in the background.
- Precise findings from the spec's verdict rules (R01–R05, R07–R11) in plain sentences with local times: link drops ("Wi-Fi link dropped at 21:04:12"), DNS failing while direct connections work, whether delay starts before or beyond the router, secure-web failures by category, and "can't conclude" with what's missing. R06 needs a second independent test service.
- Real services per "What's happening?" choice, tested after each quick check: game platforms, Teams/Zoom/Meet relays, streaming services, or connectivity checks, plus an optional user host:port (TCP connect time only).
- Scheduled background checks are enabled by default on Wi-Fi and Ethernet, run at the configured interval (3 minutes to 8 hours, 5 minutes by default) for the configured background check length (10–60 seconds, 10 by default) only while the app is running, and can be disabled in Settings. On mobile networks (cellular and Windows-metered connections such as phone hotspots) they are off by default behind a separate opt-in, and resume automatically on Wi-Fi or Ethernet.
- Lag-event markers that correlate a reported symptom with measurements; findings at a marker are listed first.
- Two ways to check, separated on screen: Quick check (10–60 seconds of baseline measurement, 30 by default), or Capture longer for 15, 30 or 45 minutes or 1, 2, 4 or 8 hours (15 minutes by default). Settings shows how long a quick check really takes with the speed test.
- Export (PDF) and Export (MHTML) after a quick check or longer capture: a multi-page PDF through Microsoft Print to PDF, or an offline single-file web archive with embedded PNG visuals. Both contain check results and measurement tables, with counted sampling limits for long captures.
- Each symptom's extra service starts with a default (Riot Games, Prime Video, Discord, Microsoft) that users can replace or clear; Insights history can be cleared from Settings.
- Download/upload and loaded-latency measurement is enabled by default for user-started checks, can be disabled in Settings, uses up to 300 MB, and is skipped on metered connections. Bufferbloat is graded A–F with router SQM/QoS steps.
- Background checks also measure download and upload speed with a light sample about once an hour (a quick-check speed test counts): one connection that stops as soon as the speed is steady, about 5 MB per direction at 100 Mbps and never more than 3 seconds or 20 MB down and 10 MB up. The sample waits while other apps use the connection, never runs on metered or mobile connections and follows the same Settings switch. A connection too fast to settle within those limits gets a lower bound ("≥"), kept in history but out of averages, and that direction is sampled again only every 6 hours.
- Local check history with a daily quality score (0–100), speed compared with the entered plan, and the worst hours; gaps between checks are treated as unobserved.
- Insights tools: a hop view that names the hop adding lasting delay or loss, a DNS comparison of the current resolver against Cloudflare, Google and Quad9 with a one-click switch and restore, and a Wi-Fi channel analyzer with the connected channel, crowding and band/channel advice.
- A report for your provider: accessible HTML or print-to-PDF with the findings at local times, measurements and a sampled timeline, with network identifiers omitted.
- Convenience: start with Windows (opt-in), F5 and Ctrl+L, Quick check in the taskbar jump list and the notification-area menu, Store auto-update and winget manifests generated by the release script.
- Action-oriented recommendations that identify the observed evidence and safe steps to try. Configuration advice is read-only.
- Accessible interface with keyboard support, screen-reader names and help text, tooltips, live announcements, Dark (default), Light, System, two high-contrast themes, and Windows High Contrast support.
- Compact Settings tabs group Checks, Network and Preferences without a long scrolling page. The in-app update-check section is removed; Store and winget management remain outside the app.
- UI resources cover 20 languages; non-English translations need professional review before a production launch. Copy for findings, services, diagnostics, reports and other new features intentionally falls back to English until reviewed. The bundled guide is English.
- Online review of generic recommendation text is enabled by default and can be disabled in Settings. Its request body excludes check results, device names, local addresses, and measurements; public services still receive normal HTTPS metadata, including your public IP. Review is skipped when offline.

For usage details, privacy boundaries, keyboard shortcuts, and diagnostic limitations, see [helpme.md](./helpme.md). The design rationale, technical contracts, release process, and known limitations are in [ConnectionClue-Developer-Handoff.md](./ConnectionClue-Developer-Handoff.md).

## Requirements

- Windows 11, build 26100 or newer; x64 and ARM64 are the release architectures.
- .NET SDK 10.0.401, selected by `global.json`.
- Python 3 to validate the SQLite schema.
- PowerShell 7 to generate icons and build release packages.

## Build and test

From the repository root on Windows:

```powershell
python tools/validate_schema.py
dotnet build ConnectionClue.slnx -c Release
dotnet test --solution ConnectionClue.slnx -c Release
```

Regenerate the Windows icons, MSIX visual assets, toolbar mark, and the nine PNG brand images with:

```powershell
pwsh tools/generate-icons.ps1
```

The nine requested PNGs are written to `logos\`: BoxArt 1080×1080; Logo 44×44, 71×71, 150×150, 300×300, 512×512, 1024×1024, and 1920×1080; Poster 720×1080. The script also creates the app and tray ICO files and the MSIX tile assets used by the build.

## Release packages

Build the x64 and ARM64 MSI installers and combined MSIX bundle with:

```powershell
pwsh tools/build-release.ps1 -Version 1.0.8
```

Packages are written to `releases\1.0.8\`, with winget manifests under `releases\1.0.8\winget\`. Publish the GitHub release `v1.0.8` with the MSI files before submitting the manifests to microsoft/winget-pkgs, and set `-WingetLicense` to the project's license.

The MSIX bundle uses the reserved Microsoft Store identity by default (`BulentOzkir.ConnectionClue`, publisher `CN=06D08AF4-6BB1-40DF-9B96-5DF27BEE0635`, publisher display name `Bulent Ozkir`), so it can be uploaded to Partner Center as is; the Store replaces its signature. For sideloading, it is signed with a self-signed test certificate for that publisher (`ConnectionClue-msix-test-signing.cer`). The MSIs are signed with the self-signed `CN=ConnectionClue Test` certificate (`ConnectionClue-test-signing.cer`) unless `-CertificateThumbprint` names an approved code-signing certificate, which MSI distribution requires because the Store never signs MSIs. Test-signed packages install only where their certificate is trusted. See the developer handoff for signing and release-validation requirements.

The MSI is per-machine and requires Windows Installer elevation; the installed app itself runs as a standard user. MSIX startup is opt-in and managed by Windows.

The MSIX manifest declares Hausa as `ha-Latn-NG`, while the existing .NET translations remain under `ha`. Windows package registration rejects bare `ha` with `0x80073CF6` / `0x80070057`, even when `makeappx` accepts the package. Before Store submission, test installation of the actual bundle on a supported Windows version; a successful build or unpack is not an installation test.

## Privacy and safety

- Check history is local to the Windows user and retained for a bounded period.
- ConnectionClue does not silently edit operating-system, adapter, router, or driver configuration. The DNS switch runs only when selected, after the Windows administrator prompt, and Restore automatic DNS undoes it.
- A disconnected system is shown with a warning and network checks are gated; offline recommendation review is skipped.
- Windows startup is off by default. MSI installs use an opt-in per-user startup entry; MSIX startup is controlled by Windows.
- Review exported files before sharing them. A report can still disclose information included in its visible measurements or notes.
