# Setting up signage for a new company

Each company gets **its own Cloudflare account** (their photos, screens and logins are completely separate),
while all companies run the **same code from this one GitHub repo**: one push updates everyone.

Allow **about 1½ hours** for the first company (most of it waiting on Cloudflare emails and the first Pi install),
then **~15 minutes per screen**.

> Screens shown are as of November 2026. Cloudflare moves its menus around; if something doesn't match,
> look for the same words nearby (the *settings* are what matter, not where the button lives).

---

## 0. Before you start: collect this

| What | Example | Notes |
|---|---|---|
| A short **company id** | `acme` | lowercase letters/numbers/dashes. Used in GitHub and `companies.json`. |
| A **workers.dev name** for their addresses | `acme-signage` | becomes `signage-admin.acme-signage.workers.dev`. Hard to change later (it's built into the screens). |
| **Who will manage the screens** | `jane@acme.com` | their email domain (`acme.com`) is what gets admin access. |
| **Who owns the Cloudflare account** | someone at the company, or you | see step 1. |
| A **payment card** for Cloudflare | | Cloudflare asks for one to enable storage (R2) and Zero Trust, even on the free plans. Nothing is charged within the free limits. |
| **Their network details** | | send them [NETWORK-CHECKLIST.md](NETWORK-CHECKLIST.md) ahead of time. |

---

## 1. Cloudflare account

**Recommended:** the company owns the account and adds you as an administrator, so they keep control if you part ways.

1. Someone at the company signs up at **dash.cloudflare.com** (free).
2. They invite you: **Manage Account → Members → Invite** → your email → role **Super Administrator** (or Administrator).
3. Accept the emailed invite. In the dashboard's account picker you'll now see their account. **Make sure their account
   is the one selected for every step below.**

*(Alternatively you create the account yourself with a company email address and hand over the login later.)*

Note the **Account ID**: Workers & Pages overview page (right side), or the long number in the dashboard URL
after `dash.cloudflare.com/`.

## 2. Turn on storage (R2)

Dashboard → **R2 Object Storage** → follow the prompt to enable it (payment card required; the free tier is 10 GB).

*Skipping this makes the first deploy fail with "Please enable R2 through the Cloudflare Dashboard. [code: 10042]".*

## 3. Pick the workers.dev name

Dashboard → **Workers & Pages** (or **Compute (Workers)**). If it asks you to choose a `*.workers.dev` subdomain,
enter the name from step 0 (e.g. `acme-signage`). If it doesn't ask, the account already has one (it's shown on that page).

*Skipping this makes the deploy fail with "You need to register a workers.dev subdomain".*
*A brand-new subdomain can take a few minutes before it answers (secure connection errors until then).*

## 4. API token for deploying

