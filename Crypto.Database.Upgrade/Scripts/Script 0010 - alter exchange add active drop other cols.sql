ALTER TABLE exchange DROP COLUMN IF EXISTS description;
ALTER TABLE exchange DROP COLUMN IF EXISTS more_info;

ALTER TABLE exchange ADD active boolean not null DEFAULT(true);

ALTER TABLE exchange ALTER COLUMN name SET NOT NULL;