# Changelog

Newest first.

## 2026-10-27: the scene legend opens without a flash, US spelling

- **The scene legend opens as its own card** over the headline and the four gauges, titled "What's in the scene"
  with a × to close it, instead of pushing the panel down. Growing the window moved it up a frame after drawing it,
  so the legend's text flashed in the wrong place and the panel jumped; now the window keeps its size.
- **US spelling** in everything you read: color, gray, harbor, recognized.
- **A tidier top folder:** just the README, the license and **Install.cmd**, which installs Lighthouse or updates
  it, with one UAC prompt either way. The code, tests and scripts moved to `source/`; the changelog, security notes
  and example settings to `docs/`.
- **A one-command install,** `tools\install.ps1`: installs the .NET 10 SDK with winget if it's missing, builds,
  then runs `setup-admin.ps1` behind one UAC prompt. The README is now short, with the install first; every detail
  it had moved, unchanged, to [reference.md](reference.md).

## 2026-10-23: the mini gets out of the way of fullscreen video

- **During fullscreen video** (right-click on the mini, `miniFullscreen`): while a fullscreen window covers the
  mini's screen, the mini moves to its favorite spot on another screen, or hides when there is only one. It comes
  back to where it was when fullscreen ends. "Hide" always hides; "Stay on top" leaves it.
- A window counts as fullscreen when it covers its whole monitor, taskbar too. A browser stays maximized under its
  fullscreen video, with the title bar gone, so a maximized window counts only without a title bar. It checks four
  times a second, and keeps counting a fullscreen video while you click on another screen. The rules are
  `MiniFullscreen`, with tests.

## 2026-10-23: dark mode switch, a scene legend, more boats, cards that stay closed

- **A Dark mode switch** at the bottom of the flyout. Lighthouse follows Windows' app theme until you flip it,
  then keeps the choice (`theme`: `system`, `light` or `dark`).
- **History keeps the flyout's size**, so it reads as the same panel turning a page; its list fills the rest.
- **A legend for the scene**, opened with its **?** (`sceneLegend`): each part (beam, sea, boats, ship, moon,
  gulls, foam, weather, light) and what it shows right now.
- **More boats.** As well as watched jobs, any program working hard (about a core of CPU, 10% of the GPU, or
  5 Mbps of network) sails, for at least 30 seconds once it calms down, up to 6. Each sail is the program's dot
  color in Using it now (`SceneBoats`, with tests). When tags run out of room the busiest keep theirs.
- **An Internet group**: web browsers (Chrome, Edge, Firefox, Brave, Opera, Vivaldi, Arc) get their own plum dot, apart from the AI teal.
- **A closed card stays closed until Windows restarts.** Closing was remembered only while Lighthouse ran, and only
  until the card went away, so an update or a check that came back brought the same card back. The closed cards
  are saved with the time Windows started (`ClosedCards`, with tests).

## 2026-10-23: tidier README pictures

- The animated scene is an animated PNG (`Apng`, with tests) instead of a GIF: full color, so the dusk sky has no
  bands, and see-through rounded corners that suit GitHub's light and dark pages. It is smaller too (1.3 MB, was
  2 MB).
- The two flyout pictures carry the same cards, so they are the same height, and sit top-aligned side by side.
- The four-scene picture has a see-through background and captions in a gray that reads on both pages.
- Light pictures by default, and a **Dark mode** section that opens to the same two in dark. A README can't offer
  a real switch, and following the reader's GitHub theme gave no way to see the other one.

## 2026-10-23: History moves into the flyout

- **History is a page of the flyout** (a drill-through), opened by the History button, the tray menu's History,
  or a click on a gauge (on that gauge's last hour). The back arrow or Escape goes back. The separate History
  window is gone, and so is the gauge's small last-hour panel, which the page replaces.
- **One chart** with chips for what it shows (CPU, GPU, RAM, VRAM, temperatures) and how far back (1 hour, 24
  hours, 7 days), instead of three stacked charts.
  - The line breaks where nothing was recorded. The old charts drew a straight line across the hours the PC
    was off.
  - Round time ticks ("06:00", "Tue") instead of only a first and last time.
  - Each moment's peak is kept, so the chart reaches the "max" written under it.
  - Temperatures start near their lowest reading instead of 0.
- **One list** of Events, Games, Jobs or Speed tests, newest first, one line each; click a row for its detail.
  Events no longer repeat the speed tests, games and job runs that have their own lists. An empty list says
  what will appear there.
- `--history-image <history.db> <file>` draws the page from a copy of a history file.

## 2026-10-23: the tray icon leaves the overflow from Program Files too; README refreshed

- Lighthouse moves its tray icon out of Windows' hidden-icons overflow once per install. Windows records a
  Program Files exe as "{folder id}\Lighthouse\Lighthouse.exe", which never matched, so an icon installed there
  stayed hidden. The folder id is now expanded before comparing (`KnownFolders`, with tests), and a give-up is
  logged.
- The README covers the tray menu, Windows only, the GPU column, the LLM servers, every alert and its default,
  `update.ps1 -NoBuild`, `--reload`, `--import` and `--exit`, and the mini's measured cost (about 0.2% of a
  16-core CPU).

