ALTER TABLE public.coin_stats
 ADD CONSTRAINT coin_stats_symbol_exchange_id_pk UNIQUE (symbol, exchange_id);