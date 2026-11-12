#!/usr/bin/env python3
"""Signage player for Raspberry Pi (Raspberry Pi OS Lite, no desktop needed).

Talks to the same device API as the Fire TV app:
  - shows a pairing code until the screen is added in the admin page
  - downloads images to local storage and plays from there (works offline)
  - optional lock-step mode: every screen on a playlist shows the same image at the same time
  - follows "Refresh now" / "Restart app" commands from the admin page

Runs as a systemd service (see install.sh). Only needs python3-pygame from apt.
"""

import json
import logging
import os
import platform
import sys
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime
from zoneinfo import ZoneInfo

os.environ.setdefault("PYGAME_HIDE_SUPPORT_PROMPT", "1")
# pygame is only used to decode, resize and draw text off-screen; frames go straight to /dev/fb0.
os.environ["SDL_VIDEODRIVER"] = "dummy"
import pygame  # noqa: E402

VERSION = "pi-1.1.0"
SERVER = os.environ.get("SIGNAGE_SERVER_URL", "").rstrip("/")
STATE_DIR = os.environ.get("SIGNAGE_STATE_DIR", os.path.expanduser("~/.signage-player"))
IMAGE_DIR = os.path.join(STATE_DIR, "images")
TOKEN_FILE = os.path.join(STATE_DIR, "token")
MANIFEST_FILE = os.path.join(STATE_DIR, "manifest.json")
FADE_SEC = 0.9
FPS = 25

log = logging.getLogger("signage")


# ------------------------------------------------------------------ storage

def read_file(path):
    try:
        with open(path, "r", encoding="utf-8") as f:
            return f.read()
    except OSError:
        return None


def write_atomic(path, data):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(data)
    os.replace(tmp, path)


def image_path(image_id):
    return os.path.join(IMAGE_DIR, image_id + ".jpg")


def clear_all():
    for name in os.listdir(IMAGE_DIR):
        try:
            os.remove(os.path.join(IMAGE_DIR, name))
        except OSError:
            pass
    for p in (TOKEN_FILE, MANIFEST_FILE):
        try:
            os.remove(p)
        except OSError:
            pass


# ------------------------------------------------------------------ network

def device_info(screen_size):
    model = (read_file("/proc/device-tree/model") or platform.machine()).strip("\x00 \n")
    os_name = "Linux"
    rel = read_file("/etc/os-release") or ""
    for line in rel.splitlines():
        if line.startswith("PRETTY_NAME="):
            os_name = line.split("=", 1)[1].strip('"')
    info = f"{model} | {os_name} | {screen_size[0]}x{screen_size[1]}"
    return info.encode("ascii", "replace").decode("ascii")


def http(method, path, token=None, headers=None, timeout=20):
    req = urllib.request.Request(SERVER + path, method=method, data=b"" if method == "POST" else None)
    req.add_header("Accept", "application/json")
    req.add_header("User-Agent", "signage-player/" + VERSION)
    if token:
        req.add_header("Authorization", "Bearer " + token)
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as res:
            return res.status, res.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()


def download(path, token, dest):
    tmp = dest + ".part"
    req = urllib.request.Request(SERVER + path, headers={"Authorization": "Bearer " + token, "User-Agent": "signage-player/" + VERSION})
    try:
        with urllib.request.urlopen(req, timeout=60) as res:
            expected = int(res.headers.get("Content-Length") or -1)
            with open(tmp, "wb") as f:
                while True:
                    chunk = res.read(65536)
                    if not chunk:
                        break
                    f.write(chunk)
        size = os.path.getsize(tmp)
        if size == 0 or (expected > 0 and size != expected):
            raise IOError("incomplete download")
        os.replace(tmp, dest)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)


# ------------------------------------------------------------------ shared state

class State:
    def __init__(self):
        self.lock = threading.Lock()
        self.playlist = None       # dict from manifest (paired) or None
        self.pair_code = None      # str while waiting to be paired
        self.status = ""           # small text bottom-right
        self.clock_offset = 0.0    # server_time - local time (seconds)
        self.reload = False
        self.restart = False
        self.message = "Starting…"


S = State()


def parse_playlist(m):
    pl = m.get("playlist") or {}
    return {
        "name": pl.get("name", ""),
        "interval": max(3, min(3600, int(pl.get("interval_sec") or 10))),
        "sync": bool(pl.get("sync", True)),
        "tz": m.get("tz") or "UTC",
        "paused": bool(m.get("paused")),
        "pause_message": (m.get("pause_message") or "")[:120],
        "items": [
            {"id": i["id"], "start": i.get("start_date"), "end": i.get("end_date")}
            for i in m.get("images", [])
        ],
    }


