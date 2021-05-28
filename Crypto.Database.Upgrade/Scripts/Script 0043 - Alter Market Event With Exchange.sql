ALTER TABLE public.market_event ADD exchange bigint;
ALTER TABLE public.futures_signal_command ADD market_event_id bigint;