## 2026-10-23: Explain works with Ollama and LM Studio, and is easier to use

- **Ollama and LM Studio** count as the local LLM, as well as llama.cpp's llama-server: their row, the moon and
  Explain. Ollama's server, model runners and tray app are found by process (its `ollama pull` and LM Studio's
  `lms` tools are not), at `OLLAMA_HOST` or Ollama's default port. LM Studio is asked on port 1234
  (`lmStudioPort` to change it).
- Each is asked through its own API, with the model it already has loaded, so Explain never loads a second
  one. A model that refuses Ollama's "no thinking" switch is asked again without it.
- **For a game:** a llama-server is stopped as before, Ollama's models are unloaded (no restart needed), and LM
  Studio is never stopped: the card asks you to unload its model.
- **"What's going on?"** replaces the Explain button, and it is hidden when there is no local LLM instead of
  grayed out.
- **Explain links** on every card and, under the mouse, on each "Using it now" row ask about that one thing.
- **Follow-ups:** a box under the answer continues the conversation. The readings go with the first question;
  a long conversation sends the first message and the last 8.
- Checked against a stand-in server that answers as Ollama's and LM Studio's documented APIs do; not yet against
  a real Ollama or LM Studio.

## 2026-10-22: the mini's favorite spot

- **A favorite spot** (right-click > Favorite spot, `miniFavorite`): a corner or edge middle of the main screen, or
  "Where it is now". Bottom right by default. The mini opens there and goes back there.
- **Back as soon as it's clear.** With Move away from text on, it returns to its favorite spot once that spot has
  been clear for two checks in a row (still at most one move every 10 seconds). This replaces the growing wait
  from earlier today, which never shipped.
- **A drag only moves it for now.** It stays where you drop it for at least 10 seconds, then goes back to its
  favorite spot once that is clear. A drop near a screen edge snaps 16 px in, where the corner and edge spots sit.
- Switching layout keeps a mini that was on its favorite spot there, at the new size.

## 2026-10-22: the mini stops going back and forth, and keeps its see-through level

- **It waits longer before going back** when text keeps chasing it off its spot. The wait starts at 10 seconds and
  doubles each time it is chased off again within a minute of going back, up to 5 minutes. A stay of more than a
  minute resets it. Before, a mini over a chat window left and came back 6 times in 90 seconds.
- **It skips spots that mostly cover where it is or where you put it.** A mini snapped 8 px from a corner used to
  "move" to that corner's spot, 16 px in, which got it off nothing.
- The log says how long it will wait: "mini moved to the right: text behind it; back once its spot is clear for
  20s".
- **The see-through level holds under the mouse.** The panel no longer turns solid when you point at it.

## 2026-10-22: README pictures, tests on GitHub, two wording fixes