def signature(pl):
    return json.dumps(pl, sort_keys=True) if pl else ""


# ------------------------------------------------------------------ sync thread

class Syncer(threading.Thread):
    def __init__(self, screen_size):
        super().__init__(daemon=True, name="sync")
        self.headers = {
            "X-App-Version": VERSION,
            "X-Device-Info": device_info(screen_size),
            "X-Capabilities": "pause",  # we keep images cached and blank the screen ourselves when paused
        }
        self.wake = threading.Event()

    def run(self):
        while True:
            try:
                wait = self.sync_once()
            except Exception as e:  # network down, DNS, server error…
                log.warning("sync failed: %s", e)
                with S.lock:
                    S.status = "Offline - showing saved images" if S.playlist else "Can't reach server - retrying"
                wait = 30
            self.wake.wait(wait)
            self.wake.clear()

    def sync_once(self):
        token = (read_file(TOKEN_FILE) or "").strip() or None
        if not token:
            code, body = http("POST", "/v1/register", headers=self.headers)
            if code != 200:
                with S.lock:
                    S.status = f"Pairing unavailable ({code}) - retrying"
                return 30
            j = json.loads(body)
            write_atomic(TOKEN_FILE, j["token"])
            os.chmod(TOKEN_FILE, 0o600)
            with S.lock:
                S.pair_code = j["code"]
                S.playlist = None
            log.info("registered, pairing code %s", j["code"])
            return 5

        code, body = http("GET", "/v1/manifest", token=token, headers=self.headers)
        if code == 401:
            log.info("token rejected (screen removed); re-pairing")
            clear_all()
            with S.lock:
                S.playlist = None
                S.pair_code = None
                S.message = "Re-pairing…"
            return 1
        if code != 200:
            with S.lock:
                S.status = f"Server error {code}"
            return 60

        m = json.loads(body)
        poll = max(5, min(3600, int(m.get("poll_sec") or 120)))
        with S.lock:
            if m.get("server_time"):
                S.clock_offset = m["server_time"] / 1000.0 - time.time()

        if m.get("status") == "pending":
            with S.lock:
                S.pair_code = m.get("code")
                S.playlist = None
            return poll

        # Paired: fetch anything new before switching, so a broken image is never shown.
        keep, failed = set(), 0
        for img in m.get("images", []):
            keep.add(img["id"])
            dest = image_path(img["id"])
            if not os.path.exists(dest) or os.path.getsize(dest) == 0:
                try:
                    download(img["url"], token, dest)
                except Exception as e:
                    log.warning("download %s failed: %s", img["id"], e)
                    failed += 1
        write_atomic(MANIFEST_FILE, body.decode("utf-8"))
        for name in os.listdir(IMAGE_DIR):
            if name.split(".")[0] not in keep or name.endswith(".part"):
                try:
                    os.remove(os.path.join(IMAGE_DIR, name))
                except OSError:
                    pass

        pl = parse_playlist(m)
        with S.lock:
            S.pair_code = None
            S.playlist = pl
            S.status = f"{failed} image(s) still downloading" if failed else ""
            cmd = m.get("command")
            if cmd == "reload":
                S.reload = True
            elif cmd == "restart":
                S.restart = True
        return min(poll, 30) if failed else poll


# ------------------------------------------------------------------ display

