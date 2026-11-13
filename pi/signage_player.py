#!/usr/bin/env python3
"""Signage player for Raspberry Pi (Raspberry Pi OS Lite, no desktop needed).

Talks to the same device API as the Fire TV app:
  - shows a pairing code until the screen is added in the admin page
  - downloads images to local storage and plays from there (works offline)
  - optional lock-step mode: every screen on a playlist shows the same image at the same time
  - follows "Refresh now" / "Restart app" commands from the admin page
  - plays short videos by handing the screen to mpv (GPU output) for their duration

Runs as a systemd service (see install.sh). Needs python3-pygame and mpv from apt.
"""

import json
import logging
import os
import platform
import re
import shutil
import socket
import struct
import subprocess
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

VERSION = "pi-1.5.0"
MPV = shutil.which("mpv")
SERVER = os.environ.get("SIGNAGE_SERVER_URL", "").rstrip("/")
# STATE_DIR (on the SD card) holds only the pairing token, written once when the screen is paired.
# CACHE_DIR holds the playlist and downloaded media; install.sh puts it in RAM (tmpfs) so normal
# running never writes to the SD card. After a reboot the media is simply downloaded again.
STATE_DIR = os.environ.get("SIGNAGE_STATE_DIR", os.path.expanduser("~/.signage-player"))
CACHE_DIR = os.environ.get("SIGNAGE_CACHE_DIR", STATE_DIR)
IMAGE_DIR = os.path.join(CACHE_DIR, "images")
TOKEN_FILE = os.path.join(STATE_DIR, "token")
MANIFEST_FILE = os.path.join(CACHE_DIR, "manifest.json")
# Manifest fields that change on every check-in; ignored when deciding whether to re-save it.
VOLATILE_KEYS = ("server_time", "today", "command")
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


def write_atomic(path, data, durable=False):
    """Write via a temp file + rename. durable=True also flushes to the SD card, so the
    file survives a power cut immediately after (used for the pairing token)."""
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(data)
        if durable:
            f.flush()
            os.fsync(f.fileno())
    os.replace(tmp, path)
    if durable:
        fd = os.open(os.path.dirname(path) or ".", os.O_RDONLY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)


def manifest_essence(m):
    """The parts of a manifest that matter for what's on screen."""
    return json.dumps({k: v for k, v in m.items() if k not in VOLATILE_KEYS}, sort_keys=True)


def media_path(item_id, kind="image"):
    return os.path.join(IMAGE_DIR, item_id + (".mp4" if kind == "video" else ".jpg"))


def image_path(image_id):
    return media_path(image_id, "image")


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


