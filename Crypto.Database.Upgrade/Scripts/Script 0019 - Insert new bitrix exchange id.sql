--unused
INSERT INTO exchange (id, name) VALUES (3, 'Bitmex')  
ON CONFLICT (id) DO NOTHING;