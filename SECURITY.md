# Security

## What runs as admin, and why

Lighthouse is two processes:
- **The app** (tray icon, flyout, mini monitor, history, settings, Explain, speed test) runs with your own rights.
- **The sensor helper**, `Lighthouse.exe --sensors`, is the only part that runs as admin. The PawnIO driver that
  reads CPU, RAM and SSD sensors needs admin rights. The helper has no window, reads no settings, and writes no
  files (its log lines go to the app).

Both start at logon from their own scheduled tasks. Your account can start either task (the app starts the
helper's when it isn't running), but only an administrator can change them.

## What the helper does, and for whom

The helper answers on a named pipe, `Lighthouse.Sensors.<session>`, that only your account can open and that
refuses network clients. Any program running as you could ask it the same questions, so each answer is one such a
program may have:
- **Sensor readings**: temperatures, power, clocks, fan speed, SSD wear.
- **Disk and network bytes per program**, from a private kernel trace (`Lighthouse-IO`) of disk reads and writes,
  network sends and receives, and thread starts. Only sizes and process ids are kept: never addresses, ports or
  file names.
- **Process image paths** for processes the app can't open itself (another account's, a service), so it can name
  them. Nothing else about them.
- **The llama-server's command line and account**, for Explain and for its restart command. No other process's
  command line, since those can carry other people's secrets.
- **Stopping a running `llama-server.exe`**, for the "Stop local LLM" card during a game. It stops nothing else.
- **The startup guard**: it keeps off only the list an administrator saved in `HKLM\SOFTWARE\Lighthouse` (written
  by `tools\autostart.ps1 -Disable`, which asks first), and never Windows' own entries (tasks under `\Microsoft\`,
  Windows Security's Run values, Windows notifications).

The app checks that the process serving the pipe is this same `Lighthouse.exe` before it uses an answer, and the
helper takes the pipe name first (`FirstPipeInstance`), so another program can't stand in for it unnoticed.

## The rest

- **The program can't be swapped.** It is installed in `%ProgramFiles%\Lighthouse`, where only administrators can
  write, and its native DLLs ship next to it instead of being unpacked into `%TEMP%`. Updates need one UAC prompt.
- **Explain only talks to a server on this PC**: a llama-server, Ollama or LM Studio listening on 127.0.0.1 or
  ::1. A `llama-server.exe` that names another host, or an `OLLAMA_HOST` that points at another machine, gets no
  request. What it sends is the readings Lighthouse shows and your questions. Freeing VRAM for a game unloads
  Ollama's models through its own API; it never stops Ollama or LM Studio.
- **Nothing pasted runs more than it says.** The LLM restart command Lighthouse puts on the clipboard quotes every
  part as a literal PowerShell string.
- **Secrets stay out of labels.** A job's `--token`, `--password=` or `API_KEY=` value shows as `•••` in the
  flyout, toasts and History.
- **Started as admin by hand**, the app still protects itself: it refuses a data folder that is a link and holds
  it open, and opens other files and programs (the LLM's key file, Notepad) with your own rights.
- **What the mini monitor reads off the screen.** Once a second it reads the pixels behind it, to color its text
  and, with "Move away from text" on, to tell whether text is under it. When it needs to move it also reads the 8
  spots it could go to, and while it is away, its favorite spot. The pixels are scored in memory and dropped: never saved, logged or sent. The mini leaves
  itself out of screen captures, so it doesn't appear in screenshots or screen shares.
- **Claude Code rows** read one thing from Claude Code's own processes: their working folder, from the process's
  memory, to name the row after the project. These are your own processes, so this needs no admin.
- **Network.** The speed test sends random bytes to speed.cloudflare.com over HTTPS, and nothing about you or the
  PC. Explain talks to a server on this PC only. Nothing else goes out.

## Known limits

- **Other programs running as you can use what the helper offers**: read the sensors, learn process paths and the
  llama-server's command line, stop the llama-server, or keep the helper running by staying connected. They can
  also send the app its signals (exit, show the flyout, save a picture of it, re-read settings, start a speed
  test). None of these reaches admin rights.
- **A program running as you can stop the helper's pipe from working** by taking its name before the helper
  starts. The app then shows no CPU temperatures, which is all it gets.

A program running as you already has a lot of access. The aim is that it gets no more by going through Lighthouse.

## Reporting a problem

Please report security problems privately through the repository's **Security > Report a vulnerability** page
on GitHub, not in a public issue.
