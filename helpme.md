# ConnectionClue help

For ConnectionClue 1.0.7.

ConnectionClue checks this PC's path to the internet, explains what it observed in plain sentences with local times, and suggests next steps. It changes a Windows setting only when you ask: the DNS switch, which Windows confirms with an administrator prompt and which you can undo.

## Run a check

- Under **What's happening?**, choose one of Gaming lag, Buffering video, Choppy calls or Disconnections. It's a radio group: exactly one choice is selected, Tab moves into the group and the arrow keys change the choice. Then select **Quick check**. You can also press F5, use **Quick check** in the taskbar button's right-click menu, or use the notification-area icon's menu.
- After the check, **Quick check** also measures how long a connection to real services for your choice takes: Xbox network, Steam and Epic Games for gaming lag; Microsoft Teams, Zoom and Google Meet for calls; Netflix, YouTube and Twitch for video; the Windows connectivity check, Cloudflare and Google for disconnections. It is a TCP connection only: no data is sent, and it can't measure a game's UDP traffic, call quality or a video's bitrate.
- Each choice also tests one extra service, which you can change in **Settings > Network > Optional symptom-specific targets** or in Insights: Riot Games (auth.riotgames.com:443) for gaming lag, Prime Video (www.primevideo.com:443) for video, Discord (discord.com:443) for calls and Microsoft (www.microsoft.com:443) for disconnections. Replace it with your own server, such as your game's, or clear the box to test only the built-in services.
- **Quick check length** in Settings sets the baseline delay-measurement phase (10–60 seconds, 30 by default). The optional speed test adds about 16 seconds. Service tests and result processing can take additional time, so the setting is not a deadline for the whole operation. Settings shows the estimated measurement time next to the length. A longer length saved by an earlier version is shortened to 60 seconds; use **Capture longer** for longer recordings.
- The speed test uses up to 300 MB, is skipped on metered connections and never runs in the background.
- Select **It lagged just now** right after a lag (Ctrl+L during a check). Findings at your lag marks are listed first.
- **Stop** ends a check early; a short check may not have enough evidence.
- Below **Quick check**, a line marked **or** separates the other way to check: **Capture longer** records for 15, 30 or 45 minutes, or 1, 2, 4 or 8 hours (15 minutes by default; pick the length next to the button) while the app stays open. Both are hidden while a check runs.
- After a quick check or longer capture, use **Export (PDF)** or **Export (MHTML)** to save the results. See **Export and share results** below for the differences.
- With no network at all, ConnectionClue shows a warning and starts no network tests.
- Only one copy runs. Starting it again brings the running window back.

## Notifications, disconnections and the automatic recheck

New in 1.0.7: the one-time check after reconnection reports its result, background checks notify every problem they find, background checks have their own length, and intervals range from 3 minutes to 8 hours. Connection-loss notifications and the reconnect check itself arrived in 1.0.6.

- If Windows reports that this PC has lost its network connection, a clear warning appears above every page, including Settings. A desktop notification is sent once per disconnection, even while the window is open. New checks do not start while disconnected.
- When the network reconnects, ConnectionClue waits about two seconds for Windows to settle, then runs **one 10-second connectivity check**, whatever your quick or background check lengths are. Repeated Windows notifications do not start duplicate checks, and opening the app while already connected does not trigger this recheck.
- This one-off check runs without download/upload speed tests, symptom-specific service tests or online advice review. It does not change your saved settings. Ten seconds is the measurement window; setup, outstanding probes and result processing can take additional time.
- When it finishes, a desktop notification gives the result: **Back online: no problems found**, **Back online, but the connection has a problem**, **Back online, but performance is below your limits**, or **Back online: not enough data to judge**. The notice above every page shows the check time and result (with a warning icon for a problem) until you select **Dismiss** or the next check starts. "No problems found" means nothing exceeded your limits during those seconds; it is not a guarantee that the connection is always fine.
- If a check, diagnostic or export is busy, the automatic recheck waits. A new disconnection cancels a queued or running automatic recheck, and reconnecting arms one new attempt. A manual capture is not interrupted, so it can continue recording the drop. Exiting the app cancels pending rechecks.
- This one-time reconnect check is separate from regular background checks and also applies on mobile/metered networks. It does not enable recurring mobile checks or speed tests; those settings remain unchanged.
- If the automatic attempt cannot finish, the app shows an error and lets you try a quick check manually instead of retrying repeatedly.
- Every background check that finds a connection problem, or performance below your limits, sends a notification. When the same problem is still there, the title says it continues and the text says when it was first seen. The first background check without problems afterwards sends one **Back to normal** notification. Checks that can't conclude never notify.
- Notifications from automatic checks and disconnections appear even while the window is open. The result of a check you started notifies only while the window is in the background, for example when a longer capture ends while you use another app; the same problem is repeated at most hourly.
- After the first notification, the notification-area icon stays for the rest of the session so that Windows keeps the notification in its notification center. Windows Focus or Do not disturb settings may hold banners back.

## Why some charts continue past the check length

- With a 10-second baseline and speed testing enabled, the measurement phases are roughly 10 seconds of baseline, 8 seconds of downloading, then 8 seconds of uploading. Probe completion and other overhead can extend this slightly.
- **Home network** and **Internet** keep measuring during the speed tests to show delay under load and calculate bufferbloat. **Web services** runs only during the baseline phase, so its chart stops earlier. Empty space after its last sample is not a failed check.
- All three charts share the same timeline. A chart spanning about 26–30 seconds does not mean the 10-second setting was ignored. Turn off **Measure download and upload speed** to omit those extra load-test phases; service tests and final processing may still finish afterward.

## Export and share results

