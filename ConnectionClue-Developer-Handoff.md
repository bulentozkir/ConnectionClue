# ConnectionClue — Developer Handoff

**Type:** design and delivery contract. No implementation exists yet.
**Product:** Windows home-connection troubleshooter, sold as a $0.99 one-time Store purchase. It has no account, subscription, telemetry or user-data backend.
**Goal:** capture a reported problem and localize it with measured evidence: this PC ↔ router, beyond the router, name resolution, or a single service. Then guide one safe change, compare the before/after runs, and export a readable report.

Defaults are proposed policy pending lab calibration, not validated thresholds. Ambiguous and unavailable outcomes stay visible: the app never invents a fault to justify its price. §20 gives the rationale for the key decisions.

## 1. Scope

Workflow: **symptom → capture → finding → one change → compare → export.**

- **Symptoms:** gaming lag, buffering video, choppy calls, disconnections. Ask whether the problem happens on this PC. If it happens on another device, state that measurements cover this PC only, and advise capturing from the same connection type and location.
- **In v1:**
  - 2-minute quick check, plus optional 15/30/60-minute captures.
  - Link, route and Wi-Fi events, plus ICMP, TCP, system DNS and small HTTPS probes.
  - Symptom marker ("It lagged just now").
  - Timeline with gaps and markers.
  - Evidence cards, each with one next action.
  - Before/after comparison confirmed by the user.
  - Local history and an action checklist.
  - Redacted HTML, text and CSV exports.
- **Not in v1:** speed or saturation tests; packet capture; overlays or injection; automatic repair; DNS, router, VPN or driver changes; a Windows service; startup or scheduled monitoring; remote agents; AI diagnosis; cloud sync; a payment backend; in-app purchases.
- **Wording limits:** no universal score, no "ISP guilty" label, no promised lag reduction. Localization is allowed ("the delay appears beyond your router"); blame is not.

## 2. Platform and stack

- **OS and CPU:** Windows 11 24H2+ (10.0.26100), x64 and arm64 in one MSIX bundle. With this floor, every supported build has the same Wi-Fi location-consent model (introduced in build 25976 [1]). Confirm the floor against the servicing calendar at kickoff.
- **Runtime:** .NET 10 LTS [2], C#, WPF. Pin the SDK in global.json. Use central package versions with lock files (`RestoreLockedMode` in CI).
- **Target frameworks:**
  - Windows projects: `net10.0-windows10.0.26100.0`, with `SupportedOSPlatformVersion` and the manifest `MinVersion` both set to 10.0.26100.0, so the analyzer enforces the real floor.
  - Portable projects (Core, Capture, Analysis, Storage, Reporting): `net10.0`.
- **Libraries:**
  - CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, xUnit, System.Text.Json source generation.
  - Microsoft.Windows.CsWin32 for the IP Helper, WLAN, DNS and power APIs. Wrap handles in SafeHandles, and keep callback delegates rooted for as long as the registration lives.
- **Packaging:** self-contained, full-trust MSIX. Declare the `wiFiControl` capability, used only for optional Wi-Fi detail (§7.2). No trimming or Native AOT, because WPF doesn't support them.
- **Store and UI:** the Store enforces entitlement, so there is no license server. The UI is native WPF, with no WebView and no localhost server.

## 3. Repository layout

```text
ConnectionClue.slnx              dotnet-buildable projects only (packaging excluded, §17)
Directory.Build.props  Directory.Packages.props  global.json
src/ConnectionClue.Core/         Models/, Contracts/ (IProbe, IEvidenceWriter, IEvidenceReader,
                                 INetworkContextProvider, IClock), Endpoints/ (manifest model + validator)
src/ConnectionClue.Capture/      SessionController, ProbeScheduler, CoverageTracker, EvidenceChannel
src/ConnectionClue.Windows/      RouteProvider, InterfaceMonitor, WlanMonitor, PowerMonitor, Probes/
src/ConnectionClue.Analysis/     MetricsCalculator, RuleEngine, Rules/, ComparisonEngine, NextActionPlanner,
                                 Policies/rules-v1.json
src/ConnectionClue.Storage/      ConnectionFactory, MigrationRunner, Migrations/001_initial.sql (= schema.sql),
                                 SqliteEvidenceStore (single writer + snapshot reader), RetentionService
src/ConnectionClue.Reporting/    ReportModelBuilder, Redactor, Html/Text/CsvReportWriter, AtomicFileWriter
src/ConnectionClue.App/          Views/, ViewModels/, Resources/, CompositionRoot, SingleInstance,
                                 TrayController, HotkeyController, SettingsService
config/endpoints.production.json
packaging/                       ConnectionClue.Package.wapproj, Package.appxmanifest, Assets/
tests/                           {Core,Capture,Analysis,Storage,Reporting}.Tests, Windows.IntegrationTests, Fixtures/
tools/validate_schema.py
docs/                            endpoint-approval.md, diagnosis-language.md, privacy.md, release-checklist.md
```

**Dependencies point inward:**

