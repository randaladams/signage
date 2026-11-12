# Signage

Free, private digital signage for Fire TV Sticks.

- **Admin page** (phone or desktop): upload photos, choose which screens show them, set start/stop dates, see which screens are online.
- **Screens** (Fire Sticks): show a full-screen slideshow, keep working offline from saved copies, start themselves after a power cut.
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

### 1. Cloudflare: make sure you have a workers.dev subdomain

Cloudflare dashboard → **Workers & Pages**. If it asks you to pick a `*.workers.dev` subdomain, pick one (e.g. `adamselite-signage`). If it doesn't ask, you already have one.

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

1. Dashboard → **Workers & Pages → signage-admin → Settings → Domains & Routes**.
   On the `workers.dev` row, turn on **Cloudflare Access** (wording may vary slightly).
   If prompted to set up Zero Trust first: choose a team name and the **Free** plan.
   *(Cloudflare may ask for a card on file even for the free plan; it isn't charged.)*
2. Open **Zero Trust → Access → Applications** and click the new `signage-admin` application.
   - **Policies**: set it to *Allow* → *Include* → **Emails** → add your email and the manager's email.
   - **Login methods**: keep **One-time PIN** on (it emails a 6-digit code; no Google/Microsoft needed).
   - Copy the **Application Audience (AUD) Tag** from the application's overview.
3. Find your **team domain**: Zero Trust → **Settings → Custom pages** (or Settings → General). It looks like `yourteam.cloudflareaccess.com`.
4. Back in GitHub → **Settings → Secrets and variables → Actions → Variables** tab → add two **variables** (not secrets):
   - `ACCESS_TEAM_DOMAIN` = `yourteam.cloudflareaccess.com`
   - `ACCESS_AUD` = the AUD tag
5. **Actions → Deploy server → Run workflow**. Open the admin page: you'll get the Cloudflare sign-in, then the admin page.

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

### Updating the app later

Run **Build player app**, then on each stick open Downloader with the same `/get` address and install. It installs over the old version and stays paired.
(Fire OS requires that one click on each stick; it can't be done silently for sideloaded apps.)

---

## Day-to-day (for the manager)

- **Add photos**: Images tab → tick which playlists → optionally set *start* / *stop showing after* dates → **Choose photos…**
  Example: November birthdays → stop showing after Nov 30. It disappears on its own on Dec 1.
- **Order**: ↑ / ↓ on each image.
- **Edit / Delete**: buttons on each image.
- **Screens tab**: see each screen's online status, change its playlist, *Refresh now*, *Restart app*, or *Remove*.
- **Playlists tab**: seconds per image, and *Keep screens in step* (screens on the same playlist change images at the same moment).

Changes reach the screens within about 2 minutes.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| Admin page says "Admin is locked" | Finish step 4 (repo variables) and re-run **Deploy server**. |
| Admin page says "Forbidden" | Your email isn't in the Access policy, or the AUD/team domain variables are wrong. |
| TV shows "Can't reach server" | Check the stick's Wi-Fi. It keeps showing saved images meanwhile. |
| TV shows a pairing code again | The screen was removed in the admin page; pair it again. |
| Screen shows "Offline" in admin | It hasn't checked in for 6+ minutes: power/Wi-Fi/TV input. |
| Slideshow didn't start after a reboot | Open the app once by hand after installing; Fire OS only allows auto-start for apps that have been opened. |

## Repo layout

```
server/                 Cloudflare Worker (one codebase, deployed as signage-admin and signage-device)
  src/index.js          API + routing
  src/access.js         Cloudflare Access login verification
  src/admin.html        Admin web page
  migrations/           Database schema
android/                Fire Stick player app (Kotlin, no third-party libraries)
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
