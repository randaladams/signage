-- Short videos live in the same table as photos ("items" on screen).
ALTER TABLE images ADD COLUMN kind TEXT NOT NULL DEFAULT 'image';   -- 'image' | 'video'
ALTER TABLE images ADD COLUMN duration_ms INTEGER;                   -- videos only
ALTER TABLE images ADD COLUMN has_thumb INTEGER NOT NULL DEFAULT 0;  -- videos store a JPEG poster at thumb/<id>