- Core has no UI, Windows or database dependency.
- Capture uses Core contracts. Windows and Storage implement them.
- Analysis is pure (no I/O).
- Reporting consumes typed models only.
- App wires everything together.

Portable projects run on Linux CI against synthetic fixtures. Windows adapters, WPF and MSIX need Windows CI or the lab.

## 4. Process and concurrency

- **Process:** one normal-user process with a per-user single instance (named mutex; a second launch activates the window). One active capture per user.
- **Closing during capture:** closing the window asks the user to stop or to continue in the tray. A tray capture still ends at its deadline; there is no always-on monitoring.
- **Exit:** stop scheduling → cancel in-flight probes → flush pending writes (≤5 s) → release callbacks, hotkey, tray icon and connections. Never unregister a native notification (`CancelMibChangeNotify2`, `WlanCloseHandle`, power) from its own callback thread.

```text
WPF commands → SessionController → ProbeScheduler + event monitors
  → bounded EvidenceChannel<CaptureRecord> → single SQLite writer (batched commits)
  → snapshot reader → metrics/rules → UI snapshots, ReportModel
```

- **CaptureRecord** is one of: ProbeObservation, ConnectionEvent, CoverageGap, PathSegment start/end, SessionCapability, SymptomMarker, ClockAnchor, or a session state change.
- **Sizing:** about 7 records/s (about 25k per hour). Channel capacity is 4096, about 10 minutes of records. The writer commits every 1 s or every 250 records.
- **Backpressure:**
  - Full channel: the scheduler stops probing, and CoverageTracker opens a `StorageBackpressure` gap that is persisted once the writer recovers.
  - Writer failure: the session ends as Failed/StorageFailure and the UI stops claiming it is recording. Evidence up to the last commit stays usable.
  - Records are never dropped silently.
- **Threading:** async I/O only; nothing runs on the dispatcher. Native callbacks only enqueue bounded messages.
- **Reads:** readers use separate WAL connections. Analysis and reports each read from one transaction snapshot. The live UI shows a labelled provisional preview, updated at most once per second. Graphs are downsampled for display only.

## 5. Session lifecycle and time

Preparing → Capturing ⇄ Suspended → Stopping → terminal state:

| Terminal state | EndReason |
|---|---|
| Completed | Deadline, UserStopped, AppExit, SleptPastDeadline |
| Failed | StorageFailure, InternalError |
| Interrupted | ProcessLost (an active session found at startup; never resumed) |

- **Stop** ends the session as Completed/UserStopped. Evidence is judged by coverage, not by the terminal state. Cancelling during Preparing deletes the stub.
- **Preparing** validates the duration, manifest, network context and storage.
  - A disconnected adapter is context for the finding, not an app error.
  - An invalid manifest is a build defect: capture refuses to start and shows a setup error, never an internet finding.

**Time model**

- **Canonical time** is the µs offset from session start on the monotonic clock (Stopwatch/QPC through IClock). Ordering, durations, the deadline and gap detection all use offsets only.
- **UTC is for display only.**
  - `Session.StartedUtc` anchors offset 0.
  - ClockAnchor rows re-anchor after a resume, or after a detected wall-clock jump (UTC vs monotonic drift above 2 s, which also records a WallClockChanged event).
  - Wall-clock changes never alter the timeline, extend a session or create gaps.
- **Deadline:** start + planned duration on the monotonic clock. Sleep consumes the window. Waking past the deadline ends the session as SleptPastDeadline.
- **Gaps:** a heartbeat gap longer than 3 s opens a CoverageGap.
  - Reason is Sleep when a suspend/resume signal brackets the gap, otherwise SchedulerDelay. Power notifications can be late or missing, for example under Modern Standby.
  - Missed slots are never backfilled as timeouts.
  - `ClockDiscontinuity` is reserved for monotonic-clock anomalies.
- **Release blocker:** verify in the lab that QPC keeps counting across S3 sleep and Modern Standby. If it doesn't, derive offsets from a clock that includes suspend time.

## 6. Probe plan

| Stream | Target | Cadence | Timeout |
|---|---|---|---|
| Gateway ICMP | default gateway, per family | 1 s | 800 ms |
| External ICMP | 2 independent operators × available families | 1 s; 15 s re-check while Unavailable | 800 ms |
| External TCP | same operators, approved port, literal IP | 15 s; 1 s while that target's ICMP is Unavailable | 3 s / 800 ms |
| System DNS | 2 approved names × A and AAAA | 15 s | 3 s |
| HTTPS | 2 approved endpoints, independent operators | 15 s | 5 s |

- **Families:** probe a family only while it has a default route in the current segment. The gateway is ICMP-only; a silent gateway means limited visibility (R02).
- **Budgets:**
  - Fast pool (1 s streams): up to 6 concurrent. Slow pool (15 s streams): up to 4. A stream moves between pools when its cadence changes.
  - At most one in-flight operation per (target, kind, family).
  - Every timeout is shorter than its stream's cadence.
  - Slow probes are staggered across the cycle.
  - A slot that can't run records Skipped with a reason. No catch-up.
