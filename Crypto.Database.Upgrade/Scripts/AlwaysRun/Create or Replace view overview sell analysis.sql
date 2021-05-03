DROP VIEW IF EXISTS public.overview_sell_analysis;

CREATE OR REPLACE VIEW public.overview_sell_analysis
AS SELECT e.name AS exchange_name,
    positive_signal.signal_id,
    positive_signal.strategy,
    timezone('Australia/Sydney'::text, timezone('utc'::text, positive_signal.buy_signal_date_time)) AS buy_signal_date_time,
    timezone('Australia/Sydney'::text, timezone('utc'::text, positive_signal.buy_date_time)) AS buy_date_time,
    positive_signal.symbol,
    positive_signal.buy_price,
    positive_signal.actual_buy_price,
    a.current_bid_price,
    positive_signal.sell_price,
    positive_signal.actual_sell_price,
    positive_signal.status,
    timezone('Australia/Sydney'::text, timezone('utc'::text, positive_signal.sell_signal_date_time)) AS sell_signal_date_time,
    timezone('Australia/Sydney'::text, timezone('utc'::text, positive_signal.sell_date_time)) AS sell_date_time,
    a.current_gain,
    (positive_signal.sell_price - positive_signal.buy_price) / positive_signal.buy_price * 100::numeric AS signal_sold_gain_percent,
    (positive_signal.actual_sell_price - positive_signal.actual_buy_price) / positive_signal.buy_price * 100::numeric AS actual_sold_gain_percent,
    a.stop_loss_value,
    a.decision_sell_reason AS sell_reason,
    a.max_bid_price,
    a.min_bid_price,
    coin_stats.moving_average30min,
    a.price_movement,
    b.last_mins_neg_flow,
    a.accumulated_duration_seconds / 3600 AS accumulatedduration,
    a.total_price_movement_percent,
    b.price_movement_percent AS pricemovpercent,
    b.rsi,
    b.bolen_avg,
    b.change45min,
    b.curr_by_bolenger,
    b.curr_by_lowest,
    b.div_dip,
    b.first_resistance,
    b.second_resistance,
    b.moving_agv60min AS movingavg60min,
    b.first_support,
    b.second_support,
    b.standard_deviation,
    b.tick_high,
    b.tick_low,
    b.waited_average,
    b.largeema,
    b.medema,
    b.smallema,
    b.moving_averageb2 AS movingaverage30minbefore,
    b.moving_averageb3 AS movingaverage1hrbefore,
    b.moving_average30min AS movingavgfrombuyanalysis,
    b.tick_high_24hr,
    b.tick_low_24hr,
    timezone('Australia/Sydney'::text, timezone('utc'::text, a.updated_time)) AS sell_analysis_updated_time
   FROM positive_signal
     JOIN exchange e ON positive_signal.exchange_id = e.id
     LEFT JOIN sell_analysis a ON positive_signal.signal_id = a.signal_id
     LEFT JOIN coin_24hr_market ON positive_signal.symbol::text = coin_24hr_market.symbol::text
     LEFT JOIN buy_analysis b ON positive_signal.signal_id = b.signal_id
     LEFT JOIN coin_stats ON positive_signal.symbol::text = coin_stats.symbol::text AND coin_stats.tic_type::text = '15Sec'::text
  ORDER BY (timezone('Australia/Sydney'::text, timezone('utc'::text, positive_signal.buy_signal_date_time))) DESC;

-- Permissions

-- ALTER TABLE public.overview OWNER TO postgres;
-- GRANT ALL ON TABLE public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(exchange_name) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(signal_id) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(strategy) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(buy_signal_date_time) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(buy_date_time) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(symbol) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(buy_price) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(actual_buy_price) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(current_bid_price) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(sell_price) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(actual_sell_price) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(status) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(sell_signal_date_time) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(sell_date_time) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(current_gain) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(signal_sold_gain_percent) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(actual_sold_gain_percent) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(stop_loss_value) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(sell_reason) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(max_bid_price) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(min_bid_price) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(moving_average30min) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(isstoplossactive) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(price_movement) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(last_mins_neg_flow) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(accumulatedduration) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(total_price_movement_percent) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(pricemovpercent) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(rsi) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(bolen_avg) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(change45min) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(curr_by_bolenger) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(curr_by_lowest) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(div_dip) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(first_resistance) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(second_resistance) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(movingavg60min) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(first_support) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(second_support) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(standard_deviation) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(tick_high) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(tick_low) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(waited_average) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(largeema) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(medema) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(smallema) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(movingaverage30minbefore) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(movingaverage1hrbefore) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(movingavgfrombuyanalysis) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(tick_high_24hr) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(tick_low_24hr) ON public.overview TO postgres;
-- GRANT ALL, SELECT, INSERT, UPDATE, DELETE, REFERENCES(sell_analysis_updated_time) ON public.overview TO postgres;
