-- Dropping views because they depend on some of these columns;
DROP VIEW IF EXISTS public.overview_sell_analysis;
DROP VIEW IF EXISTS public.summary;
DROP VIEW IF EXISTS public.overview;

ALTER TABLE public.sell_analysis2
    DROP COLUMN break_even,
    DROP COLUMN current_band_entry_time,
    DROP COLUMN total_positive_gain_hits,
    DROP COLUMN total_positive_gain_hits_1_5,
    DROP COLUMN total_negative_gain_hits,
    DROP COLUMN total_negative_jumpouts;


ALTER TABLE public.sell_analysis2
    RENAME COLUMN total_min_breach TO total_price_movement_percent;
