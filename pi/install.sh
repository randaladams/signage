#!/bin/bash
# Installs the signage player on Raspberry Pi OS (Lite or Desktop) as a boot-time service.
#
#   Copy this folder to the Pi as ~/signage-pi, then:
#   sudo bash ~/signage-pi/install.sh [SERVER_URL]
#
# Re-running it upgrades the player in place and keeps the pairing.
set -euo pipefail

SERVER_URL="${1:-https://signage-device.adamselite-signage.workers.dev}"
RUN_USER="${SIGNAGE_USER:-${SUDO_USER:-}}"
HERE="$(cd "$(dirname "$0")" && pwd)"

if [ "$(id -u)" -ne 0 ]; then echo "Run with sudo: sudo bash $0"; exit 1; fi
if [ -z "$RUN_USER" ] || [ "$RUN_USER" = "root" ]; then echo "Run via sudo from the user that should own the player (or set SIGNAGE_USER)."; exit 1; fi
case "$SERVER_URL" in https://*) ;; *) echo "SERVER_URL must start with https://"; exit 1;; esac

echo "==> Installing packages"
apt-get update -qq
# pygame decodes/resizes photos and draws text; frames are written straight to /dev/fb0.
# mpv plays videos through the GPU (no desktop needed).
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends python3-pygame mpv >/dev/null

echo "==> Installing player"
install -d -m 755 /opt/signage-player
install -m 644 "$HERE/signage_player.py" /opt/signage-player/signage_player.py
usermod -aG video,render,input "$RUN_USER"   # video: /dev/fb0 + display; render: GPU (mpv); input: keyboard (diagnostics)

echo "==> Protecting the SD card (keep routine writes in RAM)"
# Power cuts while the SD card is being written are what corrupt it. With this, normal running writes
# almost nothing to the card: media, playlist, logs and swap all live in RAM. Only the pairing token
# (written once, at pairing) and software updates touch the card.
CACHE=/var/cache/signage-player
RUN_UID=$(id -u "$RUN_USER"); RUN_GID=$(id -g "$RUN_USER")
install -d -m 700 "$CACHE"
#  1) Downloaded photos/videos + playlist: a RAM disk (up to 40% of RAM, only uses what's filled).
#     After a reboot the media is simply downloaded again.
if ! grep -qs " $CACHE " /etc/fstab; then
  echo "tmpfs $CACHE tmpfs nodev,nosuid,noexec,size=40%,mode=0700,uid=$RUN_UID,gid=$RUN_GID 0 0" >> /etc/fstab
fi
systemctl daemon-reload
mountpoint -q "$CACHE" || mount "$CACHE"
#  2) System logs in RAM only (they're lost on reboot; the last 32 MB are kept while running).
install -d /etc/systemd/journald.conf.d
printf '[Journal]\nStorage=volatile\nRuntimeMaxUse=32M\n' > /etc/systemd/journald.conf.d/90-signage.conf
systemctl restart systemd-journald
rm -rf /var/log/journal
#  3) Swap in compressed RAM only (Raspberry Pi OS otherwise backs it with a file on the SD card).
#     Takes effect after the next reboot, when the old swap file is removed automatically.
if [ -d /etc/rpi ]; then
  install -d /etc/rpi/swap.conf.d
  printf '[Main]\nMechanism=zram\n' > /etc/rpi/swap.conf.d/90-signage.conf
fi
#  4) Daily package-list downloads and man-page indexing write to the card for no benefit here.
#     (Update by hand when needed: sudo apt update && sudo apt upgrade.)
systemctl disable --now apt-daily.timer apt-daily-upgrade.timer man-db.timer >/dev/null 2>&1 || true

