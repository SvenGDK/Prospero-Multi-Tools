# Prospero Multi Tools

A PS5 Backup Toolkit for PS1, PS2, PS3, PS4, PS5, PSP and PS Vita. Browse backups, inspect and modify metadata, install PS4/PS5 packages, launch PS1/PS2/PSP titles, and more.

## Contents

- [Prospero Multi Tools](#prospero-multi-tools)
  - [Contents](#contents)
  - [What you need](#what-you-need)
  - [Download](#download)
  - [Install the app](#install-the-app)
  - [Set up the emulators](#set-up-the-emulators)
  - [First run checklist](#first-run-checklist)
  - [Where installed backups live](#where-installed-backups-live)
  - [Uninstalling a backup](#uninstalling-a-backup)
  - [Backup covers and icons](#backup-covers-and-icons)
  - [What every screen does](#what-every-screen-does)
  - [Settings you might want to change](#settings-you-might-want-to-change)
  - [Known limits](#known-limits)

## What you need

- A PS5 running a firmware supported by the [`unjail](https://github.com/SvenGDK/unjail-ps5app-payload)` payload.
- The `unjail` payload sent to the console before you open the app. Without it, the app cannot widen its file view past its own sandbox: installs, emulator launches, and every scan of `/data` or a USB drive will refuse. The `unjail` payload is required for every launch.
- An USB drive is enough to get started. The app also reads and writes `/data` and `/user` internally once the escalation daemon is running.

## Download

Two files are available on the Releases page:

- `ProsperoMultiTools.zip` — the homebrew app itself as a signed folder module ready to install on the console.
- `EmulatorPack.zip` — the emulator folders and the shared `emulator-resources/`.

## Install the app

1. Unzip `ProsperoMultiTools.zip`.
2. Copy the resulting folder to an USB drive `/homebrew/PPSA99110` or the console's own storage `/data/homebrew/PPSA99110`.
3. Install the homebrew app with [`dump_installer`](https://github.com/EchoStretch/dump_installer)
4. Send the [`unjail`](https://github.com/SvenGDK/unjail-ps5app-payload) payload to the console once per boot before opening the app.

## Set up the emulators

The launch paths for PS1, PS2 and PSP backups drive the emulators shipped in `EmulatorPack.zip`.

1. Unpack `EmulatorPack.zip`.
2. Copy the emulator folders you want (`ps1hd`, `psphd`, and any PS2 emulator folders such as `BULLY`, `JAK`, `MANHUNT`, `Siren`, `RotkV1`, ...) onto the console. The primary location is:

   `/data/homebrew/emulators/<emulator-name>/`

   USB alternatives are also accepted:

   `/mnt/usbN/homebrew/emulators/<emulator-name>/`   (N = 0..7)

3. Copy the whole `emulator-resources/` folder from the same archive to:

   `/data/homebrew/emulator-resources/` on the console or to an USB drive `/homebrew/emulator-resources`

4. Open the app and check the `Diagnostics` screen. It lists every emulator folder the current install detected under `/data/homebrew/emulators/` and each USB root.

## First run checklist

- Open the app. The bottom of the Home screen tells you how many storage locations answered. If it says zero, the escalation daemon has not been sent yet or no USB drive is plugged in.
- Open `Diagnostics`. It reports whether the escalation daemon widened the file view, which storage roots are reachable, whether the file broker is answering, and which emulators the current install carries.

## Where installed backups live

`/data/homebrew/games/<TITLE_ID>/`

## Uninstalling a backup

Uninstalling a backup from the console's own home screen removes the shell registration only. The `/data/homebrew/games/<TITLE_ID>/` folder is left in place. Remove it by hand from a file manager if you want the disk space back. An automated cleanup step will be added into the app in a later version.

## Backup covers and icons

PS3, PS4, PS5, PSP and PS Vita backups carry their icon art inside their own PKG file, so the app reads it directly.

PS1 and PS2 backups do not carry that image. The first time a PS1 or PS2 backup is opened, the app fetches its cover from the network and stores it in an on-device cache. This means:

- An active internet connection is required the first time a PS1 or PS2 cover is displayed.
- Every subsequent open reads from the on-device cache; the network is not touched again for that title.

## What every screen does

- `Backup Managers` — one entry per platform (PS1, PS2, PS3, PS4, PS5, PSP, PS Vita). Each opens a scan of every reachable storage root; the result is a split view with a cover preview on the left and metadata on the right. Selecting a backup opens the detail screen.
- `Backup detail` — title, game id, content id, region, category, version, firmware, size, source file or folder, plus the SFO or `param.json` rows. Action buttons: `Launch from folder`, `Install package`, `Launch title`, `Configure and launch` (for the emulator-based platforms), `Copy backup`, `Move backup`, `Delete backup`, `Edit SFO`, `Edit param.json`.
- `Emulator launch` — per-title options plus a live launch driver. PS1, PS2 and PSP each have their own option set (render scale, aspect, filters, multitap, disc count, VMC path, and so on).
- `PS1 / PS2 Tools` — merge multi-BIN CUE sets into a single BIN, convert PS2 BIN/CUE images to ISO.
- `PS5 Tools` — send an ELF payload over TCP, clear the console's error history, read the inserted disc's `param.json`, and probe the well-known homebrew TCP ports (payload, WebSrv, FTP, klog, kernel exploit, PKG installer, and so on).
- `Console Utilities` — per-platform SFO / backup folder / PKG inspection for PS3, PS4, PSP and PS Vita.
- `Shared Tools -> Shared Utilities` — browse any folder the file view exposes, and view a file's first bytes as a hex dump.
- `System -> Settings` — display, behaviour, audio, payload-sender defaults, and a reset button.
- `System -> Diagnostics` — the reachability report described above.
- `System -> Refresh storage` — re-probes every root without leaving the app.
- `System -> Unlock full storage access` — sends the escalation request again if it did not apply at boot.
- `Exit` — close the app.

## Settings you might want to change

- `Paths -> Start path` sets the folder the file pickers open at.
- `Paths -> Backup search path` overrides the default scan set (`/data` plus every USB) with a semicolon-separated list of folders.
- `Display -> Text scale` and `Display -> List rows` adjust the reading density.
- `Behavior -> Confirm before delete / move` toggles the confirmation dialog for destructive actions.
- `Behavior -> Show hidden files` toggles dotfile visibility in the picker.
- `Behavior -> System notifications` toggles the top-screen toast messages the shell posts.
- `Behavior -> Notification time` sets how long toasts stay on screen.
- `Audio -> Play soundtrack when available` toggles the per-title soundtrack playback in `Backup detail`.
- `Audio -> Volume` sets the soundtrack playback volume.
- `Payload sender -> Host` and `Port` are the defaults `PS5 Tools -> Payload sender` picks up.
- `Reset -> Reset all settings to defaults` restores every option above to its shipped default.

## Known limits

- `/data` and `/user` are not offered by the file and folder browser yet. Every launch and install path still reaches them correctly through the file broker.
- Uninstalling a backup from the console's home screen leaves `/data/homebrew/games/<TITLE_ID>/` behind. Remove the folder by hand for now.
- The `unjail` payload must be sent again every time the console reboots.
- PS1 and PS2 cover art needs an active internet connection the first time each title is displayed.