- **Backoff:** 15/30/60 s per endpoint, only for refusals signalled by the endpoint: 429 (honour Retry-After), 503, contract mismatch. Timeouts and network errors never trigger backoff.
- **Traffic:** small diagnostic requests only; no load generation. Report payload bytes and estimated wire bytes separately.
- **Cancellation:** abort supported operations and close per-operation sockets. The result is Cancelled, never Timeout. Calls that can't be interrupted are capped and not replaced, and results arriving after the session closes are discarded.

## 7. Windows integration and measurement truth

### 7.1 Context, routing, segments

- **Adapters:** `GetIfTable2`/NetworkInterface.
- **Routes:** `GetBestRoute2` [3] gives the per-destination route and source. It is a prediction, not proof of the packet path. Refresh it every probe cycle and on `NotifyRouteChange2` [4] and `NotifyIpInterfaceChange` [5].
- **Link state:** `NotifyIpInterfaceChange` + `GetIfEntry2` (OperStatus, MediaConnectState). This needs no consent and is the baseline evidence for R01.
- **Segments:** the key is, per family, (interface LUID, next hop) plus the effective proxy mode.
  - A key change opens a new PathSegment. Metrics never pool across segments.
  - Address-only changes, such as IPv6 temporary addresses, are events, not segment breaks.
  - Time without a usable route is a ContextTransition gap.
- **Attribution (per observation):** SocketObserved (connected socket endpoints), RoutePredicted (ICMP, or before connect), Proxied (HTTPS through the system proxy or PAC), Unknown.
- **Tunnels:** a tunnel is suspected when the best-route interface to external targets lacks the `MIB_IF_ROW2` HardwareInterface flag or is a tunnel or PPP type. Never infer it from adapter names. A suspected tunnel suppresses Wi-Fi-vs-upstream localization.
- **Path choice:** follow the OS-selected path and never bind to an adapter. For an Ethernet comparison, the user changes the setup and the next run verifies the interface actually used; a plugged-in cable is not proof.
- **Families:** IPv4 and IPv6 are separate streams. On-link IPv6 next hops keep their scope IDs.

### 7.2 Wi-Fi and power

- **Baseline (no consent):** `WlanRegisterNotification` [6] with `WLAN_NOTIFICATION_SOURCE_ACM` only, for connect/disconnect events and reason codes.
- **Optional detail:** MSM notifications (roaming, signal quality), `WlanQueryInterface(current_connection)` and SSID/BSSID all require `wiFiControl` and location consent [1].
  - Check with `AppCapability.CheckAccess`.
  - Prompt only from an explicit "add Wi-Fi detail" action. Never at first launch, and never repeatedly.
  - Denied or unavailable becomes a SessionCapability state; the other probes continue.
- **No WLAN event-log import** in v1.
- **Power:** suspend/resume notifications cancel in-flight work and bracket gaps (§5). No blocking or database work on callback threads.

### 7.3 ICMP

- Use `Ping.SendPingAsync(IPAddress, TimeSpan, byte[], PingOptions, CancellationToken)` [7], with one Ping instance per stream (instances are not concurrency-safe). Resolve names outside the timed part.
- **Latency** is the OS-reported RoundtripTime (1 ms resolution, TimingSource OsReported). Stopwatch is used only for scheduling and timeout bookkeeping.
- **Errors:** unreachable replies map to NetworkUnreachable or HostUnreachable, with an ErrorCode such as AdminProhibited or TtlExpired. No reply is Timeout.

### 7.4 TCP

- Use `Socket.ConnectAsync` to an approved literal IP:port, with cancellation, and close immediately.
- **Duration:** TCP_INFO `RttUs` via `SIO_TCP_INFO` [8] when available (OsReported), otherwise the Stopwatch connect time (UserMode). Record the local and remote endpoints (SocketObserved).
- **Refused** means the path answered. It is neither silence nor proof of service health.
- **Proxies:** TCP is always direct. When a proxy or PAC is configured, label the streams "direct while proxy configured" and exclude them from R04/R06. Never bypass a mandated proxy for HTTPS.

### 7.5 System DNS

- Use `DnsQueryEx` [9] asynchronously, with `DNS_QUERY_BYPASS_CACHE` and `DnsCancelQuery`.
  - A queries form the IPv4 stream and AAAA queries the IPv6 stream.
  - This keeps the system resolver path (configured servers, NRPT, VPN policy, Windows DoH) without reading or flushing the OS cache.
- **Reporting:** report it as "system resolution time", not server RTT.
- **ErrorCode:** NxDomain, NoData, ServFail, DnsRefused, NoServers. A DNS timeout is recorded as Timeout. NxDomain or NoData for an approved probe name is an endpoint configuration issue, not a failure of the user's network.
- **Never** force public DNS, generate unique names, or query the user's own domains.

### 7.6 HTTPS

