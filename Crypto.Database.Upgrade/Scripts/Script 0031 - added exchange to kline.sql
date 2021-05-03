DELETE FROM kline_data;
ALTER TABLE public.kline_data ADD period VARCHAR(25) NOT NULL;
ALTER TABLE public.kline_data ADD exchange_id BIGINT NOT NULL;
ALTER TABLE public.kline_data ADD id BIGINT NOT NULL;
CREATE UNIQUE INDEX kline_data_id_uindex ON public.kline_data (id);
ALTER TABLE public.kline_data DROP CONSTRAINT kline_data_pk;
ALTER TABLE public.kline_data ADD CONSTRAINT kline_data_id_pk PRIMARY KEY (id);
ALTER TABLE public.kline_data ALTER COLUMN open TYPE NUMERIC(18,10) USING open::NUMERIC(18,10);
ALTER TABLE public.kline_data ALTER COLUMN high TYPE NUMERIC(18,10) USING high::NUMERIC(18,10);
ALTER TABLE public.kline_data ALTER COLUMN low TYPE NUMERIC(18,10) USING low::NUMERIC(18,10);
ALTER TABLE public.kline_data ALTER COLUMN close TYPE NUMERIC(18,10) USING close::NUMERIC(18,10);
ALTER TABLE public.kline_data ALTER COLUMN volume TYPE NUMERIC(18,10) USING volume::NUMERIC(18,10);
ALTER TABLE public.kline_data ALTER COLUMN quoteassetvolume TYPE NUMERIC(18,10) USING quoteassetvolume::NUMERIC(18,10);
ALTER TABLE public.kline_data ALTER COLUMN takerbuybaseassetvolume TYPE NUMERIC(18,10) USING takerbuybaseassetvolume::NUMERIC(18,10);
ALTER TABLE public.kline_data ALTER COLUMN takerbuyquoteassetvolume TYPE NUMERIC(18,10) USING takerbuyquoteassetvolume::NUMERIC(18,10);
ALTER TABLE public.kline_data
 ADD CONSTRAINT kline_data_symbol_opentime_period_exchange_id_pk UNIQUE (symbol, opentime, period, exchange_id);
ALTER TABLE public.kline_data ADD vwap NUMERIC(18,10) NULL;