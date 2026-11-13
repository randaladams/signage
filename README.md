# Signage

Free, private digital signage for Fire TV Sticks and Raspberry Pis.

- **Admin page** (phone or desktop): upload photos, choose which screens show them, set start/stop dates, see which screens are online.
- **Screens** (Fire Sticks or Raspberry Pis, mixed freely): show a full-screen slideshow, keep working offline from saved copies, start themselves after a power cut.
- **Hosting**: Cloudflare free tier (Workers + R2 + D1). Code and builds: this GitHub repo.

```
Manager's phone ──(sign in with emailed code)──► signage-admin.<you>.workers.dev
                                                     │
                                         D1 database + R2 image storage (private)
                                                     │
Fire Sticks ──(HTTPS, outgoing only, per-screen key)──► signage-device.<you>.workers.dev
```

## How it stays secure

- Images are never public. Each screen gets its own secret key when paired; removing a screen in the admin page cuts it off instantly and wipes its saved images.
- The admin page sits behind Cloudflare Access (only email addresses you approve, one-time code login), **and** the server re-checks every request itself, so it stays locked even if Access is misconfigured.
- Photos are shrunk to TV size and stripped of location/camera data (EXIF) in the browser, and stripped again on the server.
- Screens never accept incoming connections, so guest Wi-Fi is fine.
- Images are auto-deleted 30 days after their end date.

---

## One-time setup

### 1. Cloudflare: turn on R2 and make sure you have a workers.dev subdomain

1. Cloudflare dashboard → **R2 Object Storage** → follow the prompt to enable R2.
   Cloudflare asks for a payment method even for the free tier (10 GB storage, which is far more than this needs); nothing is charged within the free limits.
   Without this, the deploy fails with *"Please enable R2 through the Cloudflare Dashboard. [code: 10042]"*.
2. Cloudflare dashboard → **Workers & Pages**. If it asks you to pick a `*.workers.dev` subdomain, pick one (e.g. `adamselite-signage`). If it doesn't ask, you already have one.

### 2. GitHub secrets

Repo → **Settings → Secrets and variables → Actions → Secrets → New repository secret**:

| Secret | Value |
|---|---|
| `CLOUDFLARE_API_TOKEN` | the `signage_api` token (already added) |
| `CLOUDFLARE_ACCOUNT_ID` | your account ID (already added) |
| `ANDROID_KEYSTORE_B64` | the entire contents of `.secrets/keystore-base64.txt` |
| `ANDROID_KEYSTORE_PASSWORD` | the contents of `.secrets/keystore-password.txt` |

