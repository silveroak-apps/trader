ALTER TABLE public.exchange_order ADD CONSTRAINT exchange_order_uq UNIQUE (exchange_order_id,signal_id);
