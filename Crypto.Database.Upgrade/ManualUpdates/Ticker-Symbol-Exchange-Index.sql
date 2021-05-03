CREATE INDEX ticker_symbol_exchange_insert_date_index
ON public.ticker (symbol,insert_date,exchange_id);