- **Export (PDF)** and **Export (MHTML)** appear together after **Recommendations** following a quick check or longer capture. They export results, findings, measurements, path steps, services, recommendations, check details and images of the Check page and its delay chart with captions.
- **Export (PDF)** creates a multi-page document through Microsoft Print to PDF; Windows asks where to save it. If that printer is missing, turn it on in Windows Features.
- **Export (MHTML)** saves one `.mhtml` web archive with text, styles, tables and PNG images embedded. Open it offline in Microsoft Edge or another MHTML-compatible browser; no printer, internet connection or separate image folder is required.
- Starting with 1.0.5, MHTML report text uses regular-width Segoe UI at an 18-pixel default size, with increased line and table spacing, larger headings and shorter text lines. Browser zoom can enlarge it further. Export a new file to get these improvements; existing files and text inside captured screenshots are unchanged.
- Long captures list up to 1,000 samples per step, preferring unanswered checks within each sampled interval. The export states how many samples were omitted; it is not a raw, complete capture.
- Export status appears below the buttons. While an export is in progress, another export or check cannot replace its results. A later background check replaces the displayed results and hides these buttons, so export a manual check before it is replaced.
- Review the file before sharing: it can include service addresses and other details shown by the app. The separate **Report for your provider** in Insights offers a more limited report.

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
- **No problem observed** — the measured results stayed within your limits during this check. It does not prove the network is always fine; a lag you marked may come from the game, app or its servers.
- **Can't conclude** — says what's missing and the next step, for example too few measurements, or a VPN that hides the path.
- A link drop, failed name lookups or a failed secure check count as a connection problem even when delay stayed within your limits. Internet checks use one test service for now, so "only one service affected" can't be told apart yet.

## Background checks and mobile networks

- Background checks are on by default on Wi-Fi and Ethernet. They run at the interval you choose under **Check every** (3, 5, 10, 15, 20, 30 or 45 minutes, or 1 hour, 90 minutes, or 2, 3, 4, 6 or 8 hours; 5 minutes by default) while ConnectionClue is open, including from the notification area.
- Each background check measures delay for the **Background check length** (10–60 seconds, 10 by default). It is separate from **Quick check length**, and background checks never run a speed test or service tests, so it is their whole measurement time. A short check uses little data; a longer one gives more evidence per check.
- If your settings were saved by an earlier version with the old 15-minute default, the interval moves to the new 5-minute default once. An interval you chose yourself is kept.
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
- Home keeps the result, measurements and check controls. Open **Recommendations** for the full **Worth checking** and **What to try** text, detailed advice and the **Checked, no change needed** list. Bufferbloat and service-test summaries for that same check also appear there; long text wraps and the page scrolls instead of truncating it.
- Apply Windows, router or driver changes yourself. Read any impact or restore notes before changing a setting.
- Online review of recommendation text is on by default. Its requests contain generic advice only, not check results, device names, local addresses or measurements; public services still see normal HTTPS metadata, including your public IP. Turn it off in Settings; it is skipped offline.
- Findings cover only the services and measurements ConnectionClue tested. A good result doesn't guarantee every game, call or website is healthy.

## Settings, shortcuts and accessibility

- Settings uses three compact tabs rather than one long page: **Checks** for quick check length and speed test, and background scheduling and length; **Network** for alert limits, plan speeds and service targets; **Preferences** for appearance, language, online advice review and history. Normal window sizes fit without scrolling; scrolling remains available when enlarged text needs more room.
- Themes: Dark (default), Light, System, High Contrast Dark and High Contrast Light. Windows High Contrast always takes priority.
- Colours keep one meaning everywhere. In the default Dark theme, headings are gold, labels that name a setting or field are lavender, and links are cyan and underlined. A filled blue button starts a check or test, an outlined teal button runs a tool, an amber button with a shield changes a Windows setting after an administrator prompt, and a pink button marks a lag. An unavailable button is grey with a dashed outline; hover over it or focus it to see why. Every button also has an icon and a label, so colour is never the only cue.
- Keyboard: F5 runs a quick check, Ctrl+L marks a lag, Tab and Shift+Tab move between controls, and Space or Enter activates them. Every control has a screen-reader name and help text, results are announced as they arrive, and status is shown with icons and words, never colour alone. Hover or focus a control for a tooltip.
- Buttons, navigation choices and switches explain what they do on mouse hover and keyboard focus. The same information is available to screen readers; disabled action buttons retain their tooltips.
- **Start with Windows** is off by default and starts ConnectionClue in the notification area.
- **Preferences > Insights history** shows how many check summaries are saved. **Clear Insights history** asks once more before deleting them all; it can't be undone and keeps your recommendations and settings.
- There is no update-check section in Settings. Microsoft Store and winget updates can be managed outside the app.
- To use an installed update, exit any older running copy from the notification-area menu's **Exit** command, then open the updated app. Closing the window may only hide it when background checks are enabled; starting it again brings that same running copy back.
- The toolbar **Help** button opens this guide, which is in English. Text for the newest features also shows in English until its translations are reviewed.

## Privacy and limitations

- Check history stays on this PC for this Windows user: up to 30 days and 5,000 summaries, with measurements and times but no raw samples or device or network identifiers. Clear it at any time in Settings.
- PDF and MHTML result exports contain check data and screenshots of the Check page, which may include service addresses and other details shown by the app. Review either file before sharing.
- The service test, DNS comparison, route trace and speed test contact public services, which can see your public IP. Only **Quick check** (for the service test) or your own selection starts them.
- The DNS switch is the only setting ConnectionClue changes, and only after you select it and approve the Windows prompt.
- ConnectionClue is a diagnostic aid. Its results describe what it observed during a check, not a promise of future performance or proof of a provider fault.
