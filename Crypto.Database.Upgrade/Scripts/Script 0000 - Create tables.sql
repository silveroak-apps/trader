create sequence if not exists exchange_order_id_seq
;

create sequence if not exists hibernate_sequence
;

create table if not exists kline_data
(
	symbol varchar(25) not null,
	opentime bigint not null,
	open numeric,
	high numeric,
	low numeric,
	close numeric,
	volume numeric,
	closetime bigint,
	quoteassetvolume numeric,
	numeroftrades integer,
	takerbuybaseassetvolume numeric,
	takerquoteassetvolume numeric,
	id bigserial not null
)
;

create index if not exists  kline_data__index_opentime
	on kline_data (opentime)
;

create index if not exists  kline_data__index_closetime
	on kline_data (closetime)
;

create table if not exists ticker
(
	id bigint not null
		constraint ticker_pkey
			primary key,
	insert_date timestamp with time zone,
	price double precision,
	symbol varchar(255),
	time_frame varchar(255)
)
;

create table if not exists exchange_order
(
	id bigint default nextval('exchange_order_id_seq'::regclass) not null
		constraint exchange_order_pkey
			primary key,
	status_reason text,
	symbol varchar(255) not null,
	price numeric(18,10) not null,
	exchange_order_id varchar(255) not null,
	exchange_order_id_secondary varchar(255),
	signal_id bigint not null,
	created_time timestamp with time zone not null,
	updated_time timestamp with time zone not null,
	original_qty numeric(18,10) not null,
	executed_qty numeric(18,10),
	status varchar(128) not null,
	exchange_id bigint not null,
	order_side char(4) not null
)
;

create table if not exists exchange
(
	id bigint not null
		constraint exchange_pkey
			primary key,
	code varchar(255),
	description varchar(255),
	more_info varchar(255)
)
;

create table if not exists api
(
	id bigint not null
		constraint api_pkey
			primary key,
	api_key varchar(255),
	api_secret varchar(255),
	type varchar(255),
	exchange_id bigint
		constraint fkb56th8yvpvnowlo9e726cke73
			references exchange
)
;

create table if not exists buy_analysis
(
	id bigint not null
		constraint signal_history_pkey
			primary key,
	rsi double precision,
	bolen_avg double precision,
	bolen_high double precision,
	bolen_low double precision,
	change45min double precision,
	coin_score double precision,
	curr_by_bolenger double precision,
	curr_by_lowest double precision,
	current_price double precision,
	div_dip double precision,
	first_resistance double precision,
	first_support double precision,
	last_sold double precision,
	lowest60min double precision,
	market varchar(255),
	market_cap double precision,
	moving_agv60min double precision,
	quote_volume double precision,
	second_resistance double precision,
	second_support double precision,
	signal_id bigint,
	standard_deviation double precision,
	symbol varchar(255),
	tick_high double precision,
	tick_low double precision,
	time timestamp with time zone,
	waited_average double precision,
	ema_large_list varchar(255),
	ema_med_list varchar(255),
	ema_small_list varchar(255),
	largeema double precision,
	macd_signal_list varchar(255),
	macd_signal_nine double precision,
	medema double precision,
	smallema double precision,
	tic_type varchar(255),
	moving_averageb2 double precision,
	moving_averageb3 double precision,
	moving_average30min double precision,
	tick_high_24hr numeric,
	tick_low_24hr numeric,
	moving_avg_two_hrs double precision,
	last_mins_neg_flow integer,
	price_movement_percent double precision
)
;

create table if not exists coin
(
	coin_id bigint not null
		constraint coin_pkey
			primary key,
	description varchar(255),
	more_info varchar(255),
	name varchar(255),
	symbol varchar(255)
)
;

create table if not exists coin_24hr_market
(
	id bigint not null
		constraint coin_24hr_market_pkey
			primary key,
	best_ask_price double precision,
	best_ask_price_quantity double precision,
	best_bid_price double precision,
	best_bid_quantity_price double precision,
	close_trade_quantity double precision,
	current_day_close double precision,
	event_time double precision,
	event_type varchar(255),
	high_price double precision,
	insert_date timestamp with time zone,
	low_price double precision,
	open_price double precision,
	previous_day_close double precision,
	price_change double precision,
	price_percentage_change double precision,
	symbol varchar(255),
	total_trades double precision,
	total_trage_base_asset_volume double precision,
	total_trage_quote_asset_volume double precision,
	weighted_average double precision
)
;

