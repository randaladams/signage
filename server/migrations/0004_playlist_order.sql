-- Each playlist gets its own order (Playlists tab → Reorder). Until now every playlist used the one
-- global order (images.sort_order); start each playlist from that order so nothing on screen changes.
ALTER TABLE image_playlists ADD COLUMN position INTEGER NOT NULL DEFAULT 0;

UPDATE image_playlists SET position = (
  SELECT COUNT(*)
    FROM image_playlists o
    JOIN images oi ON oi.id = o.image_id
    JOIN images me ON me.id = image_playlists.image_id
   WHERE o.playlist_id = image_playlists.playlist_id
     AND (oi.sort_order < me.sort_order
          OR (oi.sort_order = me.sort_order AND oi.created_at < me.created_at)
          OR (oi.sort_order = me.sort_order AND oi.created_at = me.created_at AND oi.id < me.id))
);

CREATE INDEX idx_image_playlists_order ON image_playlists (playlist_id, position);
