-- Playlists: a screen shows exactly one playlist; an image can be in many.
CREATE TABLE playlists (
  id           TEXT PRIMARY KEY,
  name         TEXT NOT NULL,
  interval_sec INTEGER NOT NULL DEFAULT 10,
  sync         INTEGER NOT NULL DEFAULT 1,   -- 1 = screens on this playlist flip together
  created_at   INTEGER NOT NULL
);

CREATE TABLE images (
  id          TEXT PRIMARY KEY,
  title       TEXT NOT NULL DEFAULT '',
  start_date  TEXT,                           -- YYYY-MM-DD, inclusive, NULL = immediately
  end_date    TEXT,                           -- YYYY-MM-DD, inclusive, NULL = forever
  sort_order  INTEGER NOT NULL DEFAULT 0,
  width       INTEGER,
  height      INTEGER,
  bytes       INTEGER,
  created_at  INTEGER NOT NULL,
  created_by  TEXT
);

CREATE TABLE image_playlists (
  image_id    TEXT NOT NULL,
  playlist_id TEXT NOT NULL,
  PRIMARY KEY (image_id, playlist_id)
);
CREATE INDEX idx_image_playlists_playlist ON image_playlists (playlist_id);

-- Screens. The token itself is never stored, only its SHA-256.
CREATE TABLE devices (
  id              TEXT PRIMARY KEY,
  name            TEXT NOT NULL DEFAULT '',
  token_hash      TEXT NOT NULL UNIQUE,
  pair_code       TEXT UNIQUE,                -- set while status = 'pending'
  status          TEXT NOT NULL,              -- 'pending' | 'paired'
  playlist_id     TEXT,
  created_at      INTEGER NOT NULL,
  last_seen       INTEGER,
  app_version     TEXT,
  info            TEXT,
  pending_command TEXT
);

CREATE TABLE settings (
  key   TEXT PRIMARY KEY,
  value TEXT
);

INSERT INTO playlists (id, name, interval_sec, sync, created_at)
VALUES ('default', 'All Screens', 10, 1, 0);