> **Back up the `.secrets` folder** (e.g. in your password manager) and keep it out of git (it's already ignored).
> It's the app's signing key. If it's lost, future app updates can't install over the old one, and every screen has to be reinstalled and re-paired.

### 3. Deploy the server

Push to `main` (or **Actions → Deploy server → Run workflow**). The first run creates the database and image storage automatically.
When it finishes, the run's summary page shows both URLs:

- Admin page: `https://signage-admin.<subdomain>.workers.dev`
- Device API: `https://signage-device.<subdomain>.workers.dev`

The admin page will say **"Admin is locked"** until step 4 is done. That's intentional.

### 4. Lock the admin page with Cloudflare Access

*(Steps as of Nov 2026; Cloudflare renames menus often.)*

1. Dashboard → **Workers → signage-admin → Domains** tab.
   Under the `signage-admin.<subdomain>.workers.dev` row, click **Enable Access**.
   It shows an **AUD** value and a **JWKS URL** like `https://<team>.cloudflareaccess.com/cdn-cgi/access/certs`. Note both.
   *(If prompted to set up Zero Trust first: pick a team name and the Free plan.)*
2. The row now says *"An Access policy applies to this URL"*. Click **Manage**, then **Manage access**:
   - **Scope:** choose **All traffic**. The default, *Previews only*, leaves the real address public.
   - **Authentication policy → + Add policy → Email domain** → `adamselite.com`.
     (Keep the *Cloudflare account* policy too; it lets the account owner in with their Cloudflare login.)
   - **Save changes.** Don't click *Remove Worker-specific Access*.
3. Turn on the emailed-code login: **Cloudflare One** (Zero Trust) home → **Connect an identity provider**
   (or Integrations → Identity providers) → **Add** → **One-time PIN** → save.
4. GitHub → **Settings → Secrets and variables → Actions → Variables** tab → add two **variables** (not secrets):
   - `ACCESS_TEAM_DOMAIN` = `<team>.cloudflareaccess.com` (the part of the JWKS URL between `https://` and `/cdn-cgi`)
   - `ACCESS_AUD` = the AUD value
5. **Actions → Deploy server → Run workflow**.
6. Test in a private browser window: the admin address should show the Cloudflare Access login with an **Email** box.
   Enter a company address, type the emailed code, and the admin page opens.

> The *Email domain* policy lets **anyone** with an `@adamselite.com` address sign in.

> ⚠️ Do **not** turn on Access for `signage-device`. The screens can't sign in; they use their own keys.

### 5. Build the Fire Stick app

**Actions → Build player app → Run workflow** (it also runs automatically when files in `android/` change).
It builds the APK, signs it, and publishes it at `https://signage-device.<subdomain>.workers.dev/get` for easy install.
The APK is also attached to the workflow run as a download.

---

## Setting up a Fire Stick (about 10 minutes each)

1. Connect the stick to the TV and to the guest Wi-Fi.
2. **Settings → My Fire TV → Developer options** → turn on **Apps from Unknown Sources**.
   *(If Developer options is missing: Settings → My Fire TV → About → click the device name 7 times.)*
3. From the home screen, search for and install the **Downloader** app (free, Amazon Appstore).
   If Fire OS asks, allow Downloader to install unknown apps.
4. Open Downloader, type `https://signage-device.<subdomain>.workers.dev/get` → **Go** → **Install** → **Open**.
5. The TV shows a **6-character pairing code**. On your phone, open the admin page → **Screens → Add a screen**,
   type the code, give the screen a name, choose a playlist → **Add screen**. Images appear within seconds.
6. Fire Stick settings to change once:
   - **Settings → Display & Sounds → Screen Saver → Start Time → Never**
   - Optional: **Settings → Preferences → Featured Content** → turn off autoplay video/audio.
7. Optional: delete the Downloader app once all screens are done.

After that, leave it alone. It starts the slideshow after power cuts and checks for new images every 2 minutes.
If someone presses Home, the slideshow comes back on its own after 10 minutes.

## Setting up a Raspberry Pi instead (Pi 3 or newer)

The Pi uses the same server, admin page, playlists and pairing as the Fire Sticks.

1. Flash **Raspberry Pi OS Lite (64-bit)** to the SD card with **Raspberry Pi Imager**. When it offers to edit settings (OS customisation), set:
   - **hostname** (e.g. `display2`), **username and password**
   - **Wireless LAN:** network name, password, and **Wireless LAN country** (e.g. US). Without the country, the Pi's Wi-Fi stays switched off.
     A Pi 3 only sees **2.4 GHz** networks. Skip this if the Pi will use Ethernet.
   - **Services → Enable SSH** (password authentication)
2. Put the card in the Pi, connect the TV (before powering on) and power up. Give it a minute, then find its address (from your router, or try `<hostname>.local`).
3. Get the player onto the Pi. From a PC, SSH in and download it from GitHub:
   ```bash
   ssh <user>@<pi-address>
   ```
   then on the Pi:
   ```bash
   curl -fsSL https://github.com/randaladams/signage/archive/refs/heads/main.tar.gz | tar xz && rm -rf ~/signage-pi && mv signage-main/pi ~/signage-pi && rm -rf signage-main
   ```
   *(This works while the repo is public. If it's private, copy the folder from a PC instead: `scp -r pi <user>@<pi-address>:signage-pi`.)*
4. Still on the Pi, run the installer (asks for the Pi user's password once):
   ```bash
   sudo bash ~/signage-pi/install.sh
   ```
5. The TV shows a pairing code. Add it in the admin page → **Screens → Add a screen**.
6. Reboot once (`sudo reboot`) to hide the console cursor. After that it starts the slideshow by itself on every power-up.

**Adding or changing a Pi's Wi-Fi later** (over SSH, or with a keyboard plugged in):
```bash
sudo bash ~/signage-pi/wifi-scan.sh US
```
turns Wi-Fi on (setting the country) and lists the networks the Pi can see (a Pi 3 only sees 2.4 GHz). Then:
```bash
sudo nmcli --ask device wifi connect "Network Name" ifname wlan0
```
and type the Wi-Fi password when asked (`--ask` keeps it out of the command history).
The Pi remembers every network it has joined and uses whichever is in range, so you can add a new network before the old one goes away.
With both cable and Wi-Fi connected, the cable is preferred and Wi-Fi is the automatic backup.
Note the Pi gets a **different IP address on Wi-Fi** (check your router, or run `ip -4 addr` on the Pi).

How it works: a small Python program (`/opt/signage-player/signage_player.py`) runs as a systemd service and
writes photos straight to the screen's framebuffer, so no desktop is needed. Videos are handed to `mpv`, which draws
them with the Pi's GPU (measured smooth at 720p on a Pi 3, ~30% CPU). There's a ~2 second black gap while a video starts.
It restarts itself if it crashes and starts at boot.

> Video makes a Pi 3 run warm (~75 °C). Fit a heatsink, or a case with a small fan, on any Pi that plays videos.

**SD card protection.** Power cuts during a write are what corrupt SD cards, so the installer keeps routine writes in RAM:
downloaded photos/videos and the playlist (a RAM disk, re-downloaded after each reboot), system logs, and swap.
The only thing written to the card in normal running is the pairing token, once. Trade-offs:
- After a reboot the screen needs internet to download its media again (a minute or two). Without a reboot it keeps playing offline as before.
- Media per Pi is limited to about 350 MB (40% of a Pi 3's RAM), which is plenty for photos and a handful of short videos.
- Automatic package-list updates are switched off. To update the system: `sudo apt update && sudo apt full-upgrade`.
- Logs don't survive a reboot.

**Restart / Shut down.** Pi screens have **Restart device** and **Shut down** buttons in the admin page's Screens tab.
Use *Shut down* before unplugging or moving a Pi; it stays off until its power is unplugged and reconnected.

- **Logs:** `journalctl -u signage-player -f`
- **Update the player:** copy a new `signage_player.py` into `~/signage-pi/` and run `bash ~/signage-pi/update.sh` (no password needed).

### Updating the app later

Run **Build player app**, then on each stick open Downloader with the same `/get` address and install. It installs over the old version and stays paired.
(Fire OS requires that one click on each stick; it can't be done silently for sideloaded apps.)

---

## Day-to-day (for the manager)

- **Add photos**: Media tab → tick which playlists → optionally set *start* / *stop showing after* dates → **Choose photos…**
  Example: November birthdays → stop showing after Nov 30. It disappears on its own on Dec 1.
- **Animated GIFs** are turned into short videos automatically (short ones repeat to last at least 10 s); still GIFs stay photos.
- **Add videos** the same way (up to 60 seconds; they play without sound). The page re-records each video
  to 720p MP4 before uploading, which removes location data and makes iPhone videos playable on every screen.
  It takes as long as the video itself, so keep the page open and on screen until it says "uploaded".
  Works in Chrome, Edge and Safari (not Firefox). In "keep screens in step" playlists a video counts for its own length.
- **Order**: ↑ / ↓ on each image.
- **Edit / Delete**: buttons on each image.
- **Screens tab**: see each screen's online status, change its playlist, *Refresh now*, *Restart app*, or *Remove*.
  Raspberry Pi screens also have *Restart device* and *Shut down* (use before unplugging).
- **Playlists tab**: seconds per image, and *Keep screens in step* (screens on the same playlist change images at the same moment).

Changes reach the screens within about 2 minutes.

---

## Troubleshooting

**Start with the diagnostics page on the screen itself.** It works even when the screen can't reach the server:
- **Raspberry Pi:** plug in any USB keyboard and press any key. *Esc* closes it; any other key re-checks.
- **Fire Stick:** press the **Menu (☰)** button on the remote. *Back* closes it.
- **From the admin page** (only if the screen is still online): Screens → **Show diagnostics**.

It runs a step-by-step connection check: cable or Wi-Fi → IP address → router → internet names (DNS) →
signage server → pairing key. **The first red line is where the problem is.** Below that it shows the player version,
last successful check-in and last error, items saved on the screen, memory, and on a Pi the temperature and power
warnings (an *under-voltage* warning means the power supply is too weak; use the official Pi supply). It refreshes every
15 seconds and closes itself after 5 minutes.

| Problem | Fix |
|---|---|
| Admin page says "Admin is locked" | Finish step 4 (repo variables) and re-run **Deploy server**. |
| Admin page says "Forbidden" | Your email isn't in the Access policy, or the AUD/team domain variables are wrong. |
| TV shows "Can't reach server" | Check the stick's Wi-Fi. It keeps showing saved images meanwhile. |
| TV shows a pairing code again | The screen was removed in the admin page; pair it again. |
| Screen shows "Offline" in admin | It hasn't checked in for 6+ minutes: power/Wi-Fi/TV input. |
| Fire Stick slideshow didn't start after a reboot | Open the app once by hand after installing; Fire OS only allows auto-start for apps that have been opened. |
| Pi shows only a blinking cursor | Check the player: `systemctl status signage-player` and `journalctl -u signage-player -n 50`. |
| Pi has no Wi-Fi (`nmcli radio wifi` says disabled) | No Wi-Fi country set. Run the three commands under *Changing a Pi's Wi-Fi later*. |

## Repo layout

```
server/                 Cloudflare Worker (one codebase, deployed as signage-admin and signage-device)
  src/index.js          API + routing
  src/access.js         Cloudflare Access login verification
  src/admin.html        Admin web page
  migrations/           Database schema
android/                Fire Stick player app (Kotlin, no third-party libraries)
pi/                     Raspberry Pi player (Python + pygame, framebuffer output) and installer
.github/workflows/      deploy-server.yml, build-player.yml
```

### Local development (optional)

Needs Node.js 22+.

```bash
cd server
npm install
npx wrangler d1 migrations apply signage --local --env device
npx wrangler dev --env device --port 8787
npx wrangler dev --env admin --port 8788 --var DEV_NO_AUTH:1   # login bypass works on localhost only
```