- **SocketsHttpHandler settings:** HTTPS only; default certificate validation; no cookies; no default credentials; no automatic redirects; no automatic decompression; the system proxy is honoured.
- **Fresh connection per check** (`ConnectionClose`, no pooling).
  - Duration = DNS (OS-cached) + TCP + TLS + request + body validation of at most 4 KiB. Call this the "cold fetch time".
  - One handler per proxy context, recreated on segment change.
  - RequestFamily is Any; the connected socket's family is recorded.
- **Endpoint contract:** an expected status plus a body token.
  - 407 → ProxyAuthRequired.
  - 429 → RateLimited.
  - Certificate failure → TlsFailure.
  - 3xx or mismatch → HttpUnexpected (ErrorCode Redirect, Status, Body or Oversize).
- **Redirects** are recorded (host only) and never followed. Never collect credentials or suppress certificate validation.
- **Phase timing** via `ConnectCallback` [10] is an optional later extension. Never fake DNS/TLS phase times.

## 8. Probe endpoints (release dependency)

- **Hostnames:** external targets sit behind product-owned hostnames, for example `a.probe.<domain>` and `b.probe.<domain>`. DNS indirection is the kill switch: targets can be moved or retired without an app update.
- **HTTPS:** static objects (small fixed body, `Cache-Control: no-store`) on two independent CDN operators. No server code. CDN access logging is minimized and disclosed.
- **ICMP/TCP:**
  - Use the same operators' edge addresses, only under documented terms. If terms don't allow it, run two minimal responders on independent hosting providers; failing that, pause the release.
  - Addresses are resolved at session start, outside timing, and validated as public.
  - Pinned fallback addresses in the manifest keep IP-level checks independent of DNS, which R05 depends on.
- **Cost model** (in docs/endpoint-approval.md): about 8 HTTPS requests per minute per active capture, each a tiny static response. Recurring cost against one-time revenue is accepted, capped by cadence, and reviewed against install counts.
- **Manifest entry fields:** stable id, operator id (for independence), purpose, names, fallback addresses, families, ports and paths, protocols, response contract, payload cap, cadence, timeouts and allowed rate, usage-rights reference, review date, version. No secrets.
- **Distribution:** the manifest ships in the package and each session snapshots it by content hash. There is no remote configuration.
- **Validation at build and load:**
  - Reject plaintext external requests, executable actions, credentials, and external targets that resolve to loopback, link-local, private or CGNAT ranges.
  - Release builds fail if a lab manifest is present.

## 9. Contracts

These are design contracts, not tested code. Persisted strings equal the enum names used by the CHECK constraints in schema.sql.

```csharp
namespace ConnectionClue.Core;

public enum ProbeKind { Icmp, Tcp, SystemDns, Https }
public enum RequestFamily { IPv4, IPv6, Any }        // Any = OS chooses; HTTPS only
public enum IpFamily { IPv4, IPv6 }
public enum Attribution { SocketObserved, RoutePredicted, Proxied, Unknown }
public enum TimingSource { OsReported, UserMode }
public enum ProbeStatus
{
    Success, Timeout, NetworkUnreachable, HostUnreachable, Refused, DnsFailure,
    TlsFailure, HttpUnexpected, ProxyAuthRequired, RateLimited,
    PermissionDenied, Unsupported, Cancelled, Skipped, InternalError
}

public interface IClock { long NowUs { get; } DateTimeOffset UtcNow { get; } }

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

public interface IEvidenceWriter    // bounded channel → single writer; backpressure, never drops
{
    ValueTask EnqueueAsync(CaptureRecord record, CancellationToken ct);
}

public interface IEvidenceReader    // one read transaction per call
{
    Task<SessionEvidence> ReadSnapshotAsync(Guid sessionId, CancellationToken ct);
}
```

- Targets resolve only through the session's manifest snapshot. ProbeRequest never carries URLs or addresses.
- SessionController allocates Sequence values monotonically. Commit order may differ.
- Expected network errors are recorded as observations. Unexpected exceptions become a bounded InternalError observation plus a local app log entry, never a network finding.
- ErrorCode uses a closed, versioned vocabulary per probe kind.

## 10. Storage

schema.sql is migration 001: DDL only, validated (§19).

- **Tables:** Investigation, EndpointManifest, Session, ClockAnchor, PathSegment, ProbeObservation, ConnectionEvent, CoverageGap, SessionCapability, SymptomMarker, AnalysisRun, Finding, ActionAttempt, Comparison.
- **View:** InvestigationActivity.
- **Vocabularies:** symptom, device scope, states, reasons, event kinds, capabilities and marker sources are closed lists. They are defined once, in the schema.sql CHECK constraints, and mirrored by C# enums.

**Enforced in SQL** (STRICT tables, CHECKs, composite FKs, triggers):

- An observation's segment belongs to the same session. A comparison's sessions and action belong to its investigation, and baseline ≠ follow-up.
- Status, duration and timing are consistent:
  - Success has a duration. Timeout, Skipped, Cancelled, denied and internal-error rows have none.
  - Skipped carries a reason.
  - Status, family and attribution must be valid for the probe kind.
- Terminal states carry EndReason, EndedUtc and EndedUs, and the reason matches the state.
- Observations and connection events are append-only.
- **Size caps:** notes 2,000 chars; event and context details 8 KiB; probe details 4 KiB. No raw payloads and no system logs.
- Every FK child key is indexed.

