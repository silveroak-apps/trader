CREATE TABLE public.kline_coin_analysis
(
    id BIGINT PRIMARY KEY NOT NULL,
    symbol VARCHAR(256) NOT NULL,
    exchange_id BIGINT NOT NULL,
    period VARCHAR(16) NOT NULL,
    market varchar (16) NOT NULL,
    last_analysed TIMESTAMP WITH TIME ZONE NOT NULL,
    open_time_as_date TIMESTAMP WITH TIME ZONE NOT NULL,
    opentime BIGINT NOT NULL
    
);

CREATE TABLE public.kline_stats
(
    kline_coin_stats_id BIGINT NOT NULL,
    stat_type VARCHAR(64) NOT NULL,
    stat_value DOUBLE PRECISION,
    stat_values VARCHAR(256),
    opentime BIGINT NOT NULL,
    last_analysed TIMESTAMP WITH TIME ZONE NOT NULL
);

ALTER TABLE public.kline_stats
 ADD CONSTRAINT kline_stats__unique UNIQUE (kline_coin_stats_id, stat_type);

DROP TABLE kline_data;
DROP TABLE kline_data_stats;

create table kline_data
(
  symbol varchar(25) not null,
  opentime bigint not null,
  open numeric(18,10),
  high numeric(18,10),
  low numeric(18,10),
  close numeric(18,10),
  volume bigint,
  closetime bigint,
  quoteassetvolume bigint,
  numberoftrades bigint,
  takerbuybaseassetvolume bigint,
  takerbuyquoteassetvolume bigint,
  period varchar(25) not null,
  exchange_id bigint not null,
  id bigint not null
    constraint kline_data_id_pk
    primary key,
  vwap double precision,
  constraint kline_data_symbol_opentime_period_exchange_id_pk
  unique (symbol, opentime, period, exchange_id)
)
;

create unique index kline_data_id_uindex
  on kline_data (id)
;

create index kline_data__index_opentime
  on kline_data (opentime)
;

create index kline_data__index_closetime
  on kline_data (closetime)
;