def network_info():
    """How this Pi is connected right now: the interface its internet traffic uses, its IP address,
    and for Wi-Fi the network name and signal (0-100). Shown in the admin page's Screens tab."""
    try:
        out = subprocess.run(["ip", "route", "get", "1.1.1.1"], capture_output=True, text=True, timeout=3).stdout
        dev = re.search(r"\bdev (\S+)", out)
        src = re.search(r"\bsrc (\S+)", out)
        if not dev:
            return None
        iface = dev.group(1)
        kind = "wifi" if iface.startswith("wl") else "ethernet" if iface.startswith(("eth", "en")) else "other"
        info = {"type": kind}
        if src:
            info["ip"] = src.group(1)
        if kind == "wifi":
            # --rescan no: use the last scan, so this never interrupts the Wi-Fi link
            r = subprocess.run(["nmcli", "-t", "-f", "ACTIVE,SIGNAL,SSID", "device", "wifi", "list", "ifname", iface, "--rescan", "no"],
                               capture_output=True, text=True, timeout=5)
            for line in r.stdout.splitlines():
                if line.startswith("yes:"):
                    _, signal, ssid = line.split(":", 2)
                    info["signal"] = int(signal) if signal.isdigit() else None
                    info["ssid"] = ssid.replace("\\:", ":")
                    break
        return info
    except Exception:
        return None


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
        self.power = None          # "reboot" | "shutdown" requested from the admin page
        self.message = "Starting…"
        # for the diagnostics page
        self.started = time.time()
        self.last_ok = None        # time of the last successful check-in
        self.last_error = None     # (time, message) of the last failed one
        self.next_sync = None      # when the next check-in is due
        self.screen_name = ""
        self.diag = False          # diagnostics page open (keyboard key or admin page button)
        self.diag_opened = 0.0
        self.diag_refresh = False


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
            {
                "id": i["id"],
                "start": i.get("start_date"),
                "end": i.get("end_date"),
                "kind": "video" if i.get("kind") == "video" else "image",
                "dur": int(i.get("duration_ms") or 0),
            }
            for i in m.get("images", [])
            if i.get("kind") != "video" or MPV  # skip videos if mpv isn't installed
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
            # pause: we keep images cached and blank the screen ourselves when paused
            # video: we can play MP4 clips (only if mpv is installed)
            "X-Capabilities": "pause,video" if MPV else "pause",
        }
        self.wake = threading.Event()
        cached = read_file(MANIFEST_FILE)
        try:
            self.saved_essence = manifest_essence(json.loads(cached)) if cached else None
        except Exception:
            self.saved_essence = None

    def run(self):
        while True:
            try:
                wait = self.sync_once()
            except Exception as e:  # network down, DNS, server error…
                log.warning("sync failed: %s", e)
                with S.lock:
                    S.status = "Offline - showing saved images" if S.playlist else "Can't reach server - retrying"
                    S.last_error = (time.time(), str(getattr(e, "reason", e))[:160])
                wait = 30
            with S.lock:
                S.next_sync = time.time() + wait
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
            write_atomic(TOKEN_FILE, j["token"], durable=True)  # the only routine SD-card write
            os.chmod(TOKEN_FILE, 0o600)
            with S.lock:
                S.pair_code = j["code"]
                S.playlist = None
            log.info("registered, pairing code %s", j["code"])
            return 5

        headers = dict(self.headers)
        net = network_info()
        if net:
            # header values must be plain ASCII (an SSID can contain anything)
            headers["X-Network"] = json.dumps(net, ensure_ascii=True)
        code, body = http("GET", "/v1/manifest", token=token, headers=headers)
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
                S.last_error = (time.time(), f"server answered HTTP {code}")
            return 60

        m = json.loads(body)
        poll = max(5, min(3600, int(m.get("poll_sec") or 120)))
        with S.lock:
            if m.get("server_time"):
                S.clock_offset = m["server_time"] / 1000.0 - time.time()
            S.last_ok = time.time()
            S.screen_name = m.get("name") or S.screen_name

        if m.get("status") == "pending":
            with S.lock:
                S.pair_code = m.get("code")
                S.playlist = None
            return poll

        # Paired: fetch anything new before switching, so a broken image is never shown.
        keep, failed = set(), 0
        for img in m.get("images", []):
            keep.add(img["id"])
            dest = media_path(img["id"], img.get("kind", "image"))
            if not os.path.exists(dest) or os.path.getsize(dest) == 0:
                try:
                    download(img["url"], token, dest)
                except Exception as e:
                    log.warning("download %s failed: %s", img["id"], e)
                    failed += 1
        essence = manifest_essence(m)
        if essence != self.saved_essence:  # only re-save when something on screen would change
            write_atomic(MANIFEST_FILE, body.decode("utf-8"))
            self.saved_essence = essence
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
            elif cmd in ("reboot", "shutdown"):
                S.power = cmd
            elif cmd == "diag":
                S.diag, S.diag_opened = True, time.time()
            elif cmd == "restart":
                S.restart = True
        return min(poll, 30) if failed else poll


# ------------------------------------------------------------------ keyboard (opens diagnostics)