**Enforced in the app:**

- JSON matches the typed, versioned models. `json_valid` is only a backstop.
- Streams are permitted by the session manifest.
- Offsets fall within the session and a valid segment.
- Compared runs are terminal, ordered and compatible.
- ActionCode and RuleId values come from the versioned catalogs.

**Findings:** one AnalysisRun per (session, rules version). A new rules version adds a run, and old runs remain so earlier reports stay reproducible. Recomputing with the same version replaces that run in one transaction.

**Practices:**

- **Location:** data lives in LocalApplicationData (MSIX redirects it per package; verify this). Never hard-code package paths.
- **ConnectionFactory:** on every connection, set foreign_keys, busy_timeout, secure_delete and trusted_schema=OFF. Once per database, outside a transaction, set journal_mode=WAL and synchronous=NORMAL. Parameterized SQL only; one writer; bounded transactions.
- **MigrationRunner:** owns the transaction and user_version, refuses newer databases, and backs up before destructive migrations. Downgrade means restoring the backup.
- **Retention** (startup, after interrupted-session recovery): delete investigations whose LastActivityUtc (latest session end, else creation) is > 30 days old and empty investigations > 24 h old, then GC unreferenced manifests. Whole-investigation deletes never orphan a comparison; exported files are never touched.
- **Delete history** = DELETE + secure_delete + wal_checkpoint(TRUNCATE) + VACUUM. This is not forensic erasure: SSD remapping, backups and exports can still hold data.
- **No encryption** of the database or exports, by design. Per-user OS file permissions protect them, and the privacy page says so.

## 11. Metrics

Computed per stream (target + kind + family) and per segment.

- **Latency:** median and nearest-rank p95 (index ⌈0.95n⌉−1) of successful DurationUs values.
  - p95 is shown only when n ≥ 100, otherwise "insufficient samples".
  - A quick check yields at most 120 samples per 1 s stream, so p95 survives only up to 16 % loss. Recommend longer captures for tail comparisons.
- **No-reply rate (ICMP/TCP):** Timeout / (Success + Timeout), with the denominator shown.
  - Unreachable and refused are counted separately.
  - Cancelled, Skipped, PermissionDenied, Unsupported, InternalError and RateLimited are excluded.
- **DNS/HTTPS:** failure counts per category, against the endpoint contract. No universal loss percentage.
- **Probe RTT variation:** median |Δ| between adjacent successes with no failure or gap in between, spacing ≤ 2× cadence, and at least 20 pairs. Never label it VoIP jitter or MOS.
- **Scheduling coverage:** executed / scheduled eligible slots within the planned window. Timeouts count as executed; skipped, denied and internal-error slots don't. Always show it with the unobserved duration and the per-class capability.
- **No "uptime".** Show observation time and incident windows instead. Buckets are for display only, and failure counts are always visible.

## 12. Rule engine

- **Input:** the committed evidence snapshot, capabilities, segments, gaps, markers and action history.
- **Output:** typed findings stored as message keys + parameters: RuleId, rules version, time range, evidence refs as (SessionId, Sequence) ranges, facts, interpretation, next action, limitations.
- **Evidence levels:** Observed (a direct record), Correlated (independent observations align in time), Inconclusive.

**Policy** (rules-v1.json, calibrated in the lab):

- **Windows:** correlation ±5 s. Marker match [−20 s, +5 s], because users react after the event. Report marker context ±30 s.
- **Failure episode:** ≥3 consecutive eligible failures on 1 s streams, or ≥2 on 15 s streams.
- **Recovery:** 3 consecutive successes. Report first recovery and confirmation separately.
- **ICMP capability:** Unavailable only while ICMP is silent and TCP/HTTPS to the same operator succeed. Re-evaluated continuously. If everything is silent, that is an outage candidate, not a capability gap.
- **Latency baseline:** per-segment median + MAD of successes (robust to excursions), from at least 60 samples. Captures longer than 15 min use a centred 10-minute window.
- **Excursion:** RTT > median + max(5·MAD, 30 ms) in at least 2 of 3 consecutive samples. There is no global "bad latency" threshold.
- **Comparison labels:** need ≥80 % coverage per run. Tail comparisons need ≥100 successes per run.

**Rules** (a localization ladder):

