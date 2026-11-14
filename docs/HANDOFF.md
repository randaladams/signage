# Handoff: picking this project up on another device

Snapshot as of **14 Nov 2026**. This is the "where are we and how do I carry on" document, for a person or a
new Claude Code session. Start a new session by pointing it at this file:
*"Read docs/HANDOFF.md, README.md and docs/NEW-COMPANY.md, then continue."*

> ⚠️ **This repository is public.** Nothing secret is in it (keys and passwords live in GitHub Secrets and on the
> owner's PC, see §4), but internal IP addresses and device usernames below are visible. Consider making the repo
> private; if you do, the Pi install's "download from GitHub" one-liner must be replaced with a copy from a PC (`scp`),
> see README.

---

## 1. What this is

Free, private digital signage: an **admin web page** (phone/desktop) to upload photos, videos and animated GIFs,
organise them into playlists with start/end dates, and manage screens; and **players** on Raspberry Pis and
Android/Fire TV devices that show the slideshow, work offline from saved copies and start themselves after power cuts.
Runs on Cloudflare's free tier (Workers + R2 + D1 + Access). One GitHub repo deploys to many companies, each in its
own Cloudflare account.

- **README.md**: architecture, day-to-day use, Pi and Fire Stick setup, troubleshooting
- **docs/NEW-COMPANY.md**: step-by-step setup for another company (**planned for Monday 16 Nov**)
- **docs/NETWORK-CHECKLIST.md**: one page for a company's IT team
- **server/test/README.md**: automated tests (`cd server && npm install && bash test/run.sh`)

## 2. Current state

### Live system (company: Adams Elite)

| Thing | Value |
|---|---|
| Admin page | https://signage-admin.adamselite-signage.workers.dev (Cloudflare Access: Cloudflare login, or one-time PIN for `@adamselite.com`) |
| Device API | https://signage-device.adamselite-signage.workers.dev (`/get` = Android app download) |
| Portal version | **1.7.1** (top bar; tooltip shows the git build) |
| Android app | **1.0.8** (version code = build number) |
| Pi player | **pi-1.5.0** |
| Repo | github.com/randaladams/signage (`main`; every push deploys) |
| Cloudflare Access team | `noisy-shape-1431.cloudflareaccess.com` |
| Cloudflare API token | `signage_api`, **expires 12 Nov 2027** (renew before then) |

### Screens

| Name in portal | Device | Address / user | Player | State |
|---|---|---|---|---|
| **Breakroom** | Raspberry Pi 3 (hostname still `raspberrypi`) | `display2@10.184.196.100` (Ethernet); Wi-Fi backup on RinconGuest = 10.184.204.6 (guest network, not reachable from the office LAN) | pi-1.5.0 | ✅ running, left on over the weekend; USB keyboard attached |
| **(Production / first Pi)** | Raspberry Pi 3 (hostname `raspberrypi`) | `display1@10.184.196.74` (Ethernet) | **pi-1.3.0 (old)**: has SD protection + restart/shutdown, lacks network reporting + diagnostics | ⏸ powered off. **Needs upgrade** (§6.1) |
| Samsung SM-S901U (test) | Android phone running the Fire Stick app | mobile data | 1.0.8 | test only |

No real Fire TV Stick has been tested yet (the owner's stick broke). The Android app has been exercised on a phone.

## 3. How it's built (one-minute tour)

```
server/            Cloudflare Worker, one codebase deployed twice:
  src/index.js       signage-admin (Access-protected admin page + API) and signage-device (screens' API)
  src/access.js      verifies the Cloudflare Access JWT on every admin request (stays locked even if Access is misconfigured)
  src/admin.html     the whole admin page (vanilla JS): Media / Screens / Playlists / Settings
  migrations/        D1 schema: 0001 init, 0002 video, 0003 device network
  test/              end-to-end tests (run.sh)
pi/                Raspberry Pi player
  signage_player.py  pygame off-screen + writes frames to /dev/fb0; videos handed to mpv (GPU, --vo=gpu --gpu-context=drm)
  install.sh         installs as a hardened systemd service; SD-card protection; polkit + sudoers rules
  update.sh          passwordless player update (narrow sudo rule)
  wifi-scan.sh       turn Wi-Fi on + list networks
android/           Fire Stick / Android TV player (Kotlin, no third-party libraries)
companies.json     companies to deploy to (each = a GitHub Environment)
.github/workflows/ deploy-server.yml, build-player.yml (matrix over companies.json)
```

**Design decisions worth knowing before changing things**
- **Screens only make outgoing HTTPS requests** (pull every 60 s). Nothing connects to them. Commands (refresh,
  restart, reboot, shutdown, diag) ride on the next check-in and are cleared once delivered.
- **Players announce capabilities** in `X-Capabilities` (`pause`, `video`); older players are sent compatible data
  (no videos; empty list when paused). Players also send `X-App-Version`, `X-Device-Info`, `X-Network` (JSON).
- **Media is converted in the browser**, not on the server (free tier has no transcoding): photos → JPEG ≤1920 px,
  EXIF stripped (and stripped again server-side); videos and animated GIFs → 720p H.264 MP4 via canvas + MediaRecorder,
  real-time (page must stay visible). Codec sniffing explains unplayable files (HEVC, MPEG-2…).
- **Pi 3 video**: only mpv's GPU path is smooth (720p ≈ 30 % CPU). Direct framebuffer/KMS paths were measured too slow.
  mpv needs ~2 s to start; the player lets clips finish and blanks the screen first. A lone video loops in one mpv run.
- **"Keep screens in step"**: every screen computes the same position from server-corrected time; items last the
  playlist interval (photos) or their own length (videos).
- **Pi SD-card protection**: media + playlist on a tmpfs (`/var/cache/signage-player`, 40 % RAM, re-downloaded after
  reboot), journald volatile, swap zram-only, apt timers off. Only the pairing token (`/var/lib/signage-player/token`)
  is written to the card, once, fsynced.
- **Pi diagnostics**: any key on a USB keyboard (reads /dev/input; needs `input` group; ignores HDMI-CEC). Fire Stick:
  Menu button. Portal: *Show diagnostics*. Shows a network ladder (link > IP > router > DNS > server > pairing) + health.
- **Multi-company**: one repo; each company = entry in `companies.json` + GitHub Environment with
  `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ACCOUNT_ID` (secrets), `COMPANY_ID`, `ACCESS_TEAM_DOMAIN`, `ACCESS_AUD`
  (variables). `COMPANY_ID` is a safety check (prevents falling back to repo-level secrets). Adams Elite still uses the
  repo-level secrets/variables (`"uses_repo_settings": true`).

## 4. Secrets and keys: what lives where

| Item | Where | Notes |
|---|---|---|
| Cloudflare API token + account id (Adams Elite) | GitHub → repo **Secrets** `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ACCOUNT_ID` | per-company ones go in that company's Environment |
| Access team domain + AUD (Adams Elite) | GitHub → repo **Variables** `ACCESS_TEAM_DOMAIN`, `ACCESS_AUD` | not secret |
| **Android signing key** | GitHub Secrets `ANDROID_KEYSTORE_B64`, `ANDROID_KEYSTORE_PASSWORD`; original files in **`.secrets/` on the owner's PC only** (git-ignored) | ⚠️ **Back up `.secrets/` (e.g. password manager).** If lost, app updates can't install over existing installs. GitHub can't give the secret back. |
| **SSH key for the Pis** | `~/.ssh/signage_pi` (+ `.pub`) on the owner's PC; public half is in each Pi's `~/.ssh/authorized_keys` | Needed to update Pis from a new device; see §5 |
| Pi user passwords (`display1`, `display2`) | the owner | needed for `sudo` (installer, Wi-Fi) |
| Wi-Fi passwords | typed directly on the Pi (`nmcli --ask`) | never stored in the repo |

## 5. Setting up a new device to continue

1. Install **git**, **Claude Code**, and (for running tests locally) **Node.js 22+**.
2. `git clone https://github.com/randaladams/signage` and open it in Claude Code.
3. **SSH access to the Pis**, either:
   - **copy** `~/.ssh/signage_pi` and `signage_pi.pub` from the old PC to `~/.ssh/` on the new one (private file:
     owner-only permissions), **or**
   - **make a new key** on the new device and add it to each Pi (asks for that Pi's password once):
     ```bash
     ssh-keygen -t ed25519 -f ~/.ssh/signage_pi -C signage-pi-setup
     ssh-copy-id -i ~/.ssh/signage_pi.pub display2@10.184.196.100
     ```
   Connect with `ssh -i ~/.ssh/signage_pi display2@10.184.196.100`. The Pis must be reachable on the same LAN
   (10.184.196.x).
4. **Copy `.secrets/`** into the repo folder if this device will ever need to regenerate GitHub's signing secrets
   (normally not needed: GitHub already has them).
5. Check everything still passes: `cd server && npm install && bash test/run.sh` (all 7 suites should say ALL PASSED).

Deploying needs nothing locally: **pushing to `main` deploys** the server (to every company) and builds the Android app.
Watch at github.com/randaladams/signage/actions.

## 6. Open items (in priority order)

1. **Upgrade the Production Pi** (display1) when it's powered back on: on the Pi (or via SSH)
   `sudo bash ~/signage-pi/install.sh` after copying the current `pi/` folder there
   (README "Setting up a Raspberry Pi" step 3 download, or `scp -r pi display1@10.184.196.74:signage-pi`).
   No server address needed (it keeps the existing one). Then reboot once. Gives it diagnostics, keyboard access,
   network reporting, SD protection and the restart/shutdown permission.
2. **First new company (Monday)**: follow docs/NEW-COMPANY.md. Claude adds the `companies.json` entry.
3. **Portal-managed updates with test groups** (agreed design, not started):
   - CI publishes versioned releases (Pi player file + Android APK) with sha256 and an **Ed25519 signature**
     (private key in GitHub Secrets, public key baked into the Pi installer).
   - Portal "Updates": pick screens (test group first, then all) → target version; delivered on check-in.
   - Pi: small root-owned updater verifies signature + checksum, installs, restarts; **auto-rollback** if the new
     version doesn't check in within a few minutes. Portal shows up to date / updating / rolled back.
   - Android: download + Android "Install?" prompt (one click on the remote). On Android TV boxes provisioned as
     **device owner** (one-time `adb` setup on a fresh device) installs can be silent and the app can be locked as kiosk.
4. **Hardware for new customers**: evaluate a Google/Android TV box (device-owner mode, boot behaviour on Android 12+);
   **verify whether the Amazon Signage Stick allows sideloading** before recommending it (believed to require
   Amazon Appstore / MDM distribution). Pi 4/5 recommended over Pi 3 for video (cooler, 4K/HEVC).
5. **Test on a real Fire TV stick**: auto-start after power-up (open the app once first), Menu-button diagnostics,
   video playback, pause.
6. Optional: admin access limited to named emails instead of the whole `@domain`; rename Pi hostnames
   (`sudo hostnamectl set-hostname display2`); GitHub Actions warnings (bump actions to Node 24 versions,
   `setup-java@v5`); run `server/test/run.sh` in CI.

## 7. Gotchas we hit (save yourself the time)

- Cloudflare: **enable R2** and **register a workers.dev subdomain** before the first deploy; new subdomains need a
  few minutes before TLS works.
- Cloudflare Access on workers.dev: default scope is **"Previews only"**: must be **All traffic**. The emailed-code
  login needs **One-time PIN** added as an identity provider, otherwise only "Cloudflare" login is offered.
- API token: build a **custom** token; templates add unneeded permissions; scope it to the one account.
- New Pi: SSH refuses connections for **2–3 minutes** on first boot. Wi-Fi stays off unless the **country** was set in Imager.
- Pi 3 on Raspberry Pi OS Lite (trixie): SDL/KMS output didn't reach the TV, hence framebuffer for photos + mpv GPU for video.
- Browsers can't decode MPEG-2 (DVD) or (on Windows Chrome) HEVC: the admin page names the codec and suggests converting.
- Converting media needs the page **visible** (hidden tabs stop drawing frames). It now fails fast instead of hanging.
- Local dev: start the two `wrangler dev` servers **one after the other** (they share a SQLite file and can lock).
- Guest Wi-Fi (RinconGuest) is isolated from the office LAN, so a Pi on Wi-Fi only can't be SSH'd from the office.

## 8. Working with the owner (Randy)

- Explain choices plainly and keep him informed; he tests on real devices and reports what he sees.
- He types all passwords himself (Pi `sudo`, Wi-Fi). Commands needing them are opened in a terminal tab for him.
- Commit + push after changes are verified; every push deploys, so test locally first (`bash server/test/run.sh`)
  and check the Actions run afterwards.
- Bump `PORTAL_VERSION` (server/src/index.js) and the Pi `VERSION` (pi/signage_player.py) on user-visible changes.