class KeyWatcher(threading.Thread):
    """Plug a USB keyboard into the Pi and press any key: the diagnostics page opens (Esc closes it).
    Works with no network at all. Reads /dev/input directly (needs the 'input' group, set up by
    install.sh) and notices keyboards plugged in while running. HDMI-CEC "keyboards" (the TV's
    own remote) are ignored so nobody opens it by accident."""

    EVENT = struct.Struct("llHHi")   # struct input_event on 64-bit Linux
    EV_KEY, KEY_ESC = 1, 1

    def __init__(self):
        super().__init__(daemon=True, name="keys")
        self.fds = {}                # path -> fd

    def keyboards(self):
        found, name, handlers = [], "", ""
        for line in (read_file("/proc/bus/input/devices") or "").splitlines() + [""]:
            if line.startswith("N: Name="):
                name = line[8:].strip().strip('"').lower()
            elif line.startswith("H: Handlers="):
                handlers = line[12:]
            elif not line.strip():
                if "kbd" in handlers.split() and not any(x in name for x in ("cec", "vc4", "hdmi")):
                    found += ["/dev/input/" + h for h in handlers.split() if h.startswith("event")]
                name = handlers = ""
        return found

    def rescan(self):
        want = set(self.keyboards())
        for path in list(self.fds):
            if path not in want:
                os.close(self.fds.pop(path))
        for path in want - set(self.fds):
            try:
                self.fds[path] = os.open(path, os.O_RDONLY | os.O_NONBLOCK)
                log.info("keyboard connected: %s", path)
            except OSError as e:
                log.warning("can't read keyboard %s (%s); re-run install.sh to allow keyboard access", path, e)

    def run(self):
        import select
        last_scan = 0.0
        while True:
            if time.time() - last_scan > 3:
                self.rescan()
                last_scan = time.time()
            if not self.fds:
                time.sleep(1)
                continue
            ready, _, _ = select.select(list(self.fds.values()), [], [], 1.0)
            for fd in ready:
                try:
                    data = os.read(fd, self.EVENT.size * 64)
                except OSError:                       # unplugged
                    last_scan = 0
                    continue
                for off in range(0, len(data) - self.EVENT.size + 1, self.EVENT.size):
                    _, _, etype, code, value = self.EVENT.unpack_from(data, off)
                    if etype == self.EV_KEY and value == 1:   # key pressed
                        with S.lock:
                            if S.diag and code == self.KEY_ESC:
                                S.diag = False                 # Esc closes
                            elif S.diag:
                                S.diag_refresh = True          # any other key re-runs the checks
                            else:
                                S.diag, S.diag_opened = True, time.time()


# ------------------------------------------------------------------ diagnostics checks

def _timed(fn, timeout):
    """Run fn() in a thread; return (result, error, seconds). Never blocks longer than timeout."""
    box = {}

    def work():
        t = time.time()
        try:
            box["r"] = fn()
        except Exception as e:
            box["e"] = str(getattr(e, "reason", e))
        box["s"] = time.time() - t

    th = threading.Thread(target=work, daemon=True)
    th.start()
    th.join(timeout)
    if th.is_alive():
        return None, f"no answer within {timeout:.0f} s", timeout
    return box.get("r"), box.get("e"), box.get("s", 0.0)


