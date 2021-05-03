ALTER TABLE public.exchange_order
	ALTER COLUMN order_side TYPE varchar(12) USING order_side::varchar;