| Id | Finding | Condition | Never claim |
|---|---|---|---|
| R01 | Local link interrupted | Link-down or WLAN disconnect on the probed interface | Defective hardware, or a cause |
| R02 | Gateway visibility limited | Gateway ICMP is silent while external checks succeed | Router broken |
| R03 | Local network path interrupted | A gateway that was responding fails, overlapping with external failures; link up | Which device failed |
| R04 | External reachability lost | ≥2 independent operators fail on every available probe class; link up. Includes "never reachable since start". Say "beyond your router" only if the gateway kept responding | ISP fault |
| R05 | System name resolution failed | ≥2 approved names fail while direct-IP checks succeed. State whether it was a timeout or a negative answer | Resolver broken; split DNS ruled out |
| R06 | One endpoint/path affected | One operator degrades while the other still responds | Home-wide outage |
| R07 | Web validation failed | TLS failure, proxy auth, redirect or contract mismatch; state the exact category | Portal or filtering certainty |
| R08 | Delay between this PC and router | A gateway excursion overlaps (±5 s) at least 1 external excursion. Add Wi-Fi context when on Wi-Fi | Interference source |
| R09 | Delay beyond router | Excursions on ≥2 independent external targets while the gateway stays at baseline. Suggest testing household upload load (bufferbloat) | ISP fault |
| R10 | No matching problem observed | Adequate coverage around the markers (or the whole capture if there are none) with no matching failure or excursion | Network certified healthy; bandwidth |
| R11 | Unable to conclude | Missing capability, low coverage, ambiguous path (tunnel) or denied access. State what is missing and the next safe step | — |

**Guards:**

- Direct streams under a configured proxy are excluded from R04/R06.
- A tunnel-suspected segment turns R08/R09 into R11.
- Excursions on the gateway alone are ignored, because routers deprioritize ICMP handled by their control plane.

**Primary card** (total order): marker overlap → evidence level → rule priority (R01 > R03 > R04 > R05 > R07 > R08 > R09 > R06 > R02 > R10 > R11) → earliest start → RuleId.

**Stability:**

- Findings coexist, and recovered incidents stay in the report.
- A generic green status never overwrites a precise event.
- Any input order yields identical findings.

**Next actions** (catalog v1). The user performs every action. The list is filtered by the investigation's constraints and by actions already completed or skipped.

| ActionCode | Suggested by | Suppressed when |
|---|---|---|
| RetestOnEthernet | R01, R03, R08 | NoCable, or already on Ethernet |
| ImproveWifiPlacement | R01, R08 | On Ethernet |
| PauseHouseholdUploads | R09 | — |
| RestartRouter | R03, R04, R05 | — |
| DisconnectVpn | R11 (tunnel) | VpnRequired |
| CheckServiceStatus | R06, R07 | — |
| ContactIsp (with report) | R04, R05 or R09 repeating after RestartRouter | — |
| CaptureLonger | R10, R11 | — |

## 13. Comparison

- **Runs:** both from one investigation, follow-up after baseline. The user confirms which action was taken and whether more than one change occurred (`MultipleChanges`).
- **Comparability classes:**
  - **Comparable:** same probe profile, manifest hash, families and rules; only the intended context differs.
  - **Caution:** a different duration, endpoint addresses, proxy/tunnel state or coverage; multiple changes; or runs more than 60 min apart (time-of-day effects).
  - **NotComparable:** incompatible kinds, rules or targets, or unusable evidence.
- **Intended vs unexpected changes:** Wi-Fi → Ethernet is an intended change and is shown prominently. Unexpected VPN or endpoint changes mean Caution.
- **Per run, show:** sample counts, no-reply counts and rates, median, p95 (if eligible), variation, excursions, link events and gaps.
  - Show absolute differences first.
  - No aggregate "% better", no significance claims, no ratios over tiny denominators.
  - Fewer failures from fewer samples is not an improvement.
- **Labels:** "Latency improved in this sample" needs adequate data and agreement between median and tail. Otherwise the label is "mixed".
- **Method:** recommend A/B/A runs for intermittent problems.
- **Outcomes:** record them even when the change failed or wasn't possible (Failed/NotPossible).

## 14. UI

**Views:**

1. **Home:** symptom, affected device, constraints (VPN required, no cable), Quick check / Capture longer.
2. **Capture:** countdown; lanes Local (link + gateway), Internet (external ICMP/TCP) and Services (DNS + HTTPS); marker; Stop; details.
3. **Results:** primary card, limitations, next action, timeline, export.
4. **Compare:** baseline, action (Completed/Skipped/Failed/NotPossible), follow-up, cards.
5. **History:** investigations, with delete and export.
6. **Settings:** capabilities and privacy, endpoints, retention, hotkey, data path.

**Behaviour:**

- Every async command has busy, cancel and error states.
- Distinguish NotAvailable from Failed, and NoIssueObserved from "healthy".
- Everything is keyboard-reachable. Status is never shown by colour alone. Support high contrast and DPI scaling.
- **Marker:** available from a button, the tray menu, and a `RegisterHotKey` shortcut that is registered only during capture. Conflicts are reported, and the shortcut can be remapped or disabled.
  - No keyboard hooks, no overlays.
  - Outside a capture, show "Start a capture first".
  - Markers are timestamped at input and acknowledged at enqueue.
- **Timeline:** latency distributions, failures, link events, markers and grey unobserved intervals. Never interpolate across gaps.
- Recommendations are instructions for the user; the app never changes network settings.

## 15. Reports and privacy