def run_diagnostics():
    """The network ladder (each step only matters if the one before passed) plus useful facts.
    Returns {"checks": [(label, ok: True/False/None, detail)], "info": [(label, value)]}."""
    checks, info = [], []
    net = network_info() or {}
    iface = None
    route = subprocess.run(["ip", "route", "show", "default"], capture_output=True, text=True, timeout=3).stdout
    gw = re.search(r"default via (\S+) dev (\S+)", route)
    if gw:
        iface = gw.group(2)

    # 1) link: cable or Wi-Fi
    eth = (read_file("/sys/class/net/eth0/carrier") or "").strip() == "1"
    wifi_up = net.get("type") == "wifi"
    if net.get("type") == "wifi":
        sig = net.get("signal")
        checks.append(("Wi-Fi connected", True, f"{net.get('ssid', '?')}, signal {sig}%" + (" (weak)" if sig is not None and sig < 40 else "")))
    elif eth:
        checks.append(("Network cable connected", True, "eth0"))
    else:
        checks.append(("Network cable or Wi-Fi", False, "no cable detected and not connected to Wi-Fi"))

    # 2) IP address
    ok = bool(net.get("ip"))
    checks.append(("IP address", ok, net.get("ip") if ok else "none (DHCP failed?)"))

    # 3) router
    if not gw:
        checks.append(("Router (default gateway)", False, "no default route"))
    else:
        g = gw.group(1)
        r = subprocess.run(["ping", "-c", "1", "-W", "2", g], capture_output=True, text=True)
        if r.returncode == 0:
            ms = re.search(r"time=([\d.]+)", r.stdout)
            checks.append(("Router reachable", True, f"{g} ({ms.group(1) if ms else '?'} ms)"))
        else:
            neigh = subprocess.run(["ip", "neigh", "show", g], capture_output=True, text=True).stdout
            seen = any(s in neigh for s in ("REACHABLE", "STALE", "DELAY"))
            checks.append(("Router reachable", True if seen else False, f"{g}" + (" (seen on network)" if seen else " not answering")))

    # 4) DNS
    host = SERVER.split("//", 1)[-1].split("/", 1)[0]
    addr, err, secs = _timed(lambda: socket.getaddrinfo(host, 443)[0][4][0], 6)
    checks.append(("Internet names (DNS)", addr is not None, f"{host} → {addr} ({secs * 1000:.0f} ms)" if addr else (err or "failed")))

    # 5) signage server (HTTPS)
    def ping_server():
        req = urllib.request.Request(SERVER + "/", headers={"User-Agent": "signage-player/" + VERSION})
        with urllib.request.urlopen(req, timeout=8) as res:
            return res.status, res.headers.get("Date")
    res, err, secs = _timed(ping_server, 10) if addr else (None, "skipped (DNS failed)", 0)
    checks.append(("Signage server", bool(res and res[0] == 200), f"HTTPS OK ({secs * 1000:.0f} ms)" if res else err))

    # 6) this screen's pairing key
    token = (read_file(TOKEN_FILE) or "").strip()
    if not token:
        checks.append(("Pairing key", False, "none: this screen is not paired"))
    elif res:
        r, err, _ = _timed(lambda: http("GET", "/v1/manifest", token=token), 10)
        code, body = r if r else (None, b"")
        if code == 200:
            st = json.loads(body).get("status")
            checks.append(("Pairing key", st == "paired", "accepted (paired)" if st == "paired" else "waiting to be paired in the admin page"))
        elif code == 401:
            checks.append(("Pairing key", False, "rejected: screen was removed; it will show a new pairing code"))
        else:
            checks.append(("Pairing key", None, err or f"HTTP {code}"))
    else:
        checks.append(("Pairing key", None, "can't check without the server"))

    # facts
    with S.lock:
        pl, name, last_ok, last_err, nxt, offset = S.playlist, S.screen_name, S.last_ok, S.last_error, S.next_sync, S.clock_offset
    fmt = lambda t: datetime.fromtimestamp(t).strftime("%a %H:%M:%S") if t else "never"
    info.append(("Screen", f"{name or '(not paired)'} · playlist {pl['name'] if pl else '-'}"))
    info.append(("Player", f"{VERSION} · {device_info(Display_size())}"))
    info.append(("Network", ", ".join(f"{k} {v}" for k, v in net.items()) or "none"))
    info.append(("Last check-in OK", fmt(last_ok) + (f"  (next {fmt(nxt)})" if nxt else "")))
    if last_err:
        info.append(("Last error", f"{fmt(last_err[0])}: {last_err[1]}"))
    if pl:
        have = sum(os.path.exists(media_path(i["id"], i["kind"])) for i in pl["items"])
        info.append(("Content", f"{have} of {len(pl['items'])} items saved on this screen" + (" · PAUSED" if pl.get("paused") else "")))
    try:
        du = shutil.disk_usage(CACHE_DIR)
        info.append(("Media storage (RAM)", f"{(du.used) / 1e6:.0f} MB used of {du.total / 1e6:.0f} MB"))
    except OSError:
        pass
    mem = dict(re.findall(r"(\w+):\s+(\d+)", read_file("/proc/meminfo") or ""))
    if mem:
        info.append(("Memory", f"{int(mem.get('MemAvailable', 0)) // 1024} MB free of {int(mem.get('MemTotal', 0)) // 1024} MB"))
    temp = read_file("/sys/class/thermal/thermal_zone0/temp")
    thr, _, _ = _timed(lambda: subprocess.run(["vcgencmd", "get_throttled"], capture_output=True, text=True).stdout, 3)
    health = []
    if temp:
        health.append(f"{int(temp) / 1000:.0f} °C")
    if thr and "=" in thr:
        bits = int(thr.strip().split("=")[1], 16)
        now_flags = [w for b, w in ((0, "UNDER-VOLTAGE NOW (weak power supply)"), (3, "too hot now")) if bits & (1 << b)]
        past = [w for b, w in ((16, "under-voltage"), (19, "overheating")) if bits & (1 << b)]
        health += now_flags or ["power OK"]
        if past:
            health.append("since boot: " + ", ".join(past))
    up = float((read_file("/proc/uptime") or "0").split()[0])
    health.append(f"up {int(up // 86400)}d {int(up % 86400 // 3600)}h {int(up % 3600 // 60)}m")
    info.append(("Pi health", " · ".join(health)))
    ntp, _, _ = _timed(lambda: subprocess.run(["timedatectl", "show", "-p", "NTPSynchronized", "--value"], capture_output=True, text=True).stdout.strip(), 3)
    clock = f"{datetime.now().strftime('%Y-%m-%d %H:%M:%S')} · internet time sync: {'yes' if ntp == 'yes' else 'NO'}"
    if res and res[1]:
        try:
            from email.utils import parsedate_to_datetime
            diff = time.time() - parsedate_to_datetime(res[1]).timestamp()
            clock += f" · {abs(diff):.0f} s {'ahead of' if diff > 0 else 'behind'} the server"
        except Exception:
            pass
    info.append(("Clock", clock))
    info.append(("Hostname", f"{socket.gethostname()} · server {host}"))
    return {"checks": checks, "info": info, "at": time.time()}


