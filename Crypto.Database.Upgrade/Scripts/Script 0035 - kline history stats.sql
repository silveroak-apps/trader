CREATE TABLE public.kline_coin_analysis_history
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

CREATE TABLE public.kline_stats_history
(
    kline_coin_stats_id BIGINT NOT NULL,
    stat_type VARCHAR(64) NOT NULL,
    stat_value DOUBLE PRECISION,
    stat_values VARCHAR(256),
    opentime BIGINT NOT NULL,
    last_analysed TIMESTAMP WITH TIME ZONE NOT NULL
);

ALTER TABLE public.kline_stats_history
ADD CONSTRAINT kline_stats_history__unique UNIQUE (kline_coin_stats_id, stat_type, opentime);

