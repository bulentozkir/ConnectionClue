# ConnectionClue — Developer Handoff

This is the product and delivery contract; §19 distinguishes implemented behavior from open validation and release work. ConnectionClue is a **Windows 11-only** home-connection troubleshooter. A $0.99 one-time Store price is proposed, not a published offer. There is no account, subscription, app analytics, or user-data backend. Online recommendation review is enabled by default; its request body contains generic advice only, while providers receive normal HTTPS metadata (see §12).

**Goal:** capture a reported problem and localize it with measured evidence: between this PC and the router, beyond the router, in name resolution, or in a single service. Then guide one safe change, compare before and after, and export a readable report.

Defaults are proposed policy pending lab calibration. Ambiguous or unavailable outcomes stay visible: the app never invents a fault. Rationale for key decisions: §20.

## 1. Scope

Workflow: **symptom → capture → finding → one change → compare → export.**

- **Symptoms:** gaming lag, buffering video, choppy calls, disconnections. Ask whether the problem happens on this PC. If it happens on another device, state that measurements cover this PC only, and advise capturing from the same connection type and location.
- **v1 includes:**
  - Configurable 10–60 s quick checks (30 s default) and 10–60 s background checks (10 s default, every 3 min–8 h, 5 min default); optional 15/30/45-minute and 1/2/4/8-hour foreground captures. Multi-day monitoring uses sampled background summaries (≤30 days); gaps are unobserved.
  - Link, route and Wi-Fi events; ICMP, TCP, system DNS and small HTTPS probes.
  - Marker ("It lagged just now") and a timeline with gaps and markers.
  - Evidence cards, each with one next action.
  - User-confirmed before/after comparison.
  - Local history and action checklist.
  - Redacted HTML, text and CSV export.
  - UI in 20 languages (§14).
  - Background checks from the notification area, on by default, alerting against the user's own limits (§4, §12). Optional start-at-logon is off by default; MSIX delegates it to Windows, while MSI uses a per-user Run entry.
  - Speed test in manual quick checks: download and upload throughput plus latency under load (§6). Never in background checks or on metered connections.
- **Not in v1:** unattended speed tests, packet capture, overlays/injection, auto-repair, DNS/router/VPN/driver changes, Windows service or Task Scheduler job, remote agents, LLM-generated diagnoses, cloud sync, payment backend, in-app purchases.
- **Wording:** no universal score, no "ISP guilty" label, no promised lag reduction. Localization ("the delay appears beyond your router") is allowed; blame is not.

## 2. Platform and stack

- **OS/CPU:** Windows 11 24H2+ (10.0.26100); x64 + arm64 in one MSIX bundle. This floor gives a single Wi-Fi consent model (introduced in build 25976 [1]). Confirm it against the servicing calendar at kickoff.
- **Runtime/build:** .NET 10 LTS [2], C#, WPF. SDK pinned in global.json. Central package versions with lock files and RestoreLockedMode in CI. SBOM plus a vulnerable-package scan.
- **TFMs:**
  - Windows projects: `net10.0-windows10.0.26100.0`, with SupportedOSPlatformVersion and manifest MinVersion both 10.0.26100.0.
  - Platform-neutral projects (Core, Capture, Analysis, Storage, Reporting): `net10.0`. This is a compile-time boundary that keeps Windows APIs out of domain logic, not a portability goal.
- **Libraries:**
  - Microsoft.Extensions.Hosting (DI, logging, lifetime), CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, xUnit, System.Text.Json source generation.
  - `System.TimeProvider`: GetTimestamp (monotonic), GetUtcNow (anchors), CreateTimer (scheduling). Tests use FakeTimeProvider.
  - CsWin32 for IP Helper, WLAN, DNS and power APIs. Wrap handles in SafeHandles; keep callback delegates rooted for the whole registration.
- **Packaging:** self-contained full-trust MSIX. The `wiFiControl` capability is used only for optional Wi-Fi detail (§7.2). No trimming or Native AOT (not supported for WPF).
- **Store and UI:** the Store enforces entitlement, so there is no license server. The UI is native WPF, with no WebView or localhost server.

## 3. Target architecture and project layout

The layout below is the intended decomposition, not a current-file inventory. The 1.0.7 preview stores settings, the last recommendation result, review verdicts, and sampled history in atomic per-user JSON files; `schema.sql` is validated separately and is not yet the runtime evidence store. See §19 for the implemented project map.

```text
ConnectionClue.slnx            dotnet-buildable projects (packaging excluded, §17)
Directory.Build.props  Directory.Packages.props  global.json
src/ConnectionClue.Core/       Models/, Contracts/ (IProbe, ITargetResolver, IEvidenceWriter, IEvidenceReader,
                               INetworkContextProvider), Endpoints/ (manifest + validator)
src/ConnectionClue.Capture/    SessionController, ProbeScheduler, CoverageTracker, EvidenceChannel
src/ConnectionClue.Windows/    RouteProvider, InterfaceMonitor, WlanMonitor, PowerMonitor, SystemInspector, Probes/
src/ConnectionClue.Analysis/   MetricsCalculator, RuleEngine, Rules/, ComparisonEngine, NextActionPlanner, ConfigurationAdvisor,
                               Policies/rules-v1.json
src/ConnectionClue.Storage/    ConnectionFactory, MigrationRunner, Migrations/001_initial.sql (= schema.sql),
                               SqliteEvidenceStore, RetentionService
src/ConnectionClue.Reporting/  ReportModelBuilder, Redactor, Html/Text/CsvReportWriter, AtomicFileWriter
src/ConnectionClue.Presentation/ ViewModels/, Accessibility/, Localization/, Resources/ (Strings*.resx): net10.0, testable without WPF
src/ConnectionClue.App/        Views/, AutomationPeers/, Resources/ (system-colour styles), CompositionRoot, SingleInstance,
                               TrayController, HotkeyController, SettingsService
config/endpoints.production.json
packaging/msix/                AppxManifest.xml (template), Assets/ (generated)
packaging/msi/                 ConnectionClue.wxs (WiX 5)
tests/                         {Core,Capture,Analysis,Storage,Reporting,Presentation}.Tests, Windows.IntegrationTests, App.UiTests, Fixtures/
tools/validate_schema.py, build-release.ps1, generate-icons.ps1
dotnet-tools.json             pinned WiX 5
docs/                          endpoint-approval, threat-model, diagnosis-language, privacy, release-checklist (.md)
```

Dependencies point inward:

- Core has no UI, Windows or database dependency.
- Capture uses Core contracts; Windows and Storage implement them.
- Analysis is pure. Reporting consumes typed models. Presentation holds platform-neutral view models and accessibility policy. App (WPF views) composes everything.

Windows 11 is the only platform: every build, test and CI job runs on Windows 11. Tests use Microsoft.Testing.Platform, opted in through global.json.

## 4. Process and concurrency

- **Instances:** one normal-user process, per-user single instance (a named mutex; a second launch activates the window). One active capture per user.
- **Close during capture:** with background checks off, closing exits (a running check is cancelled).
- **Background checks (on by default):**
  - **No network at all** (no default route over a connected adapter: Wi-Fi off, cable unplugged, airplane mode): no check starts, manual or background.
    - The status shows an amber badge with a bold "!", "No network connection", and what to do; the tray shows its "!" badge. Screen readers hear it once per change.
    - It updates live on network changes (`NetworkChange` → `NetworkStatus.IsConnected`).
    - Wi-Fi or a cable without internet still counts as connected, because the check then shows where the path breaks.
    - A check already running continues, because a drop is evidence for "Disconnections".
  - Can be turned off in Settings or from the tray menu; an explicit saved choice is kept. Because network activity starts without a click, it is disclosed on the Store listing and privacy page, and in a notification the first time the window hides to the tray.
  - While the app runs, it checks every 3, 5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 240, 360 or 480 min (default 5; labels show whole hours as hours). Intervals are start to start; the 3-minute floor (`RecurringChecks.MinimumInterval`) caps endpoint traffic. Each background check measures for its own length (`BackgroundCheckSeconds`, 10–60 s, default 10), independent of the quick check length.
  - Recurring checks are enabled by default on Wi-Fi and Ethernet. Cellular/WWAN checks are off by default and require the separate saved "Check regularly on mobile networks" opt-in. Manual checks remain available; background checks never run a speed test. Startup checks follow the same cellular opt-in.
  -   There is no Windows service or Task Scheduler job. Start-at-logon is optional and off by default: MSIX uses a disabled Windows startup task that the user may enable; MSI uses a per-user Run entry only after opt-in. When not launched at logon, exiting stops the app and background checks.
  - When enabled, minimizing or closing hides the window to the notification area. The tray menu has Open, Quick check, a background toggle and Exit.
  - Manual and background checks share one engine and never overlap. A tick during a check runs at most once afterwards (`RecurringChecks`).
  - Alerts are Windows notifications (tray balloon → toast) and respect Focus/Do not disturb. `AlertPolicy.Evaluate(report, AlertTrigger)` decides by trigger:
    - **Background:** every Unhealthy or Degraded check alerts. A repeat of the active issue signature (level + issue kinds) is titled "… continues"/"still …" with "First seen at HH:mm"; the first NoIssue afterwards sends one "Back to normal".
    - **Reconnect:** always reports its result (`AlertKind.Reconnected`, titled by level, including Inconclusive) and updates the active issue, so the next background check can say it continues.
    - **Manual:** on change only: a new or different problem, an hourly reminder while it persists, and one "Back to normal".
    - Inconclusive checks never clear an active problem and alert only from the reconnect check. `Alert.Automatic` (disconnection, background, reconnect; not after the user pressed Stop) shows the notification even while the window is active; manual results notify only while it is not. The first notification keeps the tray icon visible for the session, because removing the icon removes its notifications.
    - A notification names the issue and "What to try: …" (the top actions), then "Worth checking: …" (the top Important best practice). Best-practice advice alone never alerts. Clicking it, or reopening the window, shows the Recommendations page.
  - The tray icon shows the latest result as a badge whose shape differs as well as its colour (! = below your limits, × = problem). The tooltip gives the time and result.
- **Exit order:** stop scheduling → cancel probes → flush writes (≤5 s) → release callbacks, hotkey, tray and connections. Never unregister a native notification (CancelMibChangeNotify2, WlanCloseHandle, power) on its own callback thread.

```text
WPF commands → SessionController → ProbeScheduler + event monitors
  → bounded EvidenceChannel<CaptureRecord> → single SQLite writer (batched)
  → snapshot reader → metrics/rules → UI snapshots, ReportModel
```