_DISPLAY_SIZE = [1920, 1080]


def Display_size():
    return tuple(_DISPLAY_SIZE)


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
        _DISPLAY_SIZE[:] = self.size
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
        self.video = None          # running mpv process, if a clip is on screen
        self.video_key = None      # which play of which clip it is (a clip can repeat)
        self.video_deadline = 0.0  # stop mpv by this time even if the clip hasn't ended
        self.video_loop = False    # mpv is looping a single clip
        self.seq_count = 0         # sequential mode: how many items have started
        self.diag_result = None    # latest run_diagnostics() result
        self.diag_thread = None
        self.diag_drawn = None

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
            p = media_path(it["id"], it["kind"])
            if os.path.exists(p) and os.path.getsize(p) > 0:
                out.append(it)
        return out

    # -- video (mpv owns the screen while a clip plays)
    def blank(self):
        """Paint the framebuffer black, so nothing stale shows while mpv starts or between clips."""
        self.screen.fill((0, 0, 0))
        self.fb.show(self.screen)
        self.last_key = None

    def start_video(self, item, offset_sec, loop=False):
        self.stop_video()
        self.blank()
        cmd = [
            MPV, "--no-config", "--no-audio", "--really-quiet", "--no-osc", "--no-input-default-bindings",
            "--vo=gpu", "--gpu-context=drm", "--hwdec=no",  # measured smooth at 720p on a Pi 3
            media_path(item["id"], "video"),
        ]
        if loop:
            cmd.insert(1, "--loop-file=inf")  # a playlist that is just this clip: loop it seamlessly
        if offset_sec > 0.5:
            cmd.insert(1, f"--start={offset_sec:.2f}")
        self.video_loop = loop
        log.info("video %s from %.1fs%s", item["id"], offset_sec, " (looping)" if loop else "")
        try:
            self.video = subprocess.Popen(cmd, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        except OSError as e:
            log.warning("could not start mpv: %s", e)
            self.video = None

    def stop_video(self):
        if self.video is not None:
            if self.video.poll() is None:
                self.video.terminate()
                try:
                    self.video.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    self.video.kill()
            self.video = None
            self.video_loop = False
            self.last_key = None  # the framebuffer must be redrawn once mpv lets go of the screen

    def item_sec(self, it, pl):
        if it["kind"] == "video":
            return max(1.0, it["dur"] / 1000.0)
        return float(pl["interval"])

    def text_center(self, lines):
        self.screen.fill((0, 0, 0))
        total = sum(f.get_linesize() for f, _ in lines)
        y = (self.size[1] - total) // 2
        for font, text in lines:
            surf = font.render(text, True, (255, 255, 255))
            self.screen.blit(surf, ((self.size[0] - surf.get_width()) // 2, y))
            y += font.get_linesize()

    # -- diagnostics page
    DIAG_AUTOCLOSE = 5 * 60
    DIAG_REFRESH = 15

    def diag_frame(self, now_local):
        """Full-screen diagnostics: the network ladder, then facts. Checks re-run every 15 s
        (or when a key is pressed) in the background; closes itself after 5 minutes."""
        with S.lock:
            opened, refresh = S.diag_opened, S.diag_refresh
            S.diag_refresh = False
            if now_local - opened > self.DIAG_AUTOCLOSE:
                S.diag = False
                return False
        if self.video is not None:
            self.stop_video()
        # when the page closes, start the slideshow afresh (incl. restarting a clip we interrupted)
        self.video_key = None
        self.current_id = None
        self.current = self.previous = None
        running = self.diag_thread is not None and self.diag_thread.is_alive()
        stale = self.diag_result is None or now_local - self.diag_result["at"] > self.DIAG_REFRESH
        if not running and (stale or refresh):
            def work():
                try:
                    self.diag_result = run_diagnostics()
                except Exception as e:
                    log.warning("diagnostics failed: %s", e)
                    self.diag_result = {"checks": [("Diagnostics", False, str(e)[:120])], "info": [], "at": time.time()}
            self.diag_thread = threading.Thread(target=work, daemon=True)
            self.diag_thread.start()
            running = True
        key = (id(self.diag_result), running, int((self.DIAG_AUTOCLOSE - (now_local - opened)) // 60))
        if key == self.diag_drawn and now_local - self.last_push < 2:
            return True
        self.diag_drawn = key

        W, H = self.size
        scr = self.screen
        scr.fill((12, 16, 24))
        pad, y = int(W * 0.04), int(H * 0.04)
        f_title = self.font_mid
        f_row = pygame.font.Font(None, max(20, int(H * 0.036)))
        f_small = pygame.font.Font(None, max(16, int(H * 0.028)))
        white, grey, green, red, amber = (240, 240, 240), (150, 160, 175), (74, 222, 128), (248, 113, 113), (251, 191, 36)

        def text(s, font, color, x, yy, max_w=None):
            s = str(s)
            if max_w:
                while s and font.size(s)[0] > max_w:
                    s = s[:-2] + "…" if len(s) > 2 else ""
            scr.blit(font.render(s, True, color), (x, yy))

        with S.lock:
            name = S.screen_name
        text(f"Diagnostics{' - ' + name if name else ''}", f_title, white, pad, y)
        y += f_title.get_linesize()
        mins = max(1, int((self.DIAG_AUTOCLOSE - (now_local - opened)) // 60) + 1)
        text(f"Esc closes · any other key re-checks · closes by itself in {mins} min" + ("  ·  checking…" if running else ""),
             f_small, grey, pad, y)
        y += int(f_small.get_linesize() * 1.6)

        res = self.diag_result
        if res is None:
            text("Running checks…", f_row, white, pad, y)
        else:
            text("Connection checks (top to bottom: the first red line is where it breaks)", f_small, grey, pad, y)
            y += f_small.get_linesize()
            r = int(f_row.get_height() * 0.28)
            for label, ok, detail in res["checks"]:
                color = green if ok else red if ok is False else amber
                pygame.draw.circle(scr, color, (pad + r, y + f_row.get_height() // 2), r)
                text(label, f_row, white, pad + 3 * r, y, int(W * 0.30))
                text(detail or "", f_row, color if ok is False else grey, pad + int(W * 0.33), y, W - pad - int(W * 0.33) - pad)
                y += int(f_row.get_linesize() * 1.15)
            y += int(f_row.get_linesize() * 0.5)
            pygame.draw.line(scr, (50, 58, 70), (pad, y), (W - pad, y), 2)
            y += int(f_row.get_linesize() * 0.5)
            for label, value in res["info"]:
                if y > H - f_small.get_linesize() * 2:
                    break
                text(label, f_small, grey, pad, y, int(W * 0.22))
                text(value, f_small, white, pad + int(W * 0.24), y, W - 2 * pad - int(W * 0.24))
                y += int(f_small.get_linesize() * 1.25)
            text(f"checked {datetime.fromtimestamp(res['at']).strftime('%H:%M:%S')}", f_small, grey, pad, H - f_small.get_linesize() - pad // 2)
        self.fb.show(scr)
        self.last_push = now_local
        self.last_key = None   # force the slideshow to redraw when the page closes
        return True

    def power_off(self, what):
        """Restart or shut down the whole Pi (requested from the admin page).
        install.sh adds a polkit rule that allows exactly these two actions for the player."""
        self.stop_video()
        lines = [(self.font_mid, "Restarting…")] if what == "reboot" else [
            (self.font_mid, "Shutting down…"),
            (self.font_small, " "),
            (self.font_small, "To turn this screen back on, unplug its power and plug it back in."),
        ]
        self.text_center(lines)
        self.fb.show(self.screen)
        log.info("%s requested from the admin page", what)
        try:
            subprocess.run(["systemctl", "reboot" if what == "reboot" else "poweroff"], check=True, timeout=30,
                           stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
            time.sleep(60)  # the system is going down; just wait
        except Exception as e:
            log.warning("%s failed: %s", what, e)
            with S.lock:
                S.status = f"{'Restart' if what == 'reboot' else 'Shut down'} not permitted - re-run install.sh"
            self.last_key = None

    # -- per frame
    def frame(self):
        now_local = time.time()
        with S.lock:
            pl, code, status, offset = S.playlist, S.pair_code, S.status, S.clock_offset
            msg = S.message
            if S.reload:
                S.reload = False
                self.current_id, self.seq_index, self.next_change = None, -1, 0
            power, S.power = S.power, None
            diag = S.diag
        if power:
            self.power_off(power)   # outside the lock: on failure it takes the lock to set a status
        if diag and self.diag_frame(now_local):
            self.busy = False
            return
        with S.lock:
            if S.restart:
                log.info("restart requested")
                self.stop_video()
                pygame.quit()
                sys.exit(0)  # systemd restarts us
        now = now_local + offset

        # Work out what should be on screen; only redraw when that changes (or during a fade).
        paused = bool(code is None and pl is not None and pl.get("paused"))
        target = None
        if paused:
            items = None
            self.current_id = None  # so resuming fades the slideshow back in
        elif code is None and pl is not None:
            sig = signature(pl)
            if sig != self.last_sig:
                self.last_sig = sig
                self.seq_index, self.next_change = -1, 0
                self.stop_video()
            items = self.visible_items(pl, now)
            if items:
                target, play_key, offset_sec = self.schedule(items, pl, now, now_local)
        else:
            items = None

        # Videos: mpv draws directly to the screen; we just start/stop it and stay out of the way.
        # mpv needs ~2 s to start on a Pi 3, so clips are allowed to finish rather than being cut
        # off when their scheduled slot ends.
        if target is not None and target["kind"] == "video":
            single = len(items) == 1
            if single:
                # Only this clip to show: one looping mpv instead of a ~2 s restart every time.
                play_key = ("loop", target["id"])
                if self.video is not None and self.video.poll() is not None:
                    self.video_key = None  # mpv died: start it again
            if self.video_key != play_key:
                self.video_key = play_key
                self.current_id = target["id"]
                self.current = self.previous = None  # next photo fades in from black
                self.start_video(target, offset_sec, loop=single)
                remaining = self.item_sec(target, pl) - offset_sec
                self.video_deadline = now_local + remaining + 4.0
                if not pl["sync"]:
                    self.next_change = float("inf") if single else self.video_deadline  # normally move on when mpv exits
            elif not single and self.video is not None and self.video.poll() is not None and not pl["sync"]:
                self.next_change = now_local  # clip finished: move on now
            self.busy = False
            return
        if self.video is not None:
            # (Pause, pairing, an empty playlist or a looping clip being replaced stop it immediately.)
            if target is not None and not self.video_loop and self.video.poll() is None and now_local < self.video_deadline:
                self.busy = False
                return  # let the clip finish (in-step mode: at most a few seconds into the next slot)
            self.stop_video()
            self.video_key = None
        if target is not None:
            self.load_image(target, now_local)
        fading = self.previous is not None and (now_local - self.fade_start) < FADE_SEC + 0.15
        self.busy = fading or self.loader is not None
        # Only redraw when something changed or mid-fade, plus a refresh every 2 s in case the
        # text console scribbled on the screen.
        key = (code, pl is None, msg, status, self.current_id, bool(items), paused, pl.get("pause_message") if pl else None)
        if not fading and key == self.last_key and now_local - self.last_push < 2:
            return
        self.last_key = key

        if code:
            self.stop_video()
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
            self.stop_video()
            self.text_center([(self.font_mid, msg)])
        elif paused:
            self.stop_video()
            self.current = self.previous = None
            self.text_center([(self.font_mid, pl["pause_message"])] if pl["pause_message"] else [])
        elif not items:
            self.current_id = None
            self.current = self.previous = None
            self.text_center([(self.font_mid, f"No photos or videos on “{pl['name']}” yet" if not pl["items"] else "Downloading…")])
        else:
            self.draw_image(now_local)

        if status:
            s = self.font_status.render(status, True, (200, 200, 200))
            self.screen.blit(s, (self.size[0] - s.get_width() - 20, self.size[1] - s.get_height() - 12))
        self.fb.show(self.screen)
        self.last_push = now_local

    def schedule(self, items, pl, now, now_local):
        """Returns (item to show, a key identifying this play of it, seconds into the item).

        In-step mode: every screen computes the same position from the server-corrected clock,
        cycling through the playlist where photos last `interval` seconds and videos their length.
        Otherwise items simply play one after another from wherever this screen is.
        """
        if pl["sync"]:
            durs = [self.item_sec(it, pl) for it in items]
            cycle = sum(durs)
            n_cycle, pos = divmod(now, cycle)
            for i, (it, d) in enumerate(zip(items, durs)):
                if pos < d:
                    return it, (int(n_cycle), i), pos
                pos -= d
            return items[-1], (int(n_cycle), len(items) - 1), 0.0
        if now_local >= self.next_change or self.seq_index < 0:
            self.seq_index = (self.seq_index + 1) % len(items)
            self.seq_count += 1
            self.next_change = now_local + self.item_sec(items[self.seq_index], pl)
        it = items[min(self.seq_index, len(items) - 1)]
        return it, ("seq", self.seq_count), 0.0

    def load_image(self, target, now_local):
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
    if os.path.realpath(CACHE_DIR) != os.path.realpath(STATE_DIR):
        # Media used to be kept on the SD card next to the token; it now lives in RAM. Tidy up once.
        shutil.rmtree(os.path.join(STATE_DIR, "images"), ignore_errors=True)
        for name in ("manifest.json", "manifest.json.tmp"):
            try:
                os.remove(os.path.join(STATE_DIR, name))
            except OSError:
                pass

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
    KeyWatcher().start()   # plug in a keyboard + press any key = diagnostics page

    while True:
        started = time.time()
        disp.frame()
        period = 1.0 / (FPS if disp.busy else 4)
        time.sleep(max(0.0, period - (time.time() - started)))


if __name__ == "__main__":
    main()