echo "==> Allowing the player to restart / shut down the Pi (Screens tab buttons)"
# polkit rule: the player's user may reboot or power off the Pi, nothing else.
install -d -m 755 /etc/polkit-1/rules.d
cat > /etc/polkit-1/rules.d/50-signage-player.rules <<EOF
// Lets the signage player restart or shut down this Pi when asked from the admin page.
polkit.addRule(function (action, subject) {
  if (subject.user === "$RUN_USER" &&
      ["org.freedesktop.login1.reboot", "org.freedesktop.login1.reboot-multiple-sessions",
       "org.freedesktop.login1.power-off", "org.freedesktop.login1.power-off-multiple-sessions"]
        .indexOf(action.id) >= 0) {
    return polkit.Result.YES;
  }
});
EOF
chmod 644 /etc/polkit-1/rules.d/50-signage-player.rules
systemctl try-restart polkit.service >/dev/null 2>&1 || true

echo "==> Installing service (runs as $RUN_USER)"
cat > /etc/systemd/system/signage-player.service <<EOF
[Unit]
Description=Signage player
Wants=network-online.target
After=network-online.target systemd-user-sessions.service
# Take over the screen from the text console on tty1.
Conflicts=getty@tty1.service
After=getty@tty1.service
RequiresMountsFor=$CACHE

[Service]
User=$RUN_USER
SupplementaryGroups=video render input
Environment=SIGNAGE_SERVER_URL=$SERVER_URL
# On the SD card: only the pairing token.
Environment=SIGNAGE_STATE_DIR=/var/lib/signage-player
# In RAM: playlist + downloaded media, and mpv / GPU driver config and shader cache.
Environment=SIGNAGE_CACHE_DIR=$CACHE
Environment=XDG_CONFIG_HOME=$CACHE/config
Environment=XDG_CACHE_HOME=$CACHE/xdg-cache
StateDirectory=signage-player
StateDirectoryMode=0700
ReadWritePaths=$CACHE
ExecStart=/usr/bin/python3 /opt/signage-player/signage_player.py
Restart=always
RestartSec=5
# Harden: the player only needs the network, the screen and its own state folder.
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=read-only
PrivateTmp=yes

[Install]
WantedBy=multi-user.target
EOF

# Keep the HDMI output awake and hide the blinking console cursor (applies after next reboot).
CMDLINE=/boot/firmware/cmdline.txt
[ -f "$CMDLINE" ] || CMDLINE=/boot/cmdline.txt
if [ -f "$CMDLINE" ] && ! grep -q "consoleblank=0" "$CMDLINE"; then
  cp "$CMDLINE" "$CMDLINE.bak-signage"
  sed -i '1 s/$/ consoleblank=0 vt.global_cursor_default=0/' "$CMDLINE"
  echo "==> Updated $CMDLINE (backup saved as $CMDLINE.bak-signage)"
fi

# Let the player's own user update and restart the player without a password (nothing else).
# The service already runs as that user, so this grants no extra privilege. Used by update.sh.
SUDOERS=/etc/sudoers.d/signage-player
cat > "$SUDOERS.tmp" <<EOF
$RUN_USER ALL=(root) NOPASSWD: /usr/bin/install -m 644 /home/$RUN_USER/signage-pi/signage_player.py /opt/signage-player/signage_player.py
$RUN_USER ALL=(root) NOPASSWD: /usr/bin/systemctl restart signage-player, /usr/bin/systemctl stop signage-player, /usr/bin/systemctl start signage-player
EOF
chmod 440 "$SUDOERS.tmp"
if visudo -cf "$SUDOERS.tmp" >/dev/null; then mv "$SUDOERS.tmp" "$SUDOERS"; else rm -f "$SUDOERS.tmp"; echo "warning: sudoers rule not installed"; fi

systemctl daemon-reload
systemctl enable signage-player.service >/dev/null
systemctl restart signage-player.service

# Old layout kept media and mpv caches on the SD card; they're in RAM now.
rm -rf /var/lib/signage-player/images /var/lib/signage-player/cache /var/lib/signage-player/config \
       /var/lib/signage-player/manifest.json /var/lib/signage-player/manifest.json.tmp

echo
echo "Done. The screen should show a pairing code within a few seconds (or its slideshow, if already paired)."
echo "Reboot once (sudo reboot) to finish: hides the console cursor and switches swap to RAM only."
echo "Logs:    journalctl -u signage-player -f"
echo "Status:  systemctl status signage-player"