- **Source:** export only from a typed ReportModel pinned to one AnalysisRun. Never export the database or raw logs as a support action.
- **Contents:** app and rules versions; capture dates with local UTC offset; symptom and device scope; observation time, sample counts, unavailable tests and gaps; findings with limits; marker context (±30 s, clipped); actions and comparison warnings; redaction summary; statement that probes cover this PC's paths only.
- **Formats:**
  - **HTML:** one self-contained file with inline CSS and SVG and a print stylesheet; the browser's "Print to PDF" replaces a built-in PDF. All text escaped; no JavaScript, web fonts, trackers or remote resources.
  - **Text:** for pasting into support chats.
  - **CSV:** one redacted row per observation.
  - **Writing:** one file per export. A Save dialog confirms overwrites, then the file is written atomically (temp file in the target folder, then replace).
- **Redaction defaults:**
  - Exclude SSID, BSSID, MAC, local and public addresses, host and user names, machine ids and free-text notes.
  - Use per-investigation aliases (Gateway, Wi-Fi adapter A). Hashing an SSID is not anonymization.
  - Product endpoint operators are public configuration and appear by name.
  - Users can opt in per export after a preview, with a warning that timestamps and notes can reveal activity.
- **CSV safety:** use a real CSV writer. Neutralize text cells that start with =, +, -, @, tab or CR, including after leading whitespace. Numeric columns stay typed, so negatives are kept. Covered by tests.
- **Network disclosure:**
  - "No telemetry" means no analytics uploads. Probe operators still see normal connection metadata.
  - No credentials, cookies, notes or user content leave the PC.
  - Windows Error Reporting (an OS feature) sends crash data to Partner Center; the privacy policy discloses this.
- **Encryption:** local data and exports are not encrypted (§10).

## 16. Testing and acceptance

**Replay tests:** use an injected IClock and fake probes. No public internet in CI. Fixtures are labelled synthetic.

| Fixture | Expected |
|---|---|
| ICMP blocked; TCP/HTTPS work | Capability Unavailable; no outage |
| All silent from start, link up | R04, not a capability gap and not R11 |
| Gateway silent; externals fine | R02 |
| Gateway responding, then failing with externals | R03 |
| Gateway fine; both operators fail | R04, "beyond your router" |
| One operator fails | R06 only |
| DNS failing; direct IP fine | R05. NxDomain on a probe name → endpoint config |
| TLS error / redirect / 407 | R07 with the exact category |
| Gateway + external excursions at a marker | R08 |
| External-only excursions | R09 |
| Gateway-only excursions | No finding |
| Proxy configured; direct TCP fails | Excluded from R04 |
| Tunnel suspected | R11; no localization |
| Wi-Fi consent denied | Capture continues; capability recorded |
| Sleep, stall, wall-clock jump, restart | Gap / anchor / Interrupted; never counted as probe loss |
| IPv6-only failure | Family-specific finding |
| Route change / IPv6 temporary-address change | New segment, no pooling / no split |
| Healthy capture with marker | R10 |
| Shuffled input order | Identical findings |

**Unit tests:**

- **Metrics:** empty and all-timeout streams; p95 threshold and nearest-rank boundary; zero denominators; gaps; family and target separation; baseline and MAD windows.
- **Comparison:** unequal counts; an Ethernet change; a manifest change; multiple changes; runs far apart.
- **Storage:** the validate_schema.py cases ported to xUnit, plus newer-database refusal, rollback, backpressure, partial shutdown, retention, and reads during writes.
- **Reports:** redaction; HTML escaping; CSV injection; Unicode; timestamps; notes opt-in; atomic write.

**Windows lab** (standard user; packaged and unpackaged; x64 and arm64):

- Ethernet and Wi-Fi active together; VPN; system proxy and PAC (never collect credentials).
- Wi-Fi consent denied; no wireless adapter.
- S3 sleep and Modern Standby (QPC behaviour, gap detection); restart during capture.
- IPv4/IPv6 changes; a disconnected gateway; a captive network.
- Controlled delay, loss, bufferbloat and Wi-Fi interference to calibrate R03–R09. Never alter a customer's network.
- A CPU-heavy game running: OS-reported timing must not create false excursions.
- Multiple monitors, DPI, high contrast, keyboard-only use, tray lifecycle.

**Budgets** (targets, not measured results):

- No probes while idle.
- Capture CPU below 1 % average on a named reference machine (report the normalization).
- Working set below 200 MiB.
- Marker acknowledgement within 150 ms.
- Stop/cancel completes within 5 s unless a documented OS call can't be interrupted.
- Measure packet and byte counts during a game or call. Never claim "zero impact".
- A missed budget blocks advertising it until it is profiled and fixed.

## 17. Build and delivery

**Work packages.** Each change follows: failing test → implement → pass → review → commit.

1. Scaffold the projects; pin the SDK and packages; Windows shell.
2. Core models, contracts and IClock; timing and status invariants.
3. Storage: ConnectionFactory, MigrationRunner, schema.sql, writer and reader; port the validation.
4. Scheduler and channel with fake probes: budgets, cancellation, overlap, gaps, backpressure.
5. Route and interface adapters, plus the ICMP probe (standard user).
6. TCP, DNS (DnsQueryEx) and HTTPS probes; manifest validation.
7. Optional WLAN and power events; consent-denied and suspend paths.
8. Metrics, rules (including latency), comparison and the next-action planner, against fixtures.
9. UI journey, marker/tray/hotkey, exports.
10. History, retention, accessibility, restart recovery.
11. Lab matrix and calibration; endpoint rights; packaging and signing; clean installs; Store submission.