- **CaptureRecord:** ProbeObservation | ConnectionEvent | CoverageGap | PathSegment start/end | SessionCapability | SymptomMarker | ClockAnchor | state change.
- **Sizing:** ~7 records/s (~25k/h). Channel capacity 4096 (~10 min). Commit every 1 s or 250 records.
- **Backpressure:**
  - Full channel → the scheduler stops probing, and CoverageTracker opens a StorageBackpressure gap (persisted once the writer recovers).
  - Writer failure → Failed/StorageFailure; the UI stops claiming it is recording. Committed evidence stays usable.
  - Records are never dropped silently.
- **Threads:** async I/O only; nothing runs on the dispatcher. Native callbacks only enqueue bounded messages.
- **Reads:** readers use separate WAL connections. Analysis and reports each read one transaction snapshot. The live UI shows a labelled provisional preview, updated at most once per second. Graphs are downsampled for display only.
- **Diagnostic log:** structured, rolling (≤3 × 5 MB), with no addresses, names or notes. Never uploaded; the user attaches it to a support request only by choice.

## 5. Session lifecycle and time

Preparing → Capturing ⇄ Suspended → Stopping → terminal state:

| Terminal state | EndReason |
|---|---|
| Completed | Deadline, UserStopped, AppExit, SleptPastDeadline |
| Failed | StorageFailure, InternalError |
| Interrupted | ProcessLost: an active session found at startup. It is never resumed and ends at its last committed record |

- **Stop** → Completed/UserStopped. Evidence is judged by coverage, not by state. Cancelling during Preparing deletes the stub.
- **Preparing** validates duration, manifest, network context and storage.
  - A disconnected adapter is context for a finding, not an app error.
  - An invalid manifest is a build defect: capture refuses to start with a setup error, never an internet finding.

**Time**

- **Canonical time** is the µs offset from session start from TimeProvider.GetTimestamp (QPC). Ordering, durations, the deadline and gap detection use offsets only.
- **UTC is display-only.**
  - Session.StartedUtc anchors offset 0.
  - ClockAnchor rows re-anchor after resume or a wall-clock jump (UTC-vs-monotonic drift >2 s, also logged as a WallClockChanged event).
  - Wall-clock changes never alter the timeline, extend a session, or create gaps.
- **Deadline** = start + planned duration, on the monotonic clock. Sleep consumes it; waking past it → SleptPastDeadline.
- **Gaps:** a heartbeat gap >3 s opens a CoverageGap.
  - Reason Sleep if suspend/resume signals bracket it, otherwise SchedulerDelay. Power notifications can be late or missing, e.g. under Modern Standby.
  - Missed slots are never backfilled as timeouts.
  - ClockDiscontinuity is reserved for monotonic-clock anomalies.
- **Release blocker:** lab-verify that QPC counts across S3 and Modern Standby. If not, use a clock that includes suspend time.

## 6. Probe plan

| Stream | Target | Cadence | Timeout |
|---|---|---|---|
| Gateway ICMP | default gateway, per family | 1 s | 800 ms |
| External ICMP | 2 independent operators × families | 1 s; 15 s while Unavailable | 800 ms |
| External TCP | same operators, approved port, literal IP | 15 s; 1 s while that target's ICMP is Unavailable | 3 s / 800 ms |
| System DNS | 2 names in independent zones × A/AAAA | 15 s | 3 s |
| HTTPS | 2 endpoints, independent operators | 15 s | 5 s |

Preview checks: quick checks 10–60 s (default 30), background checks their own 10–60 s (default 10), the reconnect check 10 s, Capture longer up to 8 h. Cadence scales so every check has enough samples: router ICMP every 1 s (every 5 s during Capture longer), internet TCP every clamp(length/30, 1, 5) s, DNS and HTTPS alternating every clamp(length/5, 5, 15) s.

- **Families:** probe a family only while it has a default route in the segment. The gateway is ICMP-only; a silent gateway means limited visibility (R02).
- **Budgets:**
  - Fast pool (1 s streams) ≤6 concurrent; slow pool (15 s streams) ≤4. A stream moves between pools when its cadence changes.
  - ≤1 in-flight operation per (target, kind, family). Every timeout is shorter than its cadence. Slow probes are staggered.
  - A slot that can't run → Skipped with a reason. No catch-up.
- **Backoff:** 15/30/60 s per endpoint, only for refusals the endpoint signals (429 with Retry-After, 503, contract mismatch). Never for timeouts or network errors.
- **Traffic:** small diagnostic requests only, except the on-demand speed phase below. Report payload bytes and estimated wire bytes separately.
- **Speed phase** (manual quick checks, when Settings allows it and the connection is not metered according to Windows connection cost):
  - Download for 8 s, then upload for 8 s, each over 4 parallel HTTPS streams (`ThroughputProbe`).
  - Byte caps: 200 MB down and 100 MB up. The upload payload is random so compression can't inflate results.
  - Mbps excludes ramp-up (the first second, or the first quarter of a capped fast transfer; `ThroughputMath`).
  - Latency probes keep running during the phase, giving latency under load (idle vs ↓/↑ medians, the bufferbloat signal).
  - The health verdict uses the idle phase only, so the test's own load never creates issues.
- **Cancellation:** aborts operations and closes per-operation sockets → Cancelled, never Timeout. Calls that can't be interrupted are capped, not replaced; their late results after close are discarded.

## 7. Windows integration and measurement truth

### 7.1 Context, routing, segments

- **Adapters:** GetIfTable2/NetworkInterface.
- **Route and source per destination:** GetBestRoute2 [3]. This is a prediction, not proof of the path. Refresh it every probe cycle and on NotifyRouteChange2 [4] / NotifyIpInterfaceChange [5].
- **Link state:** NotifyIpInterfaceChange + GetIfEntry2 (OperStatus, MediaConnectState). Needs no consent; this is the R01 baseline.
- **Segments:**
  - Key per family: (interface LUID, next hop) + proxy mode. A key change opens a new PathSegment; metrics never pool across segments.
  - Address-only changes (IPv6 temporary addresses) are events, not new segments.
  - No usable route → ContextTransition gap.
- **Attribution (per observation):** SocketObserved | RoutePredicted (ICMP, pre-connect) | Proxied (HTTPS via proxy/PAC) | Unknown.
- **Tunnels:** suspected when the best-route interface lacks the MIB_IF_ROW2 HardwareInterface flag, or is a tunnel/PPP interface. Never inferred from adapter names. A suspected tunnel suppresses Wi-Fi-vs-upstream localization.
- **Path:** follow the OS path; never bind to an adapter. For an Ethernet comparison the user changes the setup, and the next run verifies which interface was used.
- **IP families:** IPv4 and IPv6 are separate streams. On-link IPv6 next hops keep their scope IDs.

### 7.2 Wi-Fi and power

- **Baseline:** WlanRegisterNotification [6] with WLAN_NOTIFICATION_SOURCE_ACM only (connect/disconnect, reason codes). No consent needed.
- **Optional detail:** MSM roam/signal, WlanQueryInterface(current_connection) and SSID/BSSID need wiFiControl plus location consent [1].
  - Check consent with AppCapability.CheckAccess.
  - Prompt only from an explicit "add Wi-Fi detail" action; never at first launch, never repeatedly.
  - Denied or unavailable → recorded as a SessionCapability; other probes continue.
- **Event log:** no WLAN event-log import in v1.
- **Power:** suspend/resume cancels in-flight work and brackets gaps (§5). No blocking or DB work on callback threads.

### 7.3 ICMP

- **API:** Ping.SendPingAsync(IPAddress, TimeSpan, byte[], PingOptions, CancellationToken) [7], one Ping per stream. Resolve names outside timing.
- **Latency:** the OS-reported RoundtripTime (1 ms resolution, OsReported). Stopwatch is used only for scheduling bookkeeping.
- **Errors:** unreachable → NetworkUnreachable/HostUnreachable + ErrorCode (AdminProhibited, TtlExpired, …); no reply → Timeout.

### 7.4 TCP

- **Connect:** Socket.ConnectAsync to an approved literal IP:port with cancellation, then close immediately.
- **Duration:** TCP_INFO RttUs via SIO_TCP_INFO [8] (OsReported); otherwise Stopwatch connect time (UserMode). Record endpoints (SocketObserved).
- **Refused** means the path answered. It is neither silence nor a sign of service health. Windows re-sends the SYN after an RST (about 2 s), so with sub-second timeouts a refusing endpoint reads as Timeout. Approved TCP targets must therefore listen.
- **Proxies:** TCP is direct. With a proxy/PAC configured, label it "direct while proxy configured" and exclude it from R04/R06. Never bypass a mandated proxy for HTTPS.

### 7.5 System DNS

- **API:** DnsQueryEx [9], async, with DNS_QUERY_BYPASS_CACHE and DnsCancelQuery. A → IPv4 stream, AAAA → IPv6 stream. This keeps the system resolver path (servers, NRPT, VPN, Windows DoH) without reading or flushing the OS cache. Interop is hand-written, because CsWin32 emits DnsQueryEx only per CPU architecture; the layout is the same on x64 and arm64. Native memory is reference-counted, so a timeout that races completion can never free it early.
- **Reporting:** "system resolution time", not server RTT.
- **ErrorCode:** NxDomain, NoData, ServFail, DnsRefused, NoServers; timeout → Timeout. NxDomain/NoData on a probe name means an endpoint configuration issue or a retirement (§8).
- **Never:** force public DNS, generate unique names, or query user domains.

### 7.6 HTTPS

- **Handler:** SocketsHttpHandler; HTTPS only; default certificate validation. No cookies, default credentials, auto-redirect or auto-decompression. The system proxy is honoured.
- **Fresh connection per check (ConnectionClose):**
  - Duration = DNS (OS-cached) + TCP + TLS + request + ≤4 KiB body validation ("cold fetch time").
  - One handler per proxy context, recreated on segment change.
  - RequestFamily Any; the connected family is recorded.
- **Contract** = status + body token:
  - 407 → ProxyAuthRequired; 429 → RateLimited; certificate failure → TlsFailure.
  - 3xx or mismatch → HttpUnexpected (ErrorCode Redirect | Status | Body | Oversize). Record the redirect host; never follow redirects.
  - Never collect credentials or suppress validation.
- **TLS inspection:** a leaf issuer outside the manifest's expected set → "TLS inspection" context (AV or corporate proxy). This is context, not a failure.
- **ConnectCallback** [10] performs the connect. It records the family and, on direct paths, enforces pinned prefixes. Phase timing through it remains a later option; never fake phases.

## 8. Probe endpoints (release dependency)

