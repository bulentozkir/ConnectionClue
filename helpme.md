# ConnectionClue help

ConnectionClue checks this PC's path to the internet, explains what it observed in plain sentences with local times, and suggests next steps. It changes a Windows setting only when you ask: the DNS switch, which Windows confirms with an administrator prompt and which you can undo.

## Run a check

- Choose what's happening (Gaming lag, Buffering video, Choppy calls or Disconnections), then select **Quick check**. You can also press F5, use **Quick check** in the taskbar button's right-click menu, or use the notification-area icon's menu.
- After the check, Quick Check measures how long a connection to real services for your choice takes: Xbox network, Steam and Epic Games for gaming lag; Microsoft Teams, Zoom and Google Meet for calls; Netflix, YouTube and Twitch for video; the Windows connectivity check, Cloudflare and Google for disconnections. It is a TCP connection only: no data is sent, and it can't measure a game's UDP traffic, call quality or a video's bitrate. Add your own server (host:port) in Insights or Settings.
- Download/upload speed measurement is on by default for checks you start and can be turned off in Settings. It uses up to 300 MB, is skipped on metered connections and never runs in the background.
- Select **It lagged just now** right after a lag (Ctrl+L during a check). Findings at your lag marks are listed first.
- **Stop** ends a check early; a short check may not have enough evidence. **Capture longer** records for 1, 2, 4 or 8 hours while the app stays open.
- With no network at all, ConnectionClue shows a warning and starts no network tests.
- Only one copy runs. Starting it again brings the running window back.

## What the findings mean

Each check ends with findings based on the evidence it collected. Times are this PC's local time.

- **Link dropped** — for example "Wi-Fi link dropped at 21:04:12 (down for 8 s)". Windows reported that the Wi-Fi or cable link went down.
- **Home network stopped passing traffic** — your router and the internet stopped answering together while the link stayed up.
- **Internet stopped answering beyond your router** — your router kept answering, so the break is at your internet provider or farther.
- **Name lookup (DNS) failed while direct connections worked** — your DNS server is the likely cause. Compare DNS servers in Insights.
- **Secure web check failed** — the category is named: a rejected secure connection (certificate problem or filtering software), a proxy asking for sign-in, or a redirect such as a network sign-in page.
- **Delay starts between this PC and your router** — router replies slowed along with internet delay. On Wi-Fi this usually means distance, walls or interference.
- **Delay starts beyond your router** — internet delay rose while your router stayed fast: your provider or farther. If someone at home is uploading or streaming, check the bufferbloat grade first.
- **Your router doesn't answer pings** — ConnectionClue can't tell whether a problem is inside your home network or beyond it.
- **No problem observed** — everything answered on time during this check. It does not prove the network is always fine; a lag you marked may come from the game, app or its servers.
- **Can't conclude** — says what's missing and the next step, for example too few measurements, or a VPN that hides the path.
- A link drop, failed name lookups or a failed secure check count as a connection problem even when delay stayed within your limits. Internet checks use one test service for now, so "only one service affected" can't be told apart yet.

## Background checks and mobile networks

- Background checks are on by default on Wi-Fi and Ethernet. They run at the interval you choose (15 minutes by default) while ConnectionClue is open, including from the notification area, and never run a speed test.
- On mobile networks, background checks are off by default. Mobile networks include cellular connections and connections Windows marks as metered, such as phone hotspots. Turn on **Check regularly on mobile networks** in Settings to allow them. When you move back to Wi-Fi or Ethernet, scheduled checks resume automatically. Checks you start yourself always run.
- To catch problems that only happen at certain times, leave ConnectionClue running in the notification area for days. It keeps 30 days of check summaries; the time between checks is not observed.

## Insights

- **Trends:** each day shows a quality score from 0 to 100 (each check scores 100 with no issue, 50 when slower than your limits and 0 with a connection problem), the share of checks without issues, and average speeds. Enter your plan speeds in Settings to compare them with what you pay for. The worst hours list the local hours with the most problem checks.
- **Route hops:** traces the route to 1.1.1.1 and names the hop that adds lasting delay, with where it probably is: your router or Wi-Fi, your home or provider's access network, or farther. Loss is reported only when it continues to the destination; routers that skip or slow down ping replies are not blamed.
- **DNS speed comparison:** compares your current DNS server with Cloudflare, Google and Quad9. When one is clearly faster (at least 5 ms and 20%), **Use … DNS** switches the active network adapter to it after Windows asks for administrator approval. **Restore automatic DNS** undoes it. Don't switch on work or school networks, or with a VPN that needs its own DNS. **Open network settings** lets you change DNS yourself.
- **Wi-Fi channels:** shows the channel and band this PC uses, how many networks share each channel, and the least crowded channel per band (1, 6 or 11 on 2.4 GHz; the 36 or 149 block on 5 GHz). If you're on 2.4 GHz and faster bands are nearby, it suggests 5 or 6 GHz. Change the channel in your router's wireless settings, or set it to Auto. Windows requires location access for Wi-Fi scans; **Open location settings** appears when it's missing. Network names and device identifiers are not shown.
- **Bufferbloat grade:** A to F by how much delay rises under a download or upload (A up to 5 ms, B 30, C 60, D 100, E 200, F more). When it rises, the recommendations give router steps: turn on SQM, Smart Queue or Adaptive QoS and set the limit to about 90% of your measured speed.
- **Report for your provider:** **Export HTML** or **Print or save as PDF** (Microsoft Print to PDF is preselected). The report lists the findings with local times, the measurements and a sampled timeline, and leaves out IP addresses, network and adapter names, SSIDs and raw packets. Review it before sharing.

## Recommendations

- Recommendations come from the latest completed check and show the evidence, the reason for each action and how to do it.
- Apply Windows, router or driver changes yourself. Read any impact or restore notes before changing a setting.
- Online review of recommendation text is on by default. Its requests contain generic advice only, not check results, device names, local addresses or measurements; public services still see normal HTTPS metadata, including your public IP. Turn it off in Settings; it is skipped offline.
- Findings cover only the services and measurements ConnectionClue tested. A good result doesn't guarantee every game, call or website is healthy.

## Settings, shortcuts and accessibility

- Themes: Dark (default), Light, System, High Contrast Dark and High Contrast Light. Windows High Contrast always takes priority.
- Keyboard: F5 runs a Quick Check, Ctrl+L marks a lag, Tab and Shift+Tab move between controls, and Space or Enter activates them. Every control has a screen-reader name and help text, results are announced as they arrive, and status is shown with icons and words, never colour alone. Hover or focus a control for a tooltip.
- **Start with Windows** is off by default and starts ConnectionClue in the notification area.
- Updates: Microsoft Store installs update automatically. If you installed with winget, run `winget upgrade ConnectionClue.ConnectionClue` (or `winget upgrade --all`). **Check for updates** checks the official release page only when you select it.
- The toolbar **Help** button opens this guide, which is in English. Text for the newest features also shows in English until its translations are reviewed.

## Privacy and limitations

- Check history stays on this PC for this Windows user: up to 30 days and 5,000 summaries, with measurements and times but no raw samples or device or network identifiers.
- The service test, DNS comparison, route trace and speed test contact public services, which can see your public IP. Only Quick Check (for the service test) or your own selection starts them.
- The DNS switch is the only setting ConnectionClue changes, and only after you select it and approve the Windows prompt.
- ConnectionClue is a diagnostic aid. Its results describe what it observed during a check, not a promise of future performance or proof of a provider fault.