class Framebuffer:
    """Writes finished frames straight to the Linux framebuffer (/dev/fb0).

    Works on Raspberry Pi OS Lite with no desktop, no GPU libraries and no special privileges
    (only membership of the 'video' group). Supports 16-bit (RGB565) and 32-bit (XRGB8888) modes.
    """

    def __init__(self, dev="/dev/fb0"):
        name = os.path.basename(dev)
        sysfs = f"/sys/class/graphics/{name}"
        w, h = (int(x) for x in read_file(f"{sysfs}/virtual_size").strip().split(","))
        self.bpp = int(read_file(f"{sysfs}/bits_per_pixel").strip())
        self.stride = int(read_file(f"{sysfs}/stride").strip())
        self.size = (w, h)
        if self.bpp == 16:
            masks = (0xF800, 0x07E0, 0x001F, 0)
        elif self.bpp == 32:
            masks = (0x00FF0000, 0x0000FF00, 0x000000FF, 0)
        else:
            raise RuntimeError(f"unsupported framebuffer depth {self.bpp}")
        self.out = pygame.Surface(self.size, 0, self.bpp, masks)
        self.fd = os.open(dev, os.O_RDWR)
        import mmap
        self.mm = mmap.mmap(self.fd, self.stride * h, mmap.MAP_SHARED, mmap.PROT_READ | mmap.PROT_WRITE)

    def show(self, surface):
        self.out.blit(surface, (0, 0))  # converts colour format
        data = self.out.get_buffer().raw
        pitch = self.out.get_pitch()
        if pitch == self.stride:
            self.mm[0:len(data)] = data
        else:
            row = self.size[0] * self.bpp // 8
            for y in range(self.size[1]):
                self.mm[y * self.stride:y * self.stride + row] = data[y * pitch:y * pitch + row]