- **Zones:** each operator's targets use a product-owned name in its own zone, with its own registrar and DNS provider (e.g. probe.<domain-a>, probe.<domain-b>). No single DNS outage removes both, and the DNS probe names stay independent (R05).
- **Kill switch:** an authoritative NxDomain/NoData retires the target for the session: no fallback, no traffic. Pinned fallback addresses are used only on timeout or ServFail, which keeps IP checks DNS-independent.
- **Blast radius:** resolved and fallback addresses must fall inside the operator's pinned prefixes; otherwise the target is disabled (SessionCapability Target). A hijacked zone therefore can't aim installs at third parties, and per-install rates stay capped.
- **HTTPS endpoints:** small static objects on two independent CDNs, edge-cached with a long TTL and served downstream with `Cache-Control: no-store`, so proxies can't answer on the upstream path's behalf. No server code. CDN logging minimized and disclosed.
- **ICMP/TCP targets:** the same operators' edge addresses, under documented terms. Fallback plan: two minimal responders on independent hosts; failing that, pause the release. Resolved at session start, outside timing.
- **Cost** (docs/endpoint-approval.md): ~8 HTTPS requests/min per active capture, each a tiny static response. Background checks add ~2 HTTPS requests per 10-second check (~5 for 30–60 s). At the default 10-second check every 5 minutes that is ~580/day per install while the app is open; each reconnect check adds ~2. That is the default for every install, so budget for it (worst case: 60-second checks every 3 minutes ≈ 2,400/day). Recurring cost against one-time revenue is accepted, capped by cadence, and reviewed against install counts.
- **Manifest entry:** id, operator id, purpose, names, pinned prefixes, fallback addresses, families, ports/paths, protocols, response contract, expected TLS issuers, payload cap, cadence/timeouts/allowed rate, usage-rights reference, review date, version. No secrets.
  - Shipped in the package and snapshotted per session by content hash. No remote config.
- **Validation (build and load):** reject plaintext external requests, executable actions, credentials, and external targets in loopback, link-local, private or CGNAT ranges. Release builds fail on a lab manifest.

## 9. Contracts

These are the contracts. Core implements the probe models, ITargetResolver/ResolvedTarget, PathContext and MonitorEvent; the evidence reader and writer are still design only. Persisted strings equal the enum names used in the schema.sql CHECKs.

```csharp
namespace ConnectionClue.Core;

public enum ProbeKind { Icmp, Tcp, SystemDns, Https }
public enum RequestFamily { IPv4, IPv6, Any }        // Any: OS chooses (HTTPS only)
public enum IpFamily { IPv4, IPv6 }
public enum Attribution { SocketObserved, RoutePredicted, Proxied, Unknown }
public enum TimingSource { OsReported, UserMode }
public enum ProbeStatus
{
    Success, Timeout, NetworkUnreachable, HostUnreachable, Refused, DnsFailure,
    TlsFailure, HttpUnexpected, ProxyAuthRequired, RateLimited,
    PermissionDenied, Unsupported, Cancelled, Skipped, InternalError
}

public sealed record StreamKey(string TargetId, ProbeKind Kind, RequestFamily Family);
public sealed record ProbeRequest(Guid SessionId, Guid SegmentId, long Sequence,
    StreamKey Stream, long ScheduledUs, TimeSpan Timeout);

public abstract record CaptureRecord(Guid SessionId);   // + event, gap, segment, capability, marker, anchor, state
public abstract record ProbeDetail(int Version);         // IcmpDetail, TcpDetail, DnsDetail, HttpsDetail

public sealed record ProbeObservation(ProbeRequest Request, ProbeStatus Status,
    Attribution Attribution, IpFamily? ObservedFamily, long? StartedUs, long? EndedUs,
    long? DurationUs, TimingSource? TimingSource, string? ErrorCode, ProbeDetail Detail)
    : CaptureRecord(Request.SessionId);

public interface IProbe
{
    ProbeKind Kind { get; }
    Task<ProbeObservation> ExecuteAsync(ProbeRequest request, CancellationToken ct);
}

public interface IEvidenceWriter { ValueTask EnqueueAsync(CaptureRecord record, CancellationToken ct); } // backpressure, never drops
public interface IEvidenceReader { Task<SessionEvidence> ReadSnapshotAsync(Guid sessionId, CancellationToken ct); } // one read txn
```

- Targets resolve only through the session manifest snapshot. ProbeRequest never carries URLs or addresses.
- SessionController allocates Sequence monotonically; commit order may differ.
- Expected network errors are observations. Unexpected exceptions → a bounded InternalError plus a diagnostic-log entry, never a network finding.
- ErrorCode uses a closed, versioned vocabulary per kind.

## 10. Storage

schema.sql is migration 001 (DDL only; validated, §19).

- **Tables:** Investigation, EndpointManifest, Session, ClockAnchor, PathSegment, ProbeObservation, ConnectionEvent, CoverageGap, SessionCapability, SymptomMarker, AnalysisRun, Finding, ActionAttempt, Comparison.
- **View:** InvestigationActivity.
- **Vocabularies:** closed lists are defined once in schema CHECKs and mirrored by C# enums.

**Enforced in SQL** (STRICT, CHECKs, composite FKs, triggers):

- An observation's segment belongs to the same session. A comparison's sessions and action belong to its investigation, and baseline ≠ follow-up.
- Status, duration and timing are consistent:
  - Success has a duration; Timeout, Skipped, Cancelled, denied and internal errors have none.
  - Skipped has a reason.
  - Status, family and attribution are valid for the kind.
- Terminal states carry a matching EndReason, EndedUtc and EndedUs.
- Observations and events are append-only.
- **Caps:** notes 2,000 chars; event/context details 8 KiB; probe details 4 KiB. No raw payloads or logs.
- Every FK child key is indexed.

**Enforced by the app:**

- JSON matches the typed, versioned models (json_valid is only a backstop).
- Streams are permitted by the manifest.
- Offsets fall inside the session and a valid segment; segments are contiguous and non-overlapping.
- Compared runs are terminal, ordered and compatible.
- ActionCode/RuleId come from the versioned catalogs.

**Findings:** one AnalysisRun per (session, rules version).

- New rules add runs; old runs keep earlier reports reproducible.
- A same-version recompute replaces its run in one transaction.

**Operations:**

- **Location:** LocalApplicationData (MSIX redirects it per package; verify). No hard-coded package paths.
- **ConnectionFactory:** on every connection, foreign_keys, busy_timeout, secure_delete, trusted_schema=OFF. Once, outside a transaction: journal_mode=WAL, synchronous=NORMAL. Parameterized SQL, one writer, bounded transactions.
- **MigrationRunner:** owns the transaction and user_version, refuses newer DBs, and backs up before destructive migrations. Downgrade = restore the backup.
- **Retention** (at startup, after interrupted-session recovery): delete investigations whose LastActivityUtc is >30 days old and empty ones >24 h old, then GC unreferenced manifests. This never orphans comparisons, and exports are untouched.
- **Preview last result:** until the SQLite store lands, the last check with issues or best-practice advice is kept in `last-result.json`: check time (UTC), report codes and values, context (medium, tunnel, router IPv4), action codes, advisory codes, levels and values, and the areas checked as fine. It is plain JSON and never holds rendered text, so it re-renders in the current language. Files without the newer fields (older versions) still load. A clean check without advice, or Dismiss, deletes it; an inconclusive check without issues keeps it.
- **Delete history** = DELETE + secure_delete + wal_checkpoint(TRUNCATE) + VACUUM. This is not forensic erasure (SSD remapping, backups and exports survive).
- **Encryption:** none for the DB or exports, by design. Protection relies on per-user OS permissions; the privacy page discloses this.

## 11. Metrics

Metrics are computed per stream (target + kind + family) and per segment.

- **Latency:** median and nearest-rank p95 (index ⌈0.95n⌉−1) of successful DurationUs.
  - p95 only if n ≥ 100; otherwise "insufficient samples".
  - A quick check yields ≤120 samples per 1 s stream, so p95 survives only ≤16 % loss. Recommend longer captures for tail comparisons.
- **No-reply (ICMP/TCP):** Timeout / (Success + Timeout), with the denominator shown.
  - Unreachable and refused are counted separately.
  - Excluded: Cancelled, Skipped, PermissionDenied, Unsupported, InternalError, RateLimited.
- **DNS/HTTPS:** failure counts per category against the contract. No universal loss %.
- **Probe RTT variation:** median |Δ| of adjacent successes, with no failure or gap between them, spacing ≤2× cadence, and ≥20 pairs. Never call it VoIP jitter or MOS.
- **Throughput:** download and upload Mbps per phase (see §6 for ramp-up exclusion), with bytes used. **Latency under load:** internet median during each speed phase, shown next to the idle median.
- **Coverage:** executed / scheduled eligible slots in the planned window. Timeouts count; skipped, denied and internal errors don't. Show it with unobserved time and per-class capability.
- **No "uptime":** show observation time and incident windows. Buckets are for display only; failure counts are always visible.

## 12. Rule engine

- **Input:** committed snapshot, capabilities, segments, gaps, markers, action history.
- **Output:** typed findings stored as message keys + params: RuleId, rules version, time range, evidence refs ((SessionId, Sequence) ranges), facts, interpretation, next action, limitations.
- **Evidence levels:** Observed | Correlated | Inconclusive.

**Policy** (rules-v1.json). RulesVersion = semver + SHA-256 of the policy file; CI fails if the hash changes without a version bump.

- **Windows:** correlation ±5 s; marker match [−20 s, +5 s], or [−60 s, +5 s] with the Extended allowance (§14); report context ±30 s.
- **Failure episode:** ≥3 consecutive failures on streams sampled every ≤2 s, ≥2 on slower ones (`HealthEvaluator.OutageRun`).
- **Recovery:** 3 consecutive successes. Report the first recovery and its confirmation separately.
- **ICMP capability:** Unavailable only while ICMP is silent and TCP/HTTPS to the same operator succeed. Re-evaluated continuously. Everything silent = outage candidate.
- **Latency baseline:** per-segment median + MAD of successes, ≥60 samples. Captures >15 min use a centred 10-min window.
- **Excursion:** RTT > median + max(5·MAD, 30 ms) in ≥2 of 3 consecutive samples. No global "bad latency" threshold.
- **User limits (alerts):** the user's own delay, no-answer and variation limits. Defaults are 100 ms, 2 % and 30 ms; each is adjustable. A result is reported as "below your limits", never as a verdict on the network.
  - `Analysis.HealthEvaluator` (preview):
    - Needs ≥6 internet samples, otherwise the result is Inconclusive.
    - Interruption = ≥2 consecutive internet failures; web unreachable = ≥2 consecutive web failures.
    - Loss = ≥2 failures and more than the limit. Delay = internet median above the limit. Variation = probe RTT variation above the limit.
    - Location: the router explains the problem when it shows ≥50 % of the internet value, or when a router failure run (≥3) overlaps the outage within ±5 s. With a silent router, the location is Unknown.
    - Router-only symptoms never alert.
  - These are lab-calibrated like every other policy value.
- **Comparison labels:** ≥80 % coverage per run; tail comparisons need ≥100 successes per run.