create table if not exists coin_stats
(
	id bigint not null
		constraint coin_stats_pkey
			primary key,
	rsi double precision,
	bolen_avg double precision,
	bolen_high double precision,
	bolen_low double precision,
	change45min double precision,
	coin_score double precision,
	curr_by_bolenger double precision,
	curr_by_lowest double precision,
	current_price double precision,
	div_dip double precision,
	ema_large_list varchar(255),
	ema_med_list varchar(255),
	ema_small_list varchar(255),
	first_resistance double precision,
	first_support double precision,
	largeema double precision,
	last_sold double precision,
	lowest60min double precision,
	macd_signal_list varchar(255),
	macd_signal_nine double precision,
	market varchar(255),
	market_cap double precision,
	medema double precision,
	moving_agv60min double precision,
	moving_average30min double precision,
	moving_averageb2 double precision,
	moving_averageb3 double precision,
	quote_volume double precision,
	reject_reason varchar(255),
	second_resistance double precision,
	second_support double precision,
	signal_analyzed varchar(255),
	smallema double precision,
	standard_deviation double precision,
	symbol varchar(255),
	tic_type varchar(255),
	tick_high double precision,
	tick_low double precision,
	time timestamp,
	waited_average double precision,
	tick_high_24hr numeric,
	tick_low_24hr numeric,
	moving_avg_two_hrs double precision,
	last_mins_neg_flow integer,
	price_movement_percent double precision
)
;

create table if not exists customer
(
	id bigint not null
		constraint customer_pkey
			primary key,
	firstname varchar(255),
	lastname varchar(255)
)
;

create table if not exists data24hr
(
	id bigint not null
		constraint data24hr_pkey
			primary key,
	ask_price double precision,
	ask_qty double precision,
	bid_price double precision,
	bid_qty double precision,
	high_price double precision,
	insert_date timestamp with time zone,
	last_price double precision,
	last_qty double precision,
	low_price double precision,
	open_price double precision,
	prev_close_price double precision,
	price_change double precision,
	price_change_percent double precision,
	quote_volume double precision,
	coin_volume double precision,
	weighted_avg_price double precision,
	coin_id bigint
		constraint fktep2mnxpsun1dgcqos54plc9u
			references coin,
	exchange_id bigint
		constraint fki7nl0kw9r1d743oftqcoss5jr
			references exchange
)
;

create table if not exists positive_signal
(
	signal_id bigint not null
		constraint positive_signal_pkey
			primary key,
	actual_profit_percentage numeric(18,10),
	actual_sell_price numeric(18,10),
	buy_price numeric(18,10),
	change_reason varchar(255),
	dca_change_date timestamp with time zone,
	loss_decision_date timestamp with time zone,
	expected_profit_percent numeric(18,10),
	sell_price numeric(18,10),
	status varchar(255),
	symbol varchar(255),
	type_of_sell varchar(255),
	signal_info_id bigint,
	strategy varchar(255),
	actual_buy_price numeric(18,10),
	sold_percentage numeric(18,10),
	buy_date_time timestamp with time zone,
	sell_date_time timestamp with time zone,
	buy_signal_date_time timestamp with time zone,
	sell_signal_date_time timestamp with time zone,
	market varchar(255)
)
;

create index if not exists  positive_signal_buy_date_time_idx
	on positive_signal (buy_date_time)
;

create table if not exists positive_signal_excel
(
	signal_id bigint not null
		constraint positive_signal_excel_pkey
			primary key,
	actual_buy_price varchar(255),
	actual_profit_percentage double precision,
	actual_sell_price double precision,
	buy_price double precision,
	change_reason varchar(255),
	current_market_price double precision,
	current_moving_average double precision,
	dca_change_date timestamp,
	loss_decision_date timestamp,
	expected_profit_percent double precision,
	sell_date timestamp,
	sell_price double precision,
	signal_date timestamp,
	signal_info_id bigint,
	sold_percentage double precision,
	status varchar(255),
	strategy varchar(255),
	symbol varchar(255),
	type_of_sell varchar(255)
)
;

create table if not exists sell_analysis
(
	signal_id bigint not null
		constraint sell_analysis_pkey
			primary key
		constraint fk_sell_analysis_signal
			references positive_signal,
	updated_time timestamp with time zone not null,
	analysis_data jsonb not null
)
;

create table if not exists kline_data_stats
(
	opentime bigint not null,
	symbol varchar(25) not null,
	movingavg30min numeric(18,10) not null,
	high_24hr_close numeric,
	low_24hr_close numeric,
	constraint kline_data_stats_symbol_opentime_pk
		primary key (symbol, opentime)
)
;

create table if not exists sell_analysis2
(
	signal_id bigint not null
		constraint sell_analysis2_pkey
			primary key
		constraint fk_sell_analysis2_signal
			references positive_signal,
	updated_time timestamp with time zone not null,
	current_gain numeric(18,10) not null,
	previous_gain numeric(18,10) not null,
	current_bid_price numeric(18,10) not null,
	max_bid_price numeric(18,10) not null,
	max_bid_time timestamp with time zone not null,
	min_bid_price numeric(18,10) not null,
	min_bid_time timestamp with time zone not null,
	moving_average30min numeric(18,10) not null,
	stop_loss_value numeric(18,10),
	price_movement integer not null,
	break_even boolean not null,
	current_band_entry_time timestamp with time zone not null,
	accumulated_duration_seconds bigint not null,
	total_positive_gain_hits integer not null,
	total_positive_gain_hits_1_5 integer not null,
	total_negative_gain_hits integer not null,
	total_negative_jumpouts integer not null,
	total_min_breach numeric(18,10) not null,
	decision_action varchar(64) not null,
	decision_sell_price numeric(18,10),
	decision_sell_reason varchar(2048),
	decision_sell_time timestamp with time zone
)
;