class Display:
    def __init__(self):
        pygame.font.init()
        self.fb = Framebuffer(os.environ.get("SIGNAGE_FB", "/dev/fb0"))
        self.size = self.fb.size
        self.screen = pygame.Surface(self.size, 0, 32)  # we draw here, then copy to the screen
        self.last_push = 0.0
        self.last_key = None
        h = self.size[1]
        self.font_big = pygame.font.Font(None, int(h * 0.16))
        self.font_mid = pygame.font.Font(None, int(h * 0.065))
        self.font_small = pygame.font.Font(None, int(h * 0.04))
        self.font_status = pygame.font.Font(None, max(18, int(h * 0.025)))

        self.current_id = None
        self.current = None        # fitted Surface on screen
        self.previous = None       # Surface fading out
        self.fade_start = 0.0
        self.seq_index = -1
        self.next_change = 0.0
        self.last_sig = ""
        self.loader = None         # (image_id, thread, result-holder)
        self.busy = False          # fading or loading: run at full frame rate

    # -- helpers
    def fit(self, img):
        """Scale to fit the screen, letterboxed on black. Safe to call off the main thread."""
        sw, sh = self.size
        iw, ih = img.get_size()
        scale = min(sw / iw, sh / ih)
        w, h = max(1, int(iw * scale)), max(1, int(ih * scale))
        canvas = pygame.Surface(self.size, 0, 32)
        canvas.fill((0, 0, 0))
        if img.get_bitsize() not in (24, 32):
            tmp = pygame.Surface(img.get_size(), 0, 32)
            tmp.blit(img, (0, 0))
            img = tmp
        canvas.blit(pygame.transform.smoothscale(img, (w, h)), ((sw - w) // 2, (sh - h) // 2))
        return canvas

    def start_load(self, image_id):
        holder = {}

        def work():
            # Decode + resize in the background so the slideshow never stutters.
            try:
                holder["surface"] = self.fit(pygame.image.load(image_path(image_id)))
            except Exception as e:
                holder["error"] = e

        t = threading.Thread(target=work, daemon=True)
        t.start()
        self.loader = (image_id, t, holder)

    def visible_items(self, pl, now):
        today = datetime.fromtimestamp(now, ZoneInfo(pl["tz"])).strftime("%Y-%m-%d")
        out = []
        for it in pl["items"]:
            if it["start"] and it["start"] > today:
                continue
            if it["end"] and it["end"] < today:
                continue
            p = image_path(it["id"])
            if os.path.exists(p) and os.path.getsize(p) > 0:
                out.append(it)
        return out

    def text_center(self, lines):
        self.screen.fill((0, 0, 0))
        total = sum(f.get_linesize() for f, _ in lines)
        y = (self.size[1] - total) // 2
        for font, text in lines:
            surf = font.render(text, True, (255, 255, 255))
            self.screen.blit(surf, ((self.size[0] - surf.get_width()) // 2, y))
            y += font.get_linesize()

    # -- per frame
    def frame(self):
        now_local = time.time()
        with S.lock:
            pl, code, status, offset = S.playlist, S.pair_code, S.status, S.clock_offset
            msg = S.message
            if S.reload:
                S.reload = False
                self.current_id, self.seq_index, self.next_change = None, -1, 0
            if S.restart:
                log.info("restart requested")
                pygame.quit()
                sys.exit(0)  # systemd restarts us
        now = now_local + offset

        # Work out what should be on screen; only redraw when that changes (or during a fade).
        paused = bool(code is None and pl is not None and pl.get("paused"))
        if paused:
            items = None
            self.current_id = None  # so resuming fades the slideshow back in
        elif code is None and pl is not None:
            sig = signature(pl)
            if sig != self.last_sig:
                self.last_sig = sig
                self.seq_index, self.next_change = -1, 0
            items = self.visible_items(pl, now)
            if items:
                self.pick(items, pl, now, now_local)
        else:
            items = None
        fading = self.previous is not None and (now_local - self.fade_start) < FADE_SEC + 0.15
        self.busy = fading or self.loader is not None
        # Only redraw when something changed or mid-fade, plus a refresh every 2 s in case the
        # text console scribbled on the screen.
        key = (code, pl is None, msg, status, self.current_id, bool(items), paused, pl.get("pause_message") if pl else None)
        if not fading and key == self.last_key and now_local - self.last_push < 2:
            return
        self.last_key = key

        if code:
            self.current_id = None
            self.current = self.previous = None
            spaced = code[:3] + " " + code[3:]
            self.text_center([
                (self.font_mid, "Pair this screen"),
                (self.font_small, " "),
                (self.font_big, spaced),
                (self.font_small, " "),
                (self.font_small, "In the Signage admin page, go to"),
                (self.font_small, "Screens > Add a screen and enter this code."),
            ])
        elif pl is None:
            self.text_center([(self.font_mid, msg)])
        elif paused:
            self.current = self.previous = None
            self.text_center([(self.font_mid, pl["pause_message"])] if pl["pause_message"] else [])
        elif not items:
            self.current_id = None
            self.current = self.previous = None
            self.text_center([(self.font_mid, f"No images on “{pl['name']}” yet" if not pl["items"] else "Downloading images…")])
        else:
            self.draw_image(now_local)

        if status:
            s = self.font_status.render(status, True, (200, 200, 200))
            self.screen.blit(s, (self.size[0] - s.get_width() - 20, self.size[1] - s.get_height() - 12))
        self.fb.show(self.screen)
        self.last_push = now_local

    def pick(self, items, pl, now, now_local):
        interval = pl["interval"]
        if pl["sync"]:
            slot = int(now // interval)
            target = items[slot % len(items)]
        else:
            if now_local >= self.next_change or self.seq_index < 0:
                self.seq_index = (self.seq_index + 1) % len(items)
                self.next_change = now_local + interval
            target = items[min(self.seq_index, len(items) - 1)]

        if target["id"] != self.current_id and (self.loader is None or self.loader[0] != target["id"]):
            self.start_load(target["id"])

        if self.loader and not self.loader[1].is_alive():
            image_id, _, holder = self.loader
            self.loader = None
            if "surface" in holder:
                self.previous = self.current
                self.current = holder["surface"]
                self.current_id = image_id
                self.fade_start = now_local
            else:
                log.warning("could not decode %s (%s); will re-download", image_id, holder.get("error"))
                try:
                    os.remove(image_path(image_id))
                except OSError:
                    pass

    def draw_image(self, now_local):
        if self.current is None:
            self.screen.fill((0, 0, 0))
            return
        t = (now_local - self.fade_start) / FADE_SEC
        if self.previous is not None and t < 1:
            self.screen.blit(self.previous, (0, 0))
            self.current.set_alpha(int(255 * max(0.0, t)))
            self.screen.blit(self.current, (0, 0))
            self.current.set_alpha(None)
        else:
            self.previous = None
            self.screen.blit(self.current, (0, 0))


def main():
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s", stream=sys.stdout)
    if not SERVER.startswith("https://"):
        print("SIGNAGE_SERVER_URL must be set to the https device API address", file=sys.stderr)
        sys.exit(2)
    os.makedirs(IMAGE_DIR, exist_ok=True)

    # Offline-first: start from the cached playlist.
    cached = read_file(MANIFEST_FILE)
    if cached:
        try:
            S.playlist = parse_playlist(json.loads(cached))
        except Exception:
            pass

    disp = Display()
    log.info("signage player %s on %dx%d, server %s", VERSION, disp.size[0], disp.size[1], SERVER)
    Syncer(disp.size).start()

    while True:
        started = time.time()
        disp.frame()
        period = 1.0 / (FPS if disp.busy else 4)
        time.sleep(max(0.0, period - (time.time() - started)))


if __name__ == "__main__":
    main()