| Id | Finding | Condition | Never claim |
|---|---|---|---|
| R01 | Local link interrupted | Link-down or WLAN disconnect on the probed interface | Defective hardware; cause |
| R02 | Gateway visibility limited | Gateway ICMP silent while externals succeed | Router broken |
| R03 | Local network path interrupted | A previously responding gateway fails together with externals; link up | Which device failed |
| R04 | External reachability lost | ≥2 independent operators fail on every available class; link up. Includes "never reachable since start". "Beyond your router" only if the gateway kept responding | ISP fault |
| R05 | System name resolution failed | ≥2 independent names fail while direct-IP checks succeed. State whether it was a timeout or a negative answer | Resolver broken; split DNS ruled out |
| R06 | One endpoint/path affected | One operator degrades while the other responds | Home-wide outage |
| R07 | Web validation failed | TLS failure, proxy auth, redirect or contract mismatch; exact category | Portal or filtering certainty |
| R08 | Delay between this PC and router | Gateway excursion overlaps (±5 s) ≥1 external excursion. Add Wi-Fi context when on Wi-Fi | Interference source |
| R09 | Delay beyond router | Excursions on ≥2 independent externals while the gateway stays at baseline. Suggest testing household upload load (bufferbloat) | ISP fault |
| R10 | No matching problem observed | Adequate coverage around the markers (or the whole capture if none) with no matching failure or excursion | Healthy network; bandwidth |
| R11 | Unable to conclude | Missing capability, low coverage, ambiguous path (tunnel) or denied access. State what's missing and the next safe step | — |

- **Guards:**
  - Proxy-direct streams are excluded from R04/R06.
  - A suspected tunnel turns R08/R09 into R11.
  - Gateway-only excursions are ignored (routers deprioritize control-plane ICMP).
- **Primary card:** marker overlap → evidence level → priority (R01 > R03 > R04 > R05 > R07 > R08 > R09 > R06 > R02 > R10 > R11) → earliest start → RuleId.
- **Stability:**
  - Findings coexist, and recovered incidents remain.
  - A generic green status never overwrites a precise event.
  - Any input order yields identical findings.

**Next actions** (catalog v1). All are user-performed, filtered by constraints and by completed or skipped actions:

| ActionCode | Suggested by | Suppressed when |
|---|---|---|
| RetestOnEthernet | R01, R03, R08 | NoCable, or on Ethernet |
| ImproveWifiPlacement | R01, R08 | On Ethernet |
| PauseHouseholdUploads | R09 | — |
| RestartRouter | R03, R04, R05 | — |
| DisconnectVpn | R11 (tunnel) | VpnRequired |
| CheckServiceStatus | R06, R07 | — |
| ContactIsp (with report) | R04/R05/R09 repeating after RestartRouter | — |
| CaptureLonger | R10, R11 | — |
| CheckCable | Local issue while on Ethernet | Not on Ethernet |

Preview mapping (`NextActionPlanner`). The most severe issue comes first, local fixes before upstream ones, with no repeats:
- **Local interruption or loss:** Wi-Fi → ImproveWifiPlacement, RetestOnEthernet; Ethernet → CheckCable; then RestartRouter.
- **Upstream interruption or loss:** RestartRouter, ContactIsp.
- **Web unreachable:** RestartRouter (+ DisconnectVpn if a tunnel is suspected), ContactIsp.
- **Local delay or variation:** the Wi-Fi/cable steps, then PauseHouseholdUploads.
- **Upstream delay or variation:** PauseHouseholdUploads, RestartRouter, ContactIsp.
- **Suspected tunnel:** always adds DisconnectVpn.

**Configuration advisor (`ConfigurationAdvisor`, preview).**
- **When and how:** after every manual or background check, `SystemInspector` reads network-relevant settings as a standard user.
  - It only reads; the app never changes a setting.
  - Each read is independent and best-effort. A value it can't read never produces advice.
  - Facts are gathered in parallel with the check (about 0.5–1.5 s) and awaited when the check completes (at most 5 s).
  - LatencyUnderLoad needs a speed phase. Background and metered checks have none, so they carry the last measured advice forward; only a check with a speed phase or Dismiss clears it.
- **Levels:** each piece of advice is Important or Suggestion, with Important first.
- **Gating:** symptom-specific advice appears only when the chosen symptom or a detected issue makes it relevant, so healthy PCs aren't flooded.
- **Thresholds:** proposed defaults, pending lab calibration.
- **Specific, not generic:** every item carries its evidence and says exactly what to change on this PC. Generic wording is only a fallback, used when a name can't be read.
  - **Evidence** (`Advisory.Args`, stored as codes): the names from this PC: app, adapter (`NetworkInterface.Description` / `DriverDesc`), network (connection profile name), DNS server, proxy address, VPN adapter, driver version, and the exact setting value (netsh auto-tuning level, `DisabledComponents`, Wi-Fi power-saving level).
  - **Wording:** `Variant` picks instructions for the exact situation. Text lookup falls back in order: `Advice_<Code>_<Part><Variant>`, then `<Part>Named` when names are known, then the generic `<Part>`. Args starting with `#` are numbers and args starting with `@` are resource keys; both are localized when shown.
  - **Other apps' traffic:** the interface counters give the rate during the idle phase.
    - Windows' per-app data usage (`ConnectionProfile.GetAttributedNetworkUsageAsync`, the source of Settings > Data usage) names the apps as a standard user. It keeps one-minute buckets, so the text says "during the check", meaning around it. This app is left out.
    - Apps are ranked by the direction that triggered the advice: sent MB for upload, received MB for download. A big downloader is never blamed for an upload problem. OneDrive, other sync apps, game launchers, browsers, Windows services, other) picks the instructions: how to pause OneDrive syncing; pause downloads in the named launcher; Ctrl+J and streaming tabs in the named browser; the Delivery Optimization limit for Windows services (with a link); otherwise close the named app (Task Manager link). A second app is added as "Also active". App-specific steps (OneDrive, sync apps, launchers, browsers) carry no generic Task Manager link.
  - **Latency under load:** names the direction that hurts (download or upload) and gives the router a concrete SQM/QoS limit, 90% of the measured speed.
  - **Wi-Fi link speed:** it reads the adapter's standard from its name (Wi-Fi 7/6E/6/5/4).
    - A 5 GHz-capable adapter on a slow link is told to join the 5 or 6 GHz network ("<network>-5G").
    - A Wi-Fi 4 adapter is told that the adapter itself is the limit.
  - **Other specifics:**
    - DNS: whether the slow server is the router.
    - Proxy: whether it runs on this PC (loopback).
    - Old drivers: Intel adapters get Intel's driver tool (fixed URL); others get the PC maker's page or Optional updates.
    - IPv6: registry-wide (needs an admin, so no link) versus unchecked on the adapter (Network Connections link).
- **Fixes with evidence:** planner steps use the check's evidence too.
  - "Pause large uploads" names the app on this PC, or says this PC was quiet, so another device at home is the likely cause.
  - "Restart your router" names its address.
  - "Contact your provider" quotes what to tell them.
- **Online AI review before showing** (`Review/OnlineAdviceReviewer`, Settings toggle, on by default): every fix and best practice is checked by free online LLMs before its card appears.
  - **Request body:** generic English text only, not the user's personal wording. Names from this PC become `[name]`, addresses `[address]`, and measurements `N`; command and product text stays. Tests assert the body contains no check/device data.
    - Public HTTPS services still receive normal connection metadata, including the public IP; disclose this before enabling the feature and link to the providers' privacy terms.
    - The stable text also means each wording is reviewed once, cached for 30 days by SHA-256 (`review-cache.json`), and later checks are instant.
  - **Reviewers:** three LLMs on two independent free services, anonymous as documented by each: LLM7.io `default`, the Pollinations legacy anonymous `openai-fast`, and LLM7.io `GLM-5.3-Flash` as a slow last resort.
    - All items go in one numbered request per reviewer. A timeout (30/30/45 s), HTTP error, or unusable reply moves to the next reviewer.
    - Replies are parsed tolerantly (`{"results":[…]}`, `{"1":"ok"}`, `{"answers":[…]}`, bare arrays, synonyms), because the models do not follow the schema exactly.
    - Free services change, so `reviewers.json` in the app data folder replaces the list (HTTPS only), for example with keyed free tiers (Groq, OpenRouter, Gemini).
    - Web scraping of chat sites is not used; it breaks their terms and is blocked by bot defences.
  - **Decision:** an item is hidden only when two distinct provider hosts call it wrong. Only unresolved items go to the next reviewer; correlated models from one service do not count as two votes. Advice a single reviewer disputes stays unconfirmed.
    - Confirmed cards show "AI-checked", and the header states the outcome: checked by …, partly checked, unavailable, or advice held back.
    - The status line and background notifications are built only from what passed.
  - **Offline or no answer:** the advice is shown unchecked, with a clear status line, because help matters most when the connection is bad.

