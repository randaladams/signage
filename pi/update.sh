#!/bin/bash
# Updates the installed player from ~/signage-pi/signage_player.py and restarts it.
# No password needed once install.sh has run (it adds a narrow sudo rule for exactly these commands).
set -euo pipefail
python3 -m py_compile "$HOME/signage-pi/signage_player.py"
sudo -n /usr/bin/install -m 644 "/home/$USER/signage-pi/signage_player.py" /opt/signage-player/signage_player.py
sudo -n /usr/bin/systemctl restart signage-player
echo "Player updated and restarted."
