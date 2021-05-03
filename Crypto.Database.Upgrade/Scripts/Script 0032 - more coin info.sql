ALTER TABLE public.coin ADD is_active varchar(255) not null;
ALTER TABLE public.coin ADD exchange_id bigint default 1 not null;
