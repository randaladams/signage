# Digital signage: network checklist for IT

Please confirm these for the network(s) the signage screens will use. The screens are small players
(Raspberry Pi or Android TV / Fire TV sticks) plugged into TVs.

## In short
- Screens **only make outgoing HTTPS connections** to one address. **Nothing connects to them** from outside:
  no port forwarding, no inbound firewall rules, no VPN.
- They work fine on a **guest / isolated network** (client isolation is OK).
- They keep showing their saved content if the internet drops, and catch up when it's back.

## Please confirm

| # | Requirement | Details |
|---|---|---|
| 1 | **Outgoing HTTPS (TCP 443)** allowed to the signage server | `signage-device.<name>.workers.dev` (Cloudflare). If you filter by domain, allow `*.workers.dev` or that exact name. |
| 2 | **DNS** works (any resolver) | Screens look up the address above. |
| 3 | **Time sync (NTP, UDP 123)** allowed | Screens need correct time for secure connections and scheduling (Pi uses `*.debian.pool.ntp.org`; Android/Fire TV use their vendor's time servers). |
| 4 | **No login / "accept terms" page** on the Wi-Fi (captive portal) | Unattended screens can't click through one. A plain Wi-Fi password is fine. If there is a portal, please allow-list the screens' MAC addresses. |
| 5 | **Wi-Fi band** | Raspberry Pi 3 screens only see **2.4 GHz** networks. Pi 4/5 and most streaming sticks also use 5 GHz. |
| 6 | **DHCP** gives the screens an address | A DHCP reservation per screen is nice-to-have (makes support easier), not required. |
| 7 | Wired **Ethernet** where possible | Most reliable. With both cable and Wi-Fi, a Pi uses the cable and falls back to Wi-Fi automatically. |
| 8 | **During setup only**: outgoing HTTPS to `github.com`, `deb.debian.org`, `archive.raspberrypi.com` (Raspberry Pi) or the Amazon/Google app stores (sticks) | To download the player software. Not needed afterwards. |
| 9 | **Bandwidth** | Tiny: each screen checks in once a minute (a few KB) and downloads each photo/video once (photos ~0.5 MB, videos up to ~25 MB). |

## Information we need back
- Wi-Fi network name (SSID) for the screens, if not using cable. The password is typed directly on the device during setup;
  it doesn't need to be emailed.
- Whether there's a captive portal, and any domain allow-list we must be added to.
- Where the screens will be mounted, and whether there's a network port there.

## Security notes
- Each screen has its own secret key; a lost or stolen screen can be cut off instantly from the admin page.
- Photos are stored privately and only served to paired screens; location/camera data is removed on upload.
- The admin page is protected by Cloudflare Access (company email + one-time code).