| Advice | Read from | Fires when | Important when |
|---|---|---|---|
| LatencyUnderLoad | TCP RTT, idle vs. speed phase | loaded − idle ≥ 100 ms | always |
| OtherTraffic | Interface byte counters (`GetIfEntry2`) over the idle phase (the check's own probes add < 0.1 Mbps); apps named from Windows per-app data usage | ≥ 5 Mbps down or ≥ 1 Mbps up from other apps | Gaming or Calls, or a delay/variation/loss issue |
| WeakWifiSignal | WinRT `GetSignalBars` (connected profile only) | ≤ 2 of 5 bars | Gaming, Calls or Disconnects, or a local issue |
| TcpAutoTuningLimited | WMI `MSFT_NetTCPSetting.AutoTuningLevelLocal` (Internet, InternetCustom) | Disabled or Restricted | always |
| EthernetLinkSlow | `GetIfEntry2` link speed | ≤ 100 Mbps | always |
| WifiLinkSlow | `GetIfEntry2` link speed | < 150 Mbps (just above the 2.4 GHz ceiling of 144 Mbps) and no weak-signal advice | a local issue; < 50 Mbps; or Gaming, Calls or Video below 100 Mbps |
| AdapterPowerSaving | Adapter class key `PnPCapabilities` (bit 0x08 clear) | Disconnects, or an interruption | always |
| UsbSelectiveSuspend | `DeviceInstanceID` starts with `USB\`; power plan USB selective suspend on the current power source | Disconnects, or an interruption | always |
| EnergyEfficientEthernet | `*EEE` / `EEE` / `EEELinkAdvertisement` ≠ 0 | Gaming or Calls, or a delay/variation issue | always |
| PowerSaving | Energy saver, the best-efficiency power overlay, or Wi-Fi power saving ≥ Medium for the current power source (battery or plugged in) | — | Gaming or Calls, or any issue |
| SlowDns | DNS probe median (cache bypassed) | > 150 ms | Video, or web unreachable |
| OldNetworkDriver | `DriverDateData` (Microsoft inbox drivers skipped) | > 2 years old | never |
| MeteredConnection | ConnectionCost | Wi-Fi or Ethernet marked metered | never |
| ProxyConfigured | `HttpClient.DefaultProxy.GetProxy` | A proxy applies | never |
| VpnActive | Tunnel suspected | Gaming or Calls with no issues (with issues, the planner adds DisconnectVpn) | never |
| Ipv6Disabled | Tcpip6 `DisabledComponents` & 0x10, or IPv6 unbound from the adapter | — | never |

- **Checked, no change needed:** besides advice, the advisor returns the areas whose value is already the recommended one (`CheckArea`, for example Wi-Fi signal, link speed, driver, TCP auto-tuning, proxy, IPv6). A clean check therefore still shows what was verified, both in the status panel and at the end of the recommendations. Unknown values, and values that are only irrelevant to the symptom (adapter power saving without disconnects), are neither advice nor "fine", and a flagged area is never listed as fine.
- **Helpers:** each card can carry one link, placed inline after its how-to text:
  - Open the exact `ms-settings:` page (proxy, VPN, power, Wi-Fi or Ethernet properties, optional driver updates, advanced network settings).
  - Open Device Manager, Power Options or Task Manager.
  - Open the router page (`http://<gateway>/`, private IPv4 only).
  - Copy the `netsh` command.
  - Copy a plain-text summary for the internet provider: check time, findings and latest measurements.
  - Weak signal and slow cable links need physical changes and have no link.
  - The app's `Shell` accepts only this fixed set of targets, so saved or tampered data can't launch anything else. Helpers open places; the user makes every change.

## 13. Comparison

- **Runs:** from one investigation, with the follow-up after the baseline, both evaluated under the same rules version (re-analyse the baseline if needed). The user confirms the action and whether more than one change occurred (MultipleChanges).
- **Comparability:**
  - **Comparable:** same profile, manifest hash, families and rules; only the intended context differs.
  - **Caution:** different duration, endpoint addresses, proxy/tunnel state or coverage; multiple changes; or runs >60 min apart.
  - **NotComparable:** incompatible kinds, rules or targets, or unusable evidence.
- **Context changes:** Wi-Fi→Ethernet is intended and shown prominently. An unexpected VPN or endpoint change → Caution.
- **Per run:** counts, no-reply counts and rates, median, p95 (if eligible), variation, excursions, link events, gaps.
  - Show absolute differences first.
  - No aggregate "% better", no significance claims, no ratios on tiny denominators.
  - Fewer failures from fewer samples is not an improvement.
- **Verdicts:** "Latency improved in this sample" needs adequate data and agreement between median and tail; otherwise "mixed". Recommend A/B/A for intermittent problems. Record outcomes even when Failed or NotPossible.

## 14. UI

**Shell:** starts maximized on every launch, whatever the shortcut's Run setting (restore size 1280×800, clamped to the work area); reopening from the notification area returns the last state. A compact top navigation bar (Windows 11 top NavigationView pattern) holds the logo, Check, Recommendations (shown only when the latest check found issues or missing best practices, with a count badge), Insights, Help, then Settings at the far right. Help immediately precedes Settings. Tabs show an icon and a label with an underline when selected, so every page gets the full window width; a side pane left most of the left edge empty space. The Check page fills the window height, and its chart rows share the remaining space. Recommendations and Settings have a page title (H1); on Check the status headline is the H1.

**Views:**

- **Check:** a top band with the 380 px status card on the left and, on the right, the metric tiles (Download, Upload, Latency with its under-load detail, Variation; each a rounded tile in its own theme colour with an icon, and a tooltip that explains the metric) above the path, whose card stretches so both bottoms line up. The details chart spans the full width below and takes the remaining height. Speed tiles show live Mbps during the phase, then the result and MB used, or why the test was skipped (not measured, metered connection, could not measure).
  - **Status panel:** a badge whose icon and colour change together (idle, checking, no problems, below limits, problem, not enough data), the headline, findings and check controls. Detailed "What to try" and "Worth checking" text, verified checks, bufferbloat and service-test summaries belong on Recommendations, not Home.
    - **Actions:** Quick check, plus Recommendations only when issues or best practices exist, then adjacent Export (PDF) and Export (MHTML) buttons after a quick check or longer capture. Both use `ResultsExportData` and the same visual capture. PDF uses Microsoft Print to PDF; MHTML is an offline multipart/related archive with UTF-8 HTML and PNG parts, HTML-encoded result text, captions and measurement tables. Long-capture tables are capped at 1,000 rows per step with an omission note. Exports are serialized, report success/cancellation/errors on the Check page, and prevent a new check from replacing their data. During a check: Stop and the marker. A divider labelled "or" separates Capture longer and its length (15, 30 or 45 minutes, or 1, 2, 4 or 8 hours; 15 minutes by default); both hide during a check. Below them the "What's happening?" radio group (one choice always selected; arrow keys select) and the last-check time.
  - **Path:** This PC → Home network → Internet → Web services. During a check each step shows its latest sample. After it, each step shows the evaluated verdict (Responding, Slow, Not responding) and its median, so the path never contradicts the headline. Connectors are solid, long-dashed, short-dashed or dotted according to status.
  - **Details:** small multiples, one row per step, each with its own scale, so ICMP, TCP RTT and HTTPS cold fetch never share an axis. Each check is a column; unanswered checks are full-height hatched columns; values above the user's delay limit use a second colour and sit under a dashed limit line; values beyond the scale (95th percentile) get a ▲ cap. The time axis adapts to the check length. Each row header shows median · p95 · variation · no answer, and the row's UIA name carries the same text. A value grid (0, ¼, ½, ¾ and the scale top, labelled in ms; the top is chosen so every step is a round whole number, e.g. 60/120/180/240) and a time grid at the axis steps make bars comparable. Tooltips explain the chart and each row (also the UIA help text), and pointing at a bar shows its time and value.
- **Recommendations:** the first line always shows the date and time of the check that produced them, with Quick check and Dismiss beside it. When they come from an earlier app session or are older than 10 minutes, a note follows: conditions may have changed, run a quick check to confirm.
  - The reviewed "What to try" / "Worth checking" summary wraps in full above the detailed cards, including after restoring a saved result. Bufferbloat and service-test summaries appear only when they describe the same check; live details from a new check are never attached to older saved advice. These details remain available to exports and notifications.
  - Below that come two sections, in reading order:
    - **Fix the issues found:** the findings and numbered fixes (`NextActionPlanner`).
    - **Best practices for this PC:** `ConfigurationAdvisor` cards. Each card has an Important (warning icon) or Suggestion (lightbulb) badge, and the level is spelled out, so neither colour nor icon carries it alone. Each card's how-to names the exact Settings or Device Manager path.
  - When only best practices exist, a note says no issues were found instead.
  - The page ends with the "Checked, no change needed" list. A card with a helper ends with an inline link, such as Open Settings or Copy command (§12).
  - **Layout:** with five or more items and at least 760 px, the cards flow in balanced newspaper columns (`Controls/FlowColumns`); fewer items stay in one column of at most 900 px, for readable lines. In columns, the flow goes down the first column, then the second, with the split chosen to minimize the taller column. Headings are UIA level-2 headings; they are not tab stops and never end a column.
    - Measured at 960×740 with 3 fixes and 6 best practices: English, German and Turkish don't scroll. Tamil, the longest language, scrolls about 5%, and fits when maximized.
    - The page scrolls rather than truncating text (WCAG reflow).
  - This page opens automatically at startup when a saved result exists, and whenever the window is reopened from the tray or a notification.
  - After a manual check with issues, the check page shows the findings and Recommendations button, without the long advisory text; it does not navigate away.
- **Compare:** baseline, action (Completed/Skipped/Failed/NotPossible), follow-up, cards.
- **History:** delete and export.
- **Settings:** three compact two-column tabs: Checks (quick check length, speed test, background interval, background check length, mobile override and Windows startup); Network (alert limits, plan speeds and symptom targets); Preferences (theme, language, keyboard shortcuts, online advice review and history clearing). The update-check section is removed. The 19 editable preferences keep one binding each; long target explanations move to field tooltips and UI Automation help, while a short summary stays visible. Check timing uses the full card width. Normal layouts need no scrolling; per-tab scrolling remains an accessibility fallback for enlarged text. Symptom targets start with a default service per symptom (Riot Games, Prime Video, Discord, Microsoft). `SettingsViewModel.Upgrade` migrates by `AppSettings.SettingsVersion`: below 2 it fills empty targets (so a later cleared box stays empty) and moves Capture longer from 60 to 15 min; below 3 it moves the old 15-minute interval default to 5. Saved lengths outside 10–60 s are clamped. Still to come: capabilities/privacy, accessibility (announcement verbosity, marker allowance), endpoints, retention, hotkey, data path.
- **Insights:** connection history and the daily summary first, then services for the chosen symptom, the network tools (route hops, DNS comparison and switch, Wi-Fi channels), the report for your provider and the recent checks.

**Themes (Settings > Appearance):** Dark (default), Light, High contrast dark, High contrast light, and Use Windows setting. A theme applies at once, without a restart. The design tokens live in `Themes.cs`; each has one meaning everywhere and is contrast-tested in every theme (`ThemeContrastTests`).
- Text roles: headings (dark: gold #FFD479), labels that name a setting, field or column (dark: lavender #D0C4FF), body text, supporting text, and underlined links (dark: cyan #99EBFF). The four differ in every theme; size and weight differ too.
- Action roles, each with an icon and a label: Primary starts a check or test (dark: azure #60CDFF, black text); Tool runs a diagnostic or makes a file (teal outline); Admin changes a Windows setting after a UAC prompt (amber, shield icon); Mark records a lag moment in the chart's lag-marker colour; Stop (red); Advice opens recommendations (purple); Neutral dismisses (grey outline); Link opens a Windows Settings page (underlined, "opens elsewhere" icon). Dark uses light fills with black text so buttons stand out from dark cards; Light uses dark fills with white text.
- States: hover thickens the outline in the label colour, pressing darkens the face, and a disabled button turns grey with a dashed outline (still at least 3:1) and keeps its tooltip. Focus shows a double ring, visible on any background.
- Action, navigation and switch styles reuse UI Automation help text for detailed hover and keyboard-focus tooltips. Page-level help describes the operation, timing or impact rather than repeating the button label.
- Status colours (badges and path lines): Success, Warning, Danger and Muted.
- With Windows high contrast on, every token maps to the user's system colours (`ThemeManager`, updated live).

**Behaviour:**

- Local disconnection produces an all-page notice and one `Disconnected` desktop alert per episode. The next disconnected-to-connected transition queues one 10-second `CheckKind.Reconnect` check after a two-second settling delay. Duplicate connected events do not restart it. The queue waits on ongoing measurements/result processing, diagnostics and exports; another disconnection or disposal cancels it. The reconnect run skips speed tests, symptom services and online review, does not navigate away from the current page, and does not alter saved settings. It is independent of recurring scheduling, including on mobile/metered links.
- When the reconnect check completes, a `Reconnected` notification reports its level, and the all-page notice shows "Reconnect check (time): headline" (warning border and icon for Unhealthy/Degraded, via `IsConnectionNoticeWarning`) until **Dismiss** (`DismissConnectionStatusCommand`) or the next check starts. The notice never says "healthy": NoIssue is "No problems found".
- Every async command has busy, cancel and error states.
- Distinguish NotAvailable from Failed, and NoIssueObserved from "healthy".
- **Marker:** button, tray, and a RegisterHotKey shortcut registered only during capture (conflicts reported; can be remapped to any key, including a single F-key, or disabled).
  - Preview: the "It lagged just now" button shows only during a check. Its tooltip (also shown on keyboard focus) and UIA HelpText explain it: press it the moment you notice lag, and a flag marks that moment on the chart.
  - Each press records the second into the check. Every chart row draws a dashed reddish-purple line with a pennant there, so the mark is a shape as well as a colour, on top of the bars. The legend lists the times ("Your lag marks: 12 s, 31 s").
  - No hooks or overlays.
  - Outside a capture: "Start a capture first".
  - Timestamped at input, acknowledged at enqueue.
- **Timeline:** latency distributions, failures, link events, markers, grey unobserved intervals. Never interpolate across gaps.
- Recommendations are instructions; the app changes no network setting except the DNS switch, and only when the user selects it and approves the Windows prompt.

### Accessibility (release gate)

Target: WCAG 2.2 AA, applied to desktop software through EN 301 549 clause 11. It is verified per release (§16), and the Store accessibility declaration is ticked only after a pass.

- **Screen readers (Narrator, NVDA, JAWS):**
  - Every control exposes a UIA name, role and state. The visible label is part of the name, so Voice Access commands match. Icons are named; decorative elements are hidden from UIA.
  - The timeline has a custom AutomationPeer that summarises incidents in text, plus an equivalent data-table view (DataGrid with row and column headers).
- **Announcements:**
  - State changes, link events, marker acknowledgements, capture end, findings and errors go out through `AutomationPeer.RaiseNotificationEvent`.
  - Values that change every second are not live regions.
  - Verbosity (Minimal / Standard / Verbose) throttles progress and coalesces bursts of link changes. The policy is `Presentation.Accessibility.AnnouncementPolicy` and is unit-tested.
- **Keyboard and motor:**
  - Everything works from the keyboard alone: logical tab order, access keys, no focus traps, and a visible focus indicator (system focus visuals, ≥2 px).
  - Targets are ≥28 px high (buttons 30, chips 28), never below the WCAG 2.2 minimum of 24 px. Controls are compact for no-scroll layouts.
  - No drag-only, hover-only or timed interactions. The marker shortcut works with Sticky Keys.
- **Reaction time:** a marker allowance setting offers Standard [−20 s, +5 s] or Extended [−60 s, +5 s] for users who need longer to react. It is snapshotted in the session profile (Core `MarkerWindow`), so analysis and comparisons use it.
- **Vision:**
  - Honour Windows 11 contrast themes with system colour resources only; verify all four themes.
  - Honour Windows text size up to 225 % (`UISettings.TextScaleFactor`) with reflow and no clipping.
  - Per-monitor DPI v2; Magnifier follows focus and caret.
  - Contrast ≥4.5:1 for text and ≥3:1 for graphics.
  - Status always uses icon + text + pattern, never colour alone. The chart palette is colour-blind-safe, with shape markers.
- **Motion:** honour Windows animation and transparency settings (`UISettings.AnimationsEnabled`, `AdvancedEffectsEnabled`). Nothing flashes more than 3 times a second. Live graphs update in place.
- **Cognitive:**
  - Plain language first (about a 12-year-old reading level), technical detail on demand, one primary action per view.
  - Consistent layout and wording. Destructive actions need confirmation.
  - Progress shows the remaining time. Nothing waits for a timed user response, and messages stay until dismissed.
- **Hearing:** no information is conveyed by sound alone.

### Localization (20 languages)

- **Languages:** en, zh-Hans, hi, es, ar, fr, bn, pt (neutral Portuguese culture; regional variants fall back to it), ru, id, ur, de, ja, mr, vi, te, ha, tr, ta, zh-Hant.
  - These are the most-spoken languages (Ethnologue), merged by written UI locale: Egyptian Arabic → ar, Nigerian Pidgin → en, Yue and Taiwanese Mandarin → zh-Hant.
  - The list is reviewed yearly. Adding a language means adding data and a translation, not code.
- **Selection:** English by default. The user picks one of the 20 languages in Settings; each is listed by its native name.
  - The choice is saved in the per-user settings file (plain JSON) and takes effect on restart. It is disabled during a capture.
  - `LanguageResolver` maps a saved or test (`--lang`) tag through the culture parent chain (zh-TW → zh-Hant, pt-PT → pt).
- **Resources:** `Presentation/Resources/Strings.resx` holds the neutral English strings; each language has one satellite.
  - Translator comments give context and limits.
  - Plural keys use CLDR categories (`Key.one` … `Key.other`, selected by `PluralRules`).
  - Nothing localized is persisted: findings store message keys and parameters, and notes are stored exactly as typed.
  - Strings go to translators as XLIFF.
- **Formatting:**
  - Text follows CurrentUICulture. Numbers, dates and times follow CurrentCulture, i.e. the Windows regional format, including the user's calendar.
  - WPF `NumberSubstitution` honours native digits.
  - Length limits count Unicode code points, matching SQLite `length()`.
- **Right-to-left (ar, ur):**
  - FlowDirection RightToLeft mirrors the layout and the timeline's time axis.
  - Numbers, addresses and hostnames are kept as isolated left-to-right runs (FSI/PDI).
  - Directional icons are mirrored.
- **Scripts:**
  - Set the root element's Language (xml:lang) so WPF picks the right glyphs, including Han variants for zh-Hans, zh-Hant and ja. The UIA Culture is set as well, so screen readers switch voice.
  - Fonts come from the system UI fonts through WPF composite-font fallback.
  - Layouts are fluid: allow about 50 % text expansion and taller line heights for Indic scripts.
- **Tone:** the diagnosis-language rules apply in every language, backed by a translated glossary in docs/diagnosis-language.md.
- **Package metadata:** the app resources and MSIX `Resource` tags cover the same 20 UI cultures. The manifest DisplayName and Description are still English literals; localized MSIX strings and Store listing text remain release work.

## 15. Reports, privacy, security

- **Online AI review (preview):** the request body contains generic advice text only, with names, local addresses and measurements replaced (§12). Providers still receive normal HTTPS metadata, including the public IP. This is disclosed beside the Settings toggle and can be disabled. Verdicts are cached locally as plain JSON (hashes, verdicts, reviewer names).
- **Source:** export only a typed ReportModel pinned to one AnalysisRun; never the DB or raw logs.
- **Contents:** app/rules versions; capture dates + local UTC offset; symptom/device scope; observation time, counts, unavailable tests, gaps; findings with limits; marker context (±30 s, clipped); actions and comparison warnings; redaction summary; "probes cover this PC's paths only".
- **Formats:**
  - **HTML:** one self-contained file (inline CSS/SVG, print stylesheet; browser Print to PDF replaces a built-in PDF). All text escaped. No JS, web fonts, trackers or remote resources.
  - **Text:** for support chats.
  - **CSV:** one redacted row per observation.
  - **Writing:** one file per export. Save dialog with overwrite confirmation, then an atomic write (temp file in the target folder, then replace).
- **Redaction defaults:**
  - Exclude SSID, BSSID, MAC, local/public addresses, host/user names, machine ids, notes.
  - Use per-investigation aliases (Gateway, Wi-Fi adapter A). Hashing an SSID is not anonymization.
  - Endpoint operators are public config and are named.
  - Per-export opt-in after a preview, with a warning that timestamps and notes reveal activity.
- **CSV safety:** use a real CSV writer. Neutralize text starting (after optional whitespace) with = + - @ tab CR. Numeric columns stay typed. Tested.
- **Accessible reports:**
  - HTML has a `lang` attribute, a single h1 with ordered headings, and landmarks. Tables have a `<caption>` and `<th scope>`.
  - SVG charts use `role="img"` with `<title>`/`<desc>` and an adjacent data table.
  - The page reflows at 320 CSS px, honours `forced-colors`, `prefers-contrast` and `prefers-reduced-motion`, and never shows status by colour alone.
  - The text report is linear "label: value" lines, with no ASCII tables. The CSV has a single header row with units in the column names.
- **Report language:** chosen at export time (defaults to the UI language); findings re-render from their message keys. CSV stays culture-invariant, with English column identifiers, a . decimal separator and UTC ISO 8601 times.
- **Speed-test data use** is stated next to its setting (up to about 300 MB per manual check). It never runs in the background or on metered connections.
- **Network disclosure:**
  - No telemetry = no analytics uploads. Probe operators see normal connection metadata.
  - No credentials, cookies, notes or user content leave the PC.
  - OS-level WER crash data reaches Partner Center; disclosed.
- **Encryption:** local data and exports are unencrypted (§10).

**Threats** (full model: docs/threat-model.md):

| Threat | Mitigation |
|---|---|
| Probe zone or registrar hijack aims installs at a victim | Pinned prefixes, rate caps, NxDomain retirement |
| Hostile or captive responses | Bounded reads, no redirects, remote content never rendered, address pinning |
| Report data leak | Redaction defaults, preview, opt-in |
| HTML/CSV injection via names or notes | Escaping, formula neutralization |
| Same-user DB tampering | Out of scope (per-user permissions; no encryption by decision). Validate on read; never execute stored content |
| Supply chain | Locked restore, SBOM, vulnerability scan, Store signing |

## 16. Testing and acceptance

**Scheduling and alerts:** `RecurringChecks` and `AlertPolicy` are tested with FakeTimeProvider (interval, run-now, disable, minimum interval, manual change/reminder/recovery, per-check background alerts with first-seen time, reconnect results at every level), view-model tests cover reconnect and background notifications, notice warning/dismissal and background check length, and `HealthEvaluator` is tested with synthetic series (outage localization, silent router, loss, delay, variation, user limits).

**Replay tests:**

- FakeTimeProvider + fake probes; no public internet in CI.
- Fixtures = CaptureRecord JSONL + expected findings (golden), labelled synthetic or lab-recorded. Lab captures with injected impairments form the calibration corpus.
- Reports have golden-file tests.

**Fixture cases** (input → expected):

| Input | Expected |
|---|---|
| ICMP blocked, TCP/HTTPS ok | Capability Unavailable, no outage |
| All silent from start, link up | R04 (not a capability gap, not R11) |
| Gateway silent, externals ok | R02 |
| Gateway responding, then failing with externals | R03 |
| Gateway ok, both operators fail | R04 "beyond your router" |
| One operator fails | R06 only |
| DNS failing, direct IP ok | R05 |
| NxDomain on a probe name | Endpoint config |
| TLS error / redirect / 407 | R07 with the exact category |
| Gateway + external excursions at a marker | R08 |
| External-only excursions | R09 |
| Gateway-only excursions | None |
| Proxy configured, direct TCP fails | Excluded from R04 |
| Tunnel suspected | R11, no localization |
| Wi-Fi consent denied | Capture continues; capability recorded |
| Sleep, stall, wall-clock jump, restart | Gap / anchor / Interrupted; never probe loss |
| IPv6-only failure | Family-specific finding |
| Route change | New segment, no pooling |
| IPv6 temporary-address change | No split |
| Healthy capture with marker | R10 |
| Shuffled input | Identical findings |

**Unit tests:**

- **Metrics:** empty/all-timeout streams, p95 threshold and boundary, zero denominators, gaps, family/target separation, baseline/MAD windows.
- **Comparison:** unequal counts, Ethernet change, manifest change, multiple changes, runs far apart.
- **Storage:** validate_schema.py cases in xUnit, plus newer-DB refusal, rollback, backpressure, partial shutdown, retention, read-during-write.
- **Reports:** redaction, HTML escaping, CSV injection, Unicode, timestamps, notes opt-in, atomic write.

**Windows lab** (standard user; packaged and unpackaged; x64 and arm64):

- Ethernet and Wi-Fi together; VPN; proxy/PAC (never collect credentials).
- Consent denied; no Wi-Fi adapter.
- S3 and Modern Standby (QPC, gaps); restart mid-capture.
- IPv4/IPv6 changes; disconnected gateway; captive network.
- Controlled delay, loss, bufferbloat and Wi-Fi interference for R03–R09 (never on a customer network).
- A CPU-heavy game running (no false excursions).
- Multi-monitor, DPI, high contrast, keyboard-only, tray lifecycle.

**Accessibility:**

- **Automated (CI gate):** an Axe.Windows scan of every view in the UI tests, with zero errors allowed. A keyboard-only journey test. An axe-core scan of the golden HTML reports.
- **Manual (every release):** the full journey (symptom → report) with each of:
  - Narrator, NVDA and JAWS
  - Voice Access
  - Magnifier at 200–400 %
  - all four contrast themes
  - text size 225 %
  - Windows colour filters
  - Sticky Keys and keyboard only
- **Usability:** sessions include participants who use assistive technology.

**Localization:**

- **CI:** every language has every key, the plural forms its CLDR rule requires, and no unknown placeholders; plural rendering is tested per language.
- **UI tests:** run once in a pseudo-locale (expanded, accented text) and once in Arabic, to catch hard-coded strings, clipping and mirroring.
- **Lab:** check each language at 225 % text size in a contrast theme with Narrator.

**Budgets** (targets, not measured results):

- No probes while idle (with background checks on, idle means between checks).
- Capture CPU <1 % average on a named reference machine (state the normalization).
- Working set <200 MiB.
- Marker acknowledgement <150 ms.
- Stop/cancel ≤5 s, unless a documented OS call can't be interrupted.
- Measure packets and bytes during a game or call; never claim "zero impact".
- A missed budget blocks advertising that budget until it's fixed.

## 17. Build and delivery

**Work packages.** Each one follows: failing test → implement → pass → review → commit.

1. Scaffold; pin SDK and packages; Windows shell.
2. Core models and contracts on TimeProvider; invariants.
3. Storage (factory, runner, schema, writer/reader); port the validation.
4. Scheduler and channel with fake probes (budgets, cancellation, overlap, gaps, backpressure).
5. Route/interface adapters + ICMP (standard user).
6. TCP, DNS (DnsQueryEx) and HTTPS probes; manifest validation.
7. Optional WLAN/power; consent-denied and suspend paths.
8. Metrics, rules (incl. latency), comparison and next-action planner, against fixtures.
9. UI journey, marker/tray/hotkey, exports; accessibility gates (§14, §16) from the first view onward.
10. History, retention, accessibility, restart recovery.
11. Lab matrix and calibration, endpoint rights, packaging/signing, clean installs, Store submission.

```powershell
# Windows 11 (local and CI)
dotnet build ConnectionClue.slnx -c Release
dotnet test --solution ConnectionClue.slnx -c Release
pwsh tools/generate-icons.ps1
pwsh tools/build-release.ps1 -Version 1.0.7  # → releases/1.0.7/ (git-ignored; publish via GitHub Releases)
```

- Run `python tools/validate_schema.py` for the SQLite schema checks. WiX 5 is pinned in `dotnet-tools.json` and restored by the release script.
- **Packaging** (`tools/build-release.ps1`): runs from the .NET SDK alone, with no Visual Studio and no Windows SDK install.
  - **Publish:** self-contained, per architecture (x64, arm64).
  - **MSIX:** each architecture is packed with makeappx and the two are bundled into `ConnectionClue_<v>.0.msixbundle`. makeappx and signtool come from the `Microsoft.Windows.SDK.BuildTools` NuGet package. The manifest template is `packaging/msix/AppxManifest.xml`, with the assets generated by `tools/generate-icons.ps1`.
    - **Store identity** (Partner Center > Product identity; the script defaults): Name `BulentOzkir.ConnectionClue`, Publisher `CN=06D08AF4-6BB1-40DF-9B96-5DF27BEE0635`, PublisherDisplayName `Bulent Ozkir`; package family name `BulentOzkir.ConnectionClue_ghsxnkq5jxyxm`, Store ID `9MZTQBK8XJ03`. Partner Center rejects any other identity or display name.
  - **MSI:** one `ConnectionClue-<v>-<arch>.msi` per architecture, built with WiX 5 (pinned in `dotnet-tools.json`) from `packaging/msi/ConnectionClue.wxs`.
    - Per-machine, into Program Files, with an advertised Start menu shortcut.
    - Fixed UpgradeCode with MajorUpgrade, so newer versions replace older ones and downgrades are blocked. The manufacturer (Apps list publisher) is `-PublisherDisplayName`, which is also the winget publisher and matches the assembly `Company`.
    - MSI installs no services, scheduled tasks or startup entries. Windows startup is off by default; an explicit user opt-in adds a per-user Run entry, while MSIX startup is controlled by Windows.
    - Later WiX major versions changed their licence terms (Open Source Maintenance Fee), so review before upgrading.
  - **Also produced:** symbol zips per architecture (PDBs never ship in packages) and `SHA256SUMS.txt`.
- **Signing:** MSIX and MSI signatures are separate, because an MSIX signature must name the manifest publisher, which for the Store is the `CN=<GUID>` above.
  - **MSIs:** `-CertificateThumbprint`, or the self-signed test certificate `CN=ConnectionClue Test` (created once in `Cert:\CurrentUser\My`, public part exported as `ConnectionClue-test-signing.cer`). The Store never signs MSIs, so MSI distribution needs a real code-signing certificate.
  - **Bundle:** a certificate whose subject equals `-Publisher`: `-CertificateThumbprint` when it matches, otherwise a self-signed test certificate for that subject (exported as `ConnectionClue-msix-test-signing.cer`, needed only for sideloading, in Trusted People (LocalMachine)). Upload the bundle to Partner Center as is: Store submissions need no trusted signature, and the Store replaces it.
- **Verification:** 1.0.7 was built with the release pipeline; all 455 tests passed in Release configuration.
  - Settings, including the new background check length, was rendered in all 20 UI languages: 63 tab/window layouts fit without scrolling (960×740 across languages, plus 900×600 in English). The Checks tab still fits at 900×600 while the offline notice or the reconnect result notice with its Dismiss button is shown.
  - The MSIs are signed by the self-signed `CN=ConnectionClue Test` certificate (same thumbprint as earlier releases) and the bundle by the self-signed test certificate for the Store publisher; each signer matches its exported `.cer`. Windows trust was not changed; production MSI signing and clean-install validation remain release requirements.
  - Bundle holds x64 and arm64 (422 and 421 files, executable 1.0.7.0) with the Store identity `BulentOzkir.ConnectionClue` 1.0.7.0, Publisher `CN=06D08AF4-6BB1-40DF-9B96-5DF27BEE0635` and PublisherDisplayName `Bulent Ozkir`; hashing that publisher reproduces the family name `BulentOzkir.ConnectionClue_ghsxnkq5jxyxm`. The first 1.0.7 build used the test identity and was rejected by Partner Center for its PublisherDisplayName. Both MSI databases report ProductVersion 1.0.7, the expected architecture (x64, Arm64), the fixed UpgradeCode and manufacturer `Bulent Ozkir`; the winget publisher and the executable's company match.
  - Read-only WiX extraction of the x64 MSI produced 414 payload files; executable, app and Presentation assemblies are 1.0.7.0. Its embedded Help matches `helpme.md` byte-for-byte. The compiled window contains the background check length field and the notice's warning state and Dismiss command; the neutral resources contain the new alert, notice, setting and hour-plural strings, and all 19 satellites contain the new alert titles. No app was installed or launched, and no user settings were read or modified.
  - winget validated the generated manifests; MSI hashes and product codes match them. Every entry in `SHA256SUMS.txt` matches the release files.
  - ICE validation (`wix msi validate`) needs an elevated shell, so it is a release-checklist step.
- **Size:** bundle 149 MB, but the Store delivers only the matching architecture (~74 MB). MSIs are 55–60 MB. The biggest cut would be replacing WinForms `NotifyIcon` with a Shell_NotifyIcon wrapper, which removes the WinForms runtime.
- **Record** the verified packaging and signing commands in docs/release-checklist.md.
- **Status:** WP1, WP2 (probe/network slice), WP5, WP6 (probes) and WP7 are implemented and tested (§19). A preview App, the health evaluator, the configuration advisor, the background scheduler and 1.0.7 packages also exist. The preview also has a verdict evaluator for R01–R05 and R07–R11 (`VerdictEvaluator`, with link evidence from `InterfaceMonitor` and `WlanMonitor` during each check), the provider report (HTML and print-to-PDF, local times), per-symptom service targets, a hop view summary, a DNS comparison with a consented switch, a Wi-Fi channel analyzer, a daily quality score, the taskbar jump list and winget manifests. Still to do: Capture/storage, R06 (needs a second independent operator), evidence levels and full §12 marker windows, the remaining views, and manifest loading.

## 18. Release blockers and done

**Blockers:**

- Endpoint rights and rates; CDN endpoints live under product-owned names; cost budget approved.
- **Throughput service:** the preview uses a public speed-test endpoint (LAB only). Release needs a licensed or product-owned download/upload service (uploads need a sink, i.e. server-side code), sized for up to ~300 MB per manual check.
- **Symptom service targets:** `SymptomServices` lists well-known public endpoints (game platforms, Teams/Zoom/Meet, streaming services, connectivity checks) that Quick Check contacts with a TCP handshake only. Confirm acceptable use, or replace them with product-owned targets, before release; hosts can move, so review them each release.
- Proposed calibration targets met on the labelled corpus:
  - ≥95 % recall for outages ≥5 s (R01/R03/R04);
  - ≥90 % correct Wi-Fi vs upstream localization (R08/R09);
  - ≤1 false finding per 10 healthy captures.
- Verified on real Windows: QPC across sleep, route/proxy/VPN attribution, Wi-Fi consent with package identity.
- Name and Store listing clearance, build matrix, signing: a real code-signing certificate for the MSI and the Partner Center identity for the bundle (1.0.x packages are test-signed).
- Accessibility: the automated gates pass, and the manual matrix (§16) completes the full journey.
- The 19 non-English resource files are machine-drafted seeds. They need professional translation and in-country review, glossary included. Store listings must be localized.

**Done:** a fresh standard-user install completes **symptom → capture → marker → understandable finding → user change → comparison → redacted report**.

- The blocked-ICMP, silent-gateway, outage-at-start, sleep, denied-consent, proxy and inconclusive cases stay truthful.
- No account or subscription; the app runs as a standard user and never elevates itself. The only elevated action is the user-selected DNS switch or restore, which runs one fixed PowerShell command through the Windows administrator prompt. The per-machine MSI install may require administrator consent. There is no user-data backend, hidden setting change, or unapproved endpoint traffic.
- Passing tests ≠ user value. Before a broad launch, watch nontechnical users complete the journey, including people who use assistive technology, and compare against an existing free alternative.

## 19. Artifacts

- **schema.sql:** migration 001.
- **Schema validation:** `tools/validate_schema.py` runs 35 checks on SQLite 3.50.4. They cover the runner-owned transaction with WAL set outside it, STRICT, CHECKs, composite FKs, FK index coverage, append-only triggers, cascading retention delete, and checkpoint + VACUUM.
- **Release tooling:** `tools/generate-icons.ps1` creates the app/tray ICOs, MSIX visual assets and nine logo PNGs; `tools/build-release.ps1` builds the MSIX bundle and x64/arm64 MSIs. WiX 5 is pinned in `dotnet-tools.json`.
- **Code** (291 tests pass on Windows 11 25H2 x64, with network):
  - **Core:** probe models, contracts, outcome classification, schema-mirroring observation invariants, address policy and SessionClock.
  - **Windows:** RouteProvider (GetBestRoute2 + GetIfEntry2), InterfaceMonitor, ACM-only WlanMonitor, window-less PowerMonitor, and the ICMP, TCP (TCP_INFO RTT), DNS (DnsQueryEx, cache bypass) and HTTPS (cold fetch, issuer capture, prefix pinning) probes.
  - **Presentation:** AnnouncementPolicy (throttles and coalesces screen-reader announcements) and localization (LanguageResolver, CLDR PluralRules, Localizer, PseudoLocalizer), with seed resources for 20 languages. Background: RecurringChecks, AlertPolicy and SettingsViewModel. Core also holds MarkerWindow (Standard/Extended reaction allowance).
  - **Tests:** integration tests exercise the real OS and check every observation against the schema invariants.
  - **App (preview):** a WPF window that runs the real probes against a visibly marked LAB manifest (example.com).
    - Top navigation bar and pages (Check, Recommendations, Settings); balanced newspaper columns for recommendations (`Controls/FlowColumns`); role-coloured buttons and themes (`ThemeManager`); toggle switch; a path graphic with status badges; a per-step details chart (`Controls/Timeline`); `Controls/AccessibleGroup` exposes path steps, chart rows, recommendation cards and headings to UI Automation. It starts maximized.
    - Notification-area icon with status badges (`TrayIcon`; icons from `tools/generate-icons.ps1`) and a Settings page for background checks and limits.
  - **Analysis:** StepStatistics, HealthEvaluator (preview health rules, cadence-aware outage runs), NextActionPlanner and ConfigurationAdvisor (§12). The full R01–R11 engine is still to do.
  - **Windows:** also SystemInspector, a read-only snapshot of adapter, driver, TCP, power and proxy settings with their names (registry, WMI, power APIs, WinRT), and AppNetworkUsage (per-app traffic, standard user).
  - **App test hooks:** `--theme <name>` starts in a theme and `--switch-theme <name>` changes it at run time, the path the Settings page uses. `--page <Check|Recommendations|Settings>` opens a page. `--offline` pretends there is no network. `--snapshot out.png [--size 1240x768] [--start]` renders the window with WPF's software renderer after it settles (after the check with `--start`) and exits. It works without a visible desktop (CI agents, a locked PC).
  - **Presentation:** also SavedResult/IResultStore (restored recommendations with staleness) and the 10–60 s quick and background check-length settings.
    - Fluent/system theme, per-monitor DPI, RTL, UIA notifications, and a Settings page with the language picker. English is the default and the choice is saved.
- **Not yet implemented or verified:** capture/storage, the R01–R11 rule engine, reporting, the remaining views, packaging, an end-to-end alert test, diagnosis accuracy, sales.

## 20. Key decisions (vs. prior draft)

| Decision | Reason |
|---|---|
| R08/R09 latency rules | Headline symptoms are latency problems; the rules are self-baselined |
| R04 covers outage-at-start; evidence-based ICMP capability | The old 15 s check reported outages as "ICMP blocked" |
| DNS cache bypass | Cached lookups every 15 s measured nothing |
| Fresh HTTPS connection per check | Comparable timings; no stale-pool artefacts |
| OS-reported RTT | User-mode timing inflates under a game's CPU load |
| Fast/slow pools; TCP escalates to 1 s | A global cap starved slow probes, and 15 s TCP couldn't feed the rules |
| Monotonic offsets + UTC anchors; TimeProvider | Removes the dual-clock ambiguity; deterministic tests |
| Heartbeat-detected gaps | Power notifications are unreliable (Modern Standby) |
| Per-observation attribution; request vs observed family | Per-segment attribution and "Mixed" were ill-defined |
| Terminal states + EndReason | Stop vs Cancel was ambiguous |
| AnalysisRun per rules version; same-rules comparisons | Reproducible reports; a stable measuring stick |
| Evidence writer/reader; typed details | The old store couldn't carry events or serve reads |
| Names in independent zones; NxDomain retirement; pinned prefixes | Control, a kill switch and a bounded hijack radius at $0.99 |
| ACM-only Wi-Fi baseline; no event-log import | MSM needs consent; the log duplicated evidence |
| STRICT, composite FKs, FK indexes, DDL-only migration | Integrity enforced by the schema itself; the old file couldn't run inside a runner transaction |
| Investigation-level retention | Deleting sessions orphaned comparisons |
| 24H2 floor, x64+arm64, .slnx without .wapproj | One consent model; ARM PCs; dotnet build works |
| CLI packaging: makeappx from NuGet plus WiX 5, no .wapproj | Reproducible from the .NET SDK alone; one script builds the bundle, MSIs, signatures and checksums |
| Single-file atomic exports, HTML print CSS | No partial writes; no PDF dependency |
| 20 UI languages merged by written locale; resx with CLDR plural keys; switch on restart | Covers most of the world's speakers with standard .NET tooling. Plurals are correct in every language. Restarting avoids half-translated screens |
| Accessibility as a release gate; Presentation layer; Extended marker allowance | Disabled users must be able to complete the whole journey. The policy is testable without WPF. Fixed reaction windows would penalise motor and cognitive impairments |
| In-process background checks, on by default, alerting on every problem check | Product decision: monitoring is the default. No service, task or login item, disclosure and a one-click off keep it transparent. Users asked to hear about every unhealthy background result; repeats say since when the problem continues, recovery is reported once, and manual checks keep change-based alerts to limit fatigue |
| User-set limits instead of verdicts | Performance alerts need thresholds; the user's own, visible limits keep the no-verdict principle |
| Small-multiples details chart | Different quantities (ICMP, TCP RTT, HTTPS fetch) each get their own scale; no overlapping lines |
| Top navigation bar; status and evidence in a top band; chart across the full width | Standard Windows 11 top-navigation structure with a clear hierarchy (status → what to do → evidence). A side pane and a tall status column left empty space on the left; the chart, the widest evidence, gets the full width |
| Recommendations persisted as codes, with their check time first | Survives restarts; re-renders in any language; the user always sees how old the advice is |
| One colour per action role, always with an icon and a label | Easier to recognise for low-vision and cognitive disabilities; never colour alone; high contrast uses system colours |
| On-demand speed test with latency under load; idle-only verdicts | The user asked for throughput. Keeping it manual, capped and off on metered connections bounds cost and data use. Loaded latency explains lag while someone uploads |
| Read-only configuration advisor, gated by symptom and issues | Users need to know which OS, driver and power settings hurt their connection. Reading (never writing) keeps the app standard-user and safe. Gating and Important/Suggestion levels avoid noise on healthy PCs |
| "Checked, no change needed" list and one-click helpers | A healthy PC otherwise showed nothing, which looked like nothing was checked. Helpers turn instructions into one click without the app changing settings; allow-listed targets keep them safe |
| Lag marker as a chart flag | A button that only said "recorded" was unexplained and unverifiable; the flag ties the user's moment to the evidence |
| Online AI review of generic advice text, two provider-distinct votes to hide, shown unchecked when offline | The user wanted every recommendation verified by online LLMs. The request body excludes check/device data; providers still see normal HTTPS metadata such as the public IP. Two distinct services stop one model/provider's hallucination from hiding correct advice. Offline is exactly when help is needed |
| No checks without any network; an explicit "!" warning | Measuring nothing produces misleading results; the warning tells the user what to fix first |
| No local encryption | Product decision; OS permissions + disclosure |

## Sources

[1] https://learn.microsoft.com/en-us/windows/win32/nativewifi/wi-fi-access-location-changes
[2] https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
[3] https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-getbestroute2
[4] https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-notifyroutechange2
[5] https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-notifyipinterfacechange
[6] https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/nf-wlanapi-wlanregisternotification
[7] https://learn.microsoft.com/en-us/dotnet/api/system.net.networkinformation.ping.sendpingasync?view=net-10.0
[8] https://learn.microsoft.com/en-us/windows/win32/winsock/sio-tcp-info
[9] https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsqueryex
[10] https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connectcallback?view=net-10.0
