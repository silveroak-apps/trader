CREATE INDEX coin_stats_history_symbol_tic_time_exchg_idx
ON public.coin_stats_history (symbol,tic_type,"time",exchange_id) ;
