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
usermod -aG video,render "$RUN_USER"   # video: /dev/fb0 and the display; render: the GPU (for mpv)

echo "==> Installing service (runs as $RUN_USER)"
cat > /etc/systemd/system/signage-player.service <<EOF
[Unit]
Description=Signage player
Wants=network-online.target
After=network-online.target systemd-user-sessions.service
# Take over the screen from the text console on tty1.
Conflicts=getty@tty1.service
After=getty@tty1.service

[Service]
User=$RUN_USER
SupplementaryGroups=video render
Environment=SIGNAGE_SERVER_URL=$SERVER_URL
Environment=SIGNAGE_STATE_DIR=/var/lib/signage-player
# mpv and the GPU driver keep their config/shader cache here (home is read-only for the service).
Environment=XDG_CONFIG_HOME=/var/lib/signage-player/config
Environment=XDG_CACHE_HOME=/var/lib/signage-player/cache
StateDirectory=signage-player
StateDirectoryMode=0700
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

echo
echo "Done. The screen should show a pairing code within a few seconds."
echo "Logs:    journalctl -u signage-player -f"
echo "Status:  systemctl status signage-player"
