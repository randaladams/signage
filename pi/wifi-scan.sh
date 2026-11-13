#!/bin/bash
# Turns Wi-Fi on and lists the networks this Pi can see.   sudo bash wifi-scan.sh [COUNTRY]
# (A Pi 3 only sees 2.4 GHz networks.)
set -e
COUNTRY="${1:-US}"
raspi-config nonint do_wifi_country "$COUNTRY"   # also lifts the radio block
rfkill unblock wifi
nmcli radio wifi on
echo "Scanning for Wi-Fi networks..."
sleep 4
nmcli device wifi rescan 2>/dev/null || true
sleep 4
nmcli -f SSID,SIGNAL,SECURITY,CHAN,FREQ device wifi list
