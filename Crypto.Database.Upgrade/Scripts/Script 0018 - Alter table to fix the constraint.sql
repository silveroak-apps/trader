ALTER TABLE public.coin_stats DROP CONSTRAINT coin_stats_symbol_exchange_id_pk;
ALTER TABLE public.coin_stats
 ADD CONSTRAINT coin_stats_symbol_exchange_id_tic_type_pk UNIQUE (symbol, exchange_id, tic_type);
