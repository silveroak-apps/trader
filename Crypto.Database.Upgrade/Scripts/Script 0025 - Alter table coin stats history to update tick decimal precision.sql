ALTER TABLE public.coin_stats_history
    ALTER COLUMN tick_low_24hr TYPE numeric(18,10) USING tick_low_24hr::numeric,
    ALTER COLUMN tick_high_24hr TYPE numeric(18,10) USING tick_high_24hr::numeric;
