# Server tests

End-to-end tests against a local copy of the server (no Cloudflare account needed).

```bash
cd server
npm install
bash test/run.sh
```

Needs Node.js 22+ and `curl`. `run.sh` wipes the local test database, starts the device API (:8787) and the admin API
(:8788, with the login bypass that only works on localhost), runs every suite and stops the servers.

| Suite | What it checks |
|---|---|
| `jwt` | Cloudflare Access login check: valid token, wrong audience/issuer, expired, tampered, unknown key (no server needed) |
| `e2e` | Pairing, uploads, dates/scheduling, EXIF stripping, per-playlist access control, commands, revoking (expects an empty database: runs first) |
| `pause` | "Pause all screens": old players get an empty list, new ones keep their media |
| `bulk` | Bulk move / add / remove / delete, validation, cross-site request blocking |
| `video` | Video upload limits, thumbnails, only video-capable players get videos |
| `net` | Wi-Fi/Ethernet reporting and cleaning of bad values |
| `power` | Restart / shut down / diagnostics commands are delivered once, unknown commands rejected |
| `windows` | Windows player download links (404 until built), a `win-` player pairing, reporting and receiving commands |

A suite can also be run on its own while the servers are up: `node test/bulk.test.mjs`.