- **The README has pictures**: an animated scene, the flyout in both views (light or dark, following the
  reader's GitHub theme), four scenes and the mini monitor's layouts. `Lighthouse.exe --readme-images <dir>`
  redraws them from made-up demo data, so nothing from your PC, settings or history ends up in them. As in the app,
  the rows add up to the CPU, memory, disk and network totals, and "Everything else" shows "—" for GPU (GPU load is
  the busiest engine, which rows can't add up to).
- **Tests run on GitHub** on every push and pull request (`.github/workflows/ci.yml`, Windows, .NET 10).
- A job using one core said "1 cores"; it says "1 core".
- A usual time no longer shows seconds: "usually 44 min", not "usually 44m 00s" (also in the "Slower than usual"
  toast).
- The animated GIF is written by WPF and then given its loop block and frame times (`GifLoop`, with tests), which
  WPF's encoder leaves out.
- SECURITY.md says what the mini monitor reads off the screen (scored in memory, never kept) and that Claude Code
  rows read a session's working folder.

## 2026-10-22: weather in the scene, gauge history, mini layouts, the mini moves off text

- **The scene shows trouble.**
  - A storm with rain while a warning is up, and one lightning flash when a critical alert starts.
  - Fog from 85% memory commit, thick at 100%.
  - Heat shimmer by the lantern when the CPU is hot.
  - Gulls fly in with downloads and out with uploads, paced like the speed test's lights and resting while one
    runs. Foam on the wave crests follows disk activity.
- **Click a gauge for its last hour.** A chart opens under the gauges: minute averages, then the last 10 minutes
  second by second, with min, average and max (and the hottest temperature for CPU and GPU). Click again or × to
  close.
- **Mini monitor layouts**: lighthouse and gauges, a strip, the lighthouse only, or the gauges only (right-click >
  Layout, `miniLayout`). Dropped within 16 px of a screen edge, it snaps to it.
- **The mini moves off text** (`miniAvoidText`, on by default; right-click > Move away from text).
  - Once a second it scores what is behind it by its sharp light-to-dark edges, which text is full of and
    wallpapers aren't.
  - Text under it twice in a row, or the typing cursor within 24 px, slides it (200 ms) to the calmest of the
    screen's 4 corners and 4 edge midpoints, but only if that spot is calm and clearly calmer.
  - It goes back to where you put it once that spot has been clear for 10 seconds, moves at most once every 10
    seconds, and stays put while the mouse is over it or it is being dragged. Each move is logged.
- **Clicks can go through the mini** (`miniClickThrough`, tray menu > Clicks go through the mini monitor).
- `--text-check` also checks and saves each mini layout. Its clip check skips collapsed panels, which aren't drawn.
- The handle-leak test settles finalizers before counting, and runs apart from tests that draw bitmaps.

## 2026-10-22: job time left, Claude Code by project, speed-test history, your week, cooling

- **Watched jobs know their usual time.**
  - Finished runs are kept in a `job_runs` table.
  - After a command (`load`, `backfill`) has run twice, its card adds "usually 2h 10m · about 40 min left". That
    is the median of the last 5 runs that finished.
  - Past 1.5 times its usual time the card says "running longer than usual", and the end toast adds "Slower than
    usual".
  - History's new Jobs list shows each run with its duration and result.
- **Claude Code sessions are named after their project**: "Claude Code · lighthouse", with a count for two
  sessions in one folder. The folder is read from the session process's own memory, which works for your own
  processes without admin. A worktree uses its repository's name.
- **Speed tests are kept** in a `speed_tests` table, and History has a Speed tests list with a 90-day chart.
  - `internetPlan` adds "6% of your plan down (500 Mbps), 32% up (50 Mbps)".
  - `dailySpeedTest` runs one a day after its time, only when you've been away 10 minutes and no game runs; off
    by default.
  - Two tests never overlap.
- **Your week.** From Monday 06:00, a closable card and a History entry with the week's:
  - gaming time;
  - CPU and GPU peaks;
  - job runs and failures;
  - internet speed;
  - alerts.

  Fixed rules, built from the history.
- **Cooling health check.** It compares the median CPU temperature at light, medium and heavy load over the last
  7 days with days 30 to 60 ago, and fails at 5 °C or more. Each band needs an hour of data in both windows, and
  different loads are never compared.

## 2026-10-22: disk and network per program

- "Using it now" has a **Load | Disk & network** switch, remembered in settings (`usingView`). The Disk & network
  view lists the busiest programs by disk MB/s and download and upload Mbps. "Everything else" takes what is left of
  the totals:
  - disk: every program's reads and writes, including programs that have since exited;
  - network: what the network adapters moved, which also covers protocol overhead no program is named for.
- The figures come from the sensor helper, which runs a private kernel trace (`Lighthouse-IO`) of disk reads and
  writes, TCP and UDP sends and receives, and thread starts, keeping only sizes and process ids. A disk read or
  write is attributed to the process whose thread issued it.
- Without the helper's trace, disk falls back to Windows' own read and write counts per process (the I/O columns in
  Task Manager, read from `SYSTEM_PROCESS_INFORMATION`, checked against WMI). The view then says that network per
  program needs the helper.
- The speed test's busy note names the program behind most of the traffic when it can ("…downloading 34 Mbps when
  the test started (mostly Local LLM tools)").
- Task folders are passed the way Task Scheduler takes them (`\Lighthouse`, not `\Lighthouse\`). The trailing
  backslash broke starting the helper from the app, "Start with Windows" and `--set-startup`.

## 2026-10-22: the app runs without admin; a small sensor helper does the rest

- Lighthouse is two processes now. The app (tray icon, flyout, history, settings, Explain, speed test) runs with
  your own rights. Only the sensor helper, `Lighthouse.exe --sensors`, runs as admin, because the PawnIO driver
  behind CPU, RAM and SSD sensors needs it. Its own logon task starts it.
- The helper has no window and writes no files (its log lines go to the app). It answers on a named pipe that only
  your account can open, never over the network, and it does five things:
  - sensor readings;
  - the image path of a process the app can't open itself (another account's, a service);
  - the llama-server's command line and account, and no other process's;
  - stopping a running llama-server.exe and nothing else;
  - keeping the saved startup list off.
- The app checks that the process serving the pipe is this same `Lighthouse.exe` before using its answers, and
  starts the helper's task when it isn't running (your account may run it, not change it). Until the helper
  answers, the app reads what it can itself: the GPU's sensors. The helper exits 30 s after the app goes away.
- With nothing elevated left in it, the app's writes to its data folder, its signals and its menus no longer
  carry admin rights at all. That was the limit SECURITY.md listed.
- Processes the app couldn't name before the helper answered are named again once it does.
- "Start with Windows" in the tray menu asks for admin through a UAC prompt and switches both tasks
  (`--set-startup on|off`).

## 2026-10-22: security review

Lighthouse runs as admin (the sensor driver needs it), so nothing an ordinary program can change may steer it.
See [SECURITY.md](SECURITY.md).
- **Installed in `%ProgramFiles%\Lighthouse`, not a per-user folder.** Any program could have replaced the exe that
  the elevated logon task runs, then started the task. Updates now need one UAC prompt (`tools\update.ps1`).
- **No DLLs unpacked into `%TEMP%`.** The single-file build used to extract its native DLLs there and load them as
  admin, and other accounts with write access to that folder could swap one. They now ship next to the exe.
- **"Log file" and "Settings file" open Notepad from System32 with your own rights.** They used to start whatever
  "notepad.exe" resolved to, as admin.
- **The startup guard keeps off only a list saved in `HKLM\SOFTWARE\Lighthouse`** by `tools\autostart.ps1 -Disable`,
  which asks first. settings.json's `autostartOff` is only the request. Windows' own entries (tasks under
  `\Microsoft\`, Windows Security, Windows notifications) are always refused.
- **Explain only talks to a server on this PC**, and reads its key file with your own rights. A process named
  llama-server.exe could otherwise have named any file and any host.
- **The data folder is refused if it is a link, and held open so it can't become one.** Settings and images are
  written to fresh files, the log is never written through a link, and history stays in memory if its files are
  links.
- **The local LLM's restart command quotes everything as literal PowerShell strings,** so pasting it runs nothing
  hidden in a command line.
- **Secrets in job command lines** (`--token`, `--password=`, `API_KEY=`) show as `•••`.
- The startup log line says whether the user's own rights are available.
- `setup-admin.ps1` registers the logon task before it removes old copies, and a removal that fails no longer stops
  it. Elevated PowerShell can't resolve a short `%TEMP%` path such as `C:\Users\ABCDEF~1`, so the long one is used.
- `--snapshot` lists the startup guard's saved list, read the way the running copy reads it.

## 2026-10-22: first public version

Lighthouse is a CPU and GPU monitor for the Windows taskbar. This first version has:
- **The taskbar icon:** two live bars, CPU and GPU, colored by temperature, plus a lamp that blinks after a
  warning.
- **The flyout:**
  - an animated harbor scene;
  - a one-line headline;
  - four gauges (CPU, GPU, RAM, VRAM);
  - "Using it now", whose rows add up exactly to the gauges' totals, with a legend for its four groups (AI,
    jobs, games, apps and Windows);
  - cards that each close with a ×.
- **A mini monitor:** an always-on-top version with a see-through panel and text that stays readable on any
  background.
- **Watched jobs:** your own scripts and servers get a row per run, a boat in the scene and a toast with their
  exit code's meaning.
- **Games:** quiet alerts while one runs, a session summary, and on dual-CCD X3D Ryzens, whether the V-Cache CCD
  got the game.
- **A local llama-server:** Explain (a short read of the last five minutes), and stopping it for a game with its
  start command on the clipboard.
- **A speed test:** ping, download and upload, played out in the scene, naming the connection it used and
  flagging other traffic on the line.
- **Alerts** on sustained conditions only, **health checks** daily, and a guard for startup entries you list.
- **History:** minute averages for 90 days, plus events and game sessions.
- **Settings** in one JSON file, `--import` for loading a file of them, and diagnostics for checking everything
  without opening a window.
- Released under the MIT license. The README lists the bundled libraries' own licenses.