**My Profile → API Tokens → Create Token → Create Custom Token** (at the bottom; *don't* use a template).

- **Token name:** `signage_<company id>`
- **Permissions** (add each row; third dropdown in brackets):

  | | | |
  |---|---|---|
  | Account | Workers Scripts | **Edit** |
  | Account | Workers R2 Storage | **Edit** |
  | Account | D1 | **Edit** |
  | Account | Account Settings | Read |
  | Account | Workers Tail | Read |
  | User | User Details | Read |
  | User | Memberships | Read |

- **Account Resources:** *Include →* **the company's account** (not "All accounts").
  In the newer layout this is inside the **Account policy** card: click its **pencil** and pick the account.
- No Zone permissions. IP filtering: leave empty. **TTL:** 1 year (set a reminder to renew).
- **Continue to summary.** It should show exactly two blocks: *Entire \<company\> account* with 5 permissions, and *User* with 2.
- **Create Token** and copy it immediately (shown once).

## 5. GitHub: add the company

On GitHub, repo **randaladams/signage**:

1. **Settings → Environments → New environment** → name it exactly the **company id** (e.g. `acme`).
2. In that environment, **Environment secrets → Add**:
   - `CLOUDFLARE_API_TOKEN` = the token from step 4
   - `CLOUDFLARE_ACCOUNT_ID` = the account id from step 1
3. **Environment variables → Add**:
   - `COMPANY_ID` = the company id (e.g. `acme`). **Required**: it's a safety check that stops a half-set-up company
     from deploying into the wrong Cloudflare account.
4. Add the company to **`companies.json`** in the repo and commit:
   ```json
   { "id": "acme", "name": "Acme Corp", "environment": "acme" }
   ```
   (Claude can do this for you. Do **not** add `uses_repo_settings`; that's only for the original Adams Elite setup.)

> The Android app's signing key (`ANDROID_KEYSTORE_B64` / `ANDROID_KEYSTORE_PASSWORD`) stays as a **repository**
> secret shared by all companies. Nothing to add per company.

## 6. First deploy

**Actions → Deploy server → Run workflow** → *company* = `acme` → **Run**.

It creates their database and storage and deploys. When it finishes (about a minute), open the run: the **summary**
lists their admin page, the app download link and the **Raspberry Pi install command** (copy it for step 10).

The admin page will say **"Admin is locked"**. That's expected until step 7.

## 7. Lock the admin page with Cloudflare Access (company logins)

1. Dashboard → **Workers → signage-admin → Domains** tab. Under the `signage-admin.<name>.workers.dev` row click
   **Enable Access**. If prompted to set up **Zero Trust**: pick a team name (anything; Cloudflare may suggest one)
   and the **Free** plan.
2. Cloudflare shows an **AUD** value (long hex string) and a **JWKS URL** like
   `https://<team>.cloudflareaccess.com/cdn-cgi/access/certs`. Copy both.
3. The row now says *"An Access policy applies to this URL"* → **Manage** → **Manage access**:
   - **Scope: All traffic.** ⚠️ The default *"Previews only"* leaves the real admin page unprotected (it then just shows "Forbidden").
   - **Add policy → Email domain** → the company's domain (e.g. `acme.com`). Keep the *Cloudflare account* policy too.
   - **Save changes.** (Never click *Remove Worker-specific Access*.)
4. Turn on the emailed-code login: open **Zero Trust / Cloudflare One** → **Connect an identity provider**
   (or *Integrations → Identity providers*) → **Add** → **One-time PIN** → save.
   *Without this, the login page only offers "Cloudflare" and the company's staff can't get in.*
5. GitHub → **Settings → Environments → acme → Environment variables**, add:
   - `ACCESS_TEAM_DOMAIN` = `<team>.cloudflareaccess.com` (the JWKS URL between `https://` and `/cdn-cgi`)
   - `ACCESS_AUD` = the AUD value
6. **Actions → Deploy server → Run workflow** → company `acme` again.
7. **Test** in a private/incognito window: the admin address should show *Cloudflare Access* with an **Email** box.
   Enter a company address, type the emailed code, and the admin page opens.

> ⚠️ Never turn on Access for **signage-device**. Screens can't log in; they use their own keys.

## 8. Build their Android / Fire Stick app

**Actions → Build player app → Run workflow** → company `acme`. Takes ~2 minutes. The app is then downloadable at
`https://signage-device.<name>.workers.dev/get` (built for their server only).

If they'll use **Windows PCs** as screens: **Actions → Build Windows player → Run workflow** → company `acme`
(~3 minutes). Downloadable at `https://signage-device.<name>.workers.dev/get/windows`.

## 9. Set up the admin page

Log in to their admin page:
1. **Settings → Time zone**: set it (start/end dates switch at midnight there).
2. **Playlists**: create one per area if screens should show different things (e.g. *Lobby*, *Break Room*);
   otherwise use *All Screens*.
3. **Media**: upload a few test photos.

## 10. Screens

### Raspberry Pi (3 or newer)
Follow **README → Setting up a Raspberry Pi**, but use **this company's install command** from the step 6 summary:
```bash
sudo bash ~/signage-pi/install.sh https://signage-device.<name>.workers.dev
```
In Raspberry Pi Imager remember: SSH on, Wi-Fi **country** set (if using Wi-Fi). First power-up: wait **2–3 minutes**
before SSH works. After pairing, **reboot once** (or use *Restart device* in the admin page).

### Fire Stick / Android TV
On the device: allow apps from unknown sources, install **Downloader**, open `https://signage-device.<name>.workers.dev/get`,
install, **open the app once**, then pair the code shown on the TV in **Screens → Add a screen**.

### Windows 10 / 11 PC
In a browser on the PC open `https://signage-device.<name>.workers.dev/get/windows`, run `SignagePlayer.exe`
(**More info → Run anyway** on the blue SmartScreen warning), then pair the code it shows. Details, and settings for a
dedicated PC (auto sign-in, never sleep), in **README → Setting up a Windows 10 / 11 PC**.

## 11. Before you leave site: checklist

- [ ] Every screen shows **Online** in the admin page's status strip, with the expected playlist.
- [ ] **Screens tab**: each shows its network (🔌 Ethernet / 📶 Wi-Fi). Wi-Fi signal above 40% (no "weak" badge).
- [ ] Pull the power on one screen and plug it back in: the slideshow returns by itself within ~1–2 minutes.
- [ ] Open **diagnostics** once on each screen type (Pi: plug in a keyboard, press any key; Fire Stick: Menu button) so you know it works there.
- [ ] Manager can log in with the emailed code from their own phone.
- [ ] Hand over: their admin page address, the README *Day-to-day* section, and how to reach you.

## Troubleshooting what we've actually hit

| Symptom | Cause / fix |
|---|---|
| Deploy fails: *"Please enable R2 … [code: 10042]"* | Step 2 not done. |
| Deploy fails: *"register a workers.dev subdomain"* | Step 3 not done. |
| Deploy fails: *"must have the variable COMPANY_ID"* | Step 5.3: add `COMPANY_ID` to the environment. |
| Admin page: *"Admin is locked"* | Step 7.5–7.6 not done yet (or the deploy wasn't re-run). |
| Admin page: plain *"Forbidden"* | Access scope is *Previews only*: set **All traffic** (7.3). Or the AUD/team domain variables are wrong. |
| Login page only shows *"Cloudflare"*, no email box | One-time PIN not added (7.4). |
| New addresses give secure-connection errors | New workers.dev subdomain still propagating; wait a few minutes. |
| SSH to a new Pi: *Connection refused* | First boot still finishing; wait 2–3 min. If it persists, SSH wasn't enabled in Imager. |
| Pi has no Wi-Fi | Country not set in Imager: `sudo bash ~/signage-pi/wifi-scan.sh US`, then `sudo nmcli --ask device wifi connect "<name>" ifname wlan0`. |
| Screen offline but powered | Use its **diagnostics page**: the first red line shows where it breaks. |