create table if not exists sell_analysis_gain_bands
(
	signal_id bigint not null
		constraint sell_analysis_gain_bands_sell_analysis2_fk
			references sell_analysis2
				on delete cascade,
	lower double precision not null,
	higher double precision not null,
	jump_outs integer not null,
	hits integer not null,
	constraint sell_analysis_gain_bands_pk
		primary key (signal_id, lower, higher)
)
;

create table if not exists sell_analysis_bids
(
	signal_id bigint not null
		constraint sell_analysis_bids_sell_analysis2_fk
			references sell_analysis2
				on delete cascade,
	bid_price numeric(18,10) not null,
	bid_time timestamp with time zone not null,
	constraint sell_analysis_bids_pk
		primary key (signal_id, bid_time)
)
;

create or replace view webview as
SELECT positive_signal.signal_id,
    positive_signal.symbol,
    positive_signal.buy_price,
    cs.current_price,
    positive_signal.sell_price,
    ((((positive_signal.sell_price - positive_signal.buy_price) / positive_signal.buy_price) * (100)::numeric))::numeric(36,2) AS sold_gain_percent,
    ((((cs.current_price - (positive_signal.buy_price)::double precision) / (positive_signal.buy_price)::double precision) * ((100)::numeric)::double precision))::numeric(36,2) AS current_gain_percent,
    positive_signal.status,
    positive_signal.strategy,
    to_char(timezone('Australia/Sydney'::text, positive_signal.buy_signal_date_time), 'DD-Mon-YY HH:MI:SS AM'::text) AS buy_date_time,
    to_char(timezone('Australia/Sydney'::text, positive_signal.sell_date_time), 'DD-Mon-YY HH:MI:SS AM'::text) AS sell_date_time
   FROM (positive_signal positive_signal
     LEFT JOIN LATERAL ( SELECT coin_stats.current_price
           FROM coin_stats
          WHERE (((positive_signal.symbol)::text = (coin_stats.symbol)::text) AND ((coin_stats.tic_type)::text = '15Sec'::text))
         LIMIT 1) cs ON (true))
  ORDER BY positive_signal.buy_date_time DESC;

create or replace function datediff(units character varying, start_t timestamp without time zone, end_t timestamp without time zone) returns integer
	language plpgsql
as $$
DECLARE
  diff_interval INTERVAL;
  diff INT = 0;
  years_diff INT = 0;
BEGIN
  IF units IN ('yy', 'yyyy', 'year', 'mm', 'm', 'month') THEN
    years_diff = DATE_PART('year', end_t) - DATE_PART('year', start_t);

    IF units IN ('yy', 'yyyy', 'year') THEN
      -- SQL Server does not count full years passed (only difference between year parts)
      RETURN years_diff;
    ELSE
      -- If end month is less than start month it will subtracted
      RETURN years_diff * 12 + (DATE_PART('month', end_t) - DATE_PART('month', start_t));
    END IF;
  END IF;

  -- Minus operator returns interval 'DDD days HH:MI:SS'
  diff_interval = end_t - start_t;

  diff = diff + DATE_PART('day', diff_interval);

  IF units IN ('wk', 'ww', 'week') THEN
    diff = diff/7;
    RETURN diff;
  END IF;

  IF units IN ('dd', 'd', 'day') THEN
    RETURN diff;
  END IF;

  diff = diff * 24 + DATE_PART('hour', diff_interval);

  IF units IN ('hh', 'hour') THEN
    RETURN diff;
  END IF;

  diff = diff * 60 + DATE_PART('minute', diff_interval);

  IF units IN ('mi', 'n', 'minute') THEN
    RETURN diff;
  END IF;

  diff = diff * 60 + DATE_PART('second', diff_interval);

  RETURN diff;
END;


$$
;

create or replace function getmovingavg30(symbol_param character varying, opentime_param bigint) returns numeric
	language sql
as $$
with klines25 as (
    select "close", symbol, to_timestamp(closetime/1000) from public.kline_data
    where symbol =  symbol_param and opentime < opentime_param
    order by opentime desc
    offset 5 limit 25
), klines5 as (
    select "close", symbol, to_timestamp(closetime/1000) from public.kline_data
    where symbol =  symbol_param and opentime < opentime_param
    order by opentime desc
    limit 5
), avg5 as (
    select avg("close") as avgValue5, symbol from klines5 group by symbol
), avg25 as (
    select avg("close") as avgValue25, symbol from klines25 group by symbol
)
select avgValue5 / avgValue25 as movingAvg30
from avg5
  join avg25 on avg5.symbol = avg25.symbol
$$
;
