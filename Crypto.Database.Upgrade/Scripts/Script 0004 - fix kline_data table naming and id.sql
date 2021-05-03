ALTER TABLE public.kline_data DROP COLUMN IF EXISTS id;

ALTER TABLE public.kline_data RENAME COLUMN numeroftrades TO numberoftrades;