**Commands:**

```powershell
# Any OS (CI)
python tools/validate_schema.py
dotnet test tests/ConnectionClue.Core.Tests -c Release
dotnet test tests/ConnectionClue.Capture.Tests -c Release
dotnet test tests/ConnectionClue.Analysis.Tests -c Release
dotnet test tests/ConnectionClue.Storage.Tests -c Release
dotnet test tests/ConnectionClue.Reporting.Tests -c Release
# Windows
dotnet build ConnectionClue.slnx -c Release
dotnet test tests/ConnectionClue.Windows.IntegrationTests -c Release
msbuild packaging\ConnectionClue.Package.wapproj /restore /p:Configuration=Release /p:Platform=x64 `
  /p:AppxBundle=Always /p:AppxBundlePlatforms="x64|arm64" /p:UapAppxPackageBuildMode=StoreUpload
```

- The .wapproj needs Visual Studio (or Build Tools) MSBuild with the MSIX tooling. `dotnet build` can't build it, so it stays out of ConnectionClue.slnx.
- `dotnet publish` produces unpackaged binaries, not a signed MSIX.
- Record the verified packaging and signing commands in docs/release-checklist.md.
- Only validate_schema.py has been run; no application code exists yet.

## 18. Release blockers and definition of done

**Blockers:**

- Endpoint rights and rates secured, CDN endpoints live under product-owned names, and a cost budget approved.
- Thresholds calibrated in the lab, with a false-positive review (especially R08/R09).
- Verified on real Windows: QPC across sleep; route, proxy and VPN attribution; Wi-Fi consent with package identity.
- Name and Store listing clearance, the supported build matrix, and signing.

**Done:** a fresh standard-user install completes **symptom → capture → marker → understandable finding → user change → comparison → redacted report**. The blocked-ICMP, silent-gateway, outage-at-start, sleep, denied-consent, proxy and inconclusive cases all stay truthful.

It needs no account, elevation, subscription or user-data backend, makes no hidden setting changes, and sends no unapproved endpoint traffic. A passing test suite doesn't prove user value: before a broad launch, watch nontechnical users complete the journey, and compare against an existing free alternative.

## 19. Artifacts

- **This handoff.**
- **schema.sql:** migration 001.
- **tools/validate_schema.py:** 34 checks pass on SQLite 3.50.4: runner-owned migration transaction with WAL set outside it, STRICT typing, CHECKs, composite FKs, FK index coverage, append-only triggers, cascading retention delete, checkpoint + VACUUM.
- **Not implemented or verified:** probes, UI, packaging, network behaviour, diagnosis accuracy, sales. The code blocks are design contracts.

## 20. Key decisions (vs. prior draft)

| Decision | Reason |
|---|---|
| Latency rules R08/R09 in v1 | The headline symptoms are latency. Rules are self-baselined, with no global threshold |
| R04 covers outage-at-start; ICMP capability needs evidence | The old 15 s capability check reported real outages as "ICMP blocked" |
| DNS cache bypass (DnsQueryEx) | Cached lookups every 15 s measured nothing |
| Fresh HTTPS connection per check | Comparable timings; no stale-pool timeouts or hidden retries |
| OS-reported RTT | User-mode timing is inflated when a game loads the CPU |
| Fast/slow pools; TCP escalates to 1 s | A global cap of 6 starved slow probes during outages, and 15 s TCP couldn't feed the rules |
| Monotonic offsets canonical, UTC anchors | Removes the two-clock ambiguity; wall-clock changes become harmless |
| Heartbeat-detected gaps | Power notifications are unreliable (Modern Standby) |
| Attribution per observation; request vs observed family | Per-segment attribution and the "Mixed" family were ill-defined |
| Terminal states + EndReason | Stop vs Cancel was ambiguous |
| AnalysisRun per rules version | "Recompute" conflicted with reproducing historical reports |
| Evidence writer/reader; typed details | The append-only observation store couldn't carry events or serve reads |
| Product-owned probe names, CDN HTTPS | Endpoint control and a kill switch at bounded cost for a $0.99 product |
| ACM-only Wi-Fi baseline; no event-log import | MSM needs consent; the event log duplicated live evidence |
| STRICT, composite FKs, FK indexes, DDL-only migration | Integrity enforced by the schema. The old file (BEGIN/COMMIT + WAL pragma) couldn't run inside a runner transaction |
| Retention per investigation | Deleting sessions orphaned comparisons |
| 24H2 floor, x64+arm64, .slnx without .wapproj | One consent model; ARM PCs; dotnet build works |
| Single-file atomic exports; HTML print CSS | No partial-write cleanup; no PDF dependency |
| No local encryption | Product decision; OS permissions plus honest disclosure |

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
