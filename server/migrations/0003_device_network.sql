-- How each screen is connected, as last reported by the player (JSON):
--   {"type":"wifi"|"ethernet"|"other", "ssid":"...", "signal":0-100, "ip":"..."}
ALTER TABLE devices ADD COLUMN network TEXT;
