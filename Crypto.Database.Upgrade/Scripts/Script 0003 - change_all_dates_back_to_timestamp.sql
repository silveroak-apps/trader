DROP VIEW webview;
ALTER TABLE public.buy_analysis ALTER COLUMN time TYPE TIMESTAMP USING time::TIMESTAMP;
ALTER TABLE public.coin_24hr_market ALTER COLUMN insert_date TYPE TIMESTAMP USING insert_date::TIMESTAMP;
ALTER TABLE public.data24hr ALTER COLUMN insert_date TYPE TIMESTAMP USING insert_date::TIMESTAMP;
ALTER TABLE public.exchange_order ALTER COLUMN created_time TYPE TIMESTAMP USING created_time::TIMESTAMP;
ALTER TABLE public.exchange_order ALTER COLUMN updated_time TYPE TIMESTAMP USING updated_time::TIMESTAMP;
ALTER TABLE public.positive_signal ALTER COLUMN dca_change_date TYPE TIMESTAMP USING dca_change_date::TIMESTAMP;
ALTER TABLE public.positive_signal ALTER COLUMN loss_decision_date TYPE TIMESTAMP USING loss_decision_date::TIMESTAMP;
ALTER TABLE public.positive_signal ALTER COLUMN buy_date_time TYPE TIMESTAMP USING buy_date_time::TIMESTAMP;
ALTER TABLE public.positive_signal ALTER COLUMN sell_date_time TYPE TIMESTAMP USING sell_date_time::TIMESTAMP;
ALTER TABLE public.positive_signal ALTER COLUMN buy_signal_date_time TYPE TIMESTAMP USING buy_signal_date_time::TIMESTAMP;
ALTER TABLE public.positive_signal ALTER COLUMN sell_signal_date_time TYPE TIMESTAMP USING sell_signal_date_time::TIMESTAMP;
ALTER TABLE public.sell_analysis ALTER COLUMN updated_time TYPE TIMESTAMP USING updated_time::TIMESTAMP;
ALTER TABLE public.sell_analysis2 ALTER COLUMN updated_time TYPE TIMESTAMP USING updated_time::TIMESTAMP;
ALTER TABLE public.sell_analysis2 ALTER COLUMN updated_time TYPE TIMESTAMP USING updated_time::TIMESTAMP;
ALTER TABLE public.sell_analysis2 ALTER COLUMN max_bid_time TYPE TIMESTAMP USING max_bid_time::TIMESTAMP;
ALTER TABLE public.sell_analysis2 ALTER COLUMN min_bid_time TYPE TIMESTAMP USING min_bid_time::TIMESTAMP;
ALTER TABLE public.sell_analysis2 ALTER COLUMN current_band_entry_time TYPE TIMESTAMP USING current_band_entry_time::TIMESTAMP;
ALTER TABLE public.sell_analysis2 ALTER COLUMN decision_sell_time TYPE TIMESTAMP USING decision_sell_time::TIMESTAMP;
ALTER TABLE public.sell_analysis_bids ALTER COLUMN bid_time TYPE TIMESTAMP USING bid_time::TIMESTAMP;
ALTER TABLE public.sell_analysis_bids ALTER COLUMN inserted_time TYPE TIMESTAMP USING inserted_time::TIMESTAMP;
ALTER TABLE public.ticker ALTER COLUMN insert_date TYPE TIMESTAMP USING insert_date::TIMESTAMP;

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
