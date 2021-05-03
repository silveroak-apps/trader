DROP VIEW IF EXISTS public.summary;
DROP VIEW IF EXISTS public.overview;

CREATE OR REPLACE VIEW public.overview
AS select
e.name as exchange_name,
positive_signal.signal_id,
strategy,
buy_signal_date_time at time zone 'utc' at time zone 'Australia/Sydney' as buy_signal_date_time,
buy_date_time at time zone 'utc' at time zone 'Australia/Sydney' as buy_date_time,
positive_signal.symbol,
buy_price,
actual_buy_price,
a.current_bid_price,
sell_price,
actual_sell_price,
status,
sell_signal_date_time at time zone 'utc' at time zone 'Australia/Sydney' as sell_signal_date_time,
sell_date_time at time zone 'utc' at time zone 'Australia/Sydney' as sell_date_time,
CASE
  WHEN status = 'SELL_FILLED' THEN 100 * (actual_sell_price - actual_buy_price) / actual_buy_price
  ELSE a.current_gain
  END as current_gain,
((positive_signal.sell_price - buy_price) / buy_price) * 100 as signal_sold_gain_percent,
((positive_signal.actual_sell_price - actual_buy_price) / buy_price) * 100 as actual_sold_gain_percent,
a.stop_loss_value,
a.decision_sell_reason as sell_reason,
a.max_bid_price,
a.min_bid_price,
a.price_movement,
b.last_mins_neg_flow,
(accumulated_duration_seconds)/3600 as AccumulatedDuration,
a.total_price_movement_percent as SellTotalPriceMovementPercent,
b.price_movement_percent as PriceMovPercent,
b.rsi,
b.bolen_avg,
b.change45min,
b.curr_by_bolenger,
b.curr_by_lowest,
b.div_dip,
b.first_resistance,
b.second_resistance,
b.moving_agv60min as movingAvg60Min,
b.first_support,
b.second_support,
b.standard_deviation,
b.tick_high,
b.tick_low,
b.waited_average,
b.largeema,
b.medema,
b.smallema,
b.moving_averageb2 as movingAverage30MinBefore,
b.moving_averageb3 as movingAverage1HrBefore,
b.moving_average30min as movingAvgFromBuyAnalysis,
b.tick_high_24hr,
b.tick_low_24hr,
-- can only add columns to a view at the end!!
a.updated_time at time zone 'utc' at time zone 'Australia/Sydney' as sell_analysis_updated_time
FROM positive_signal
     JOIN exchange e ON positive_signal.exchange_id = e.id
LEFT JOIN sell_analysis a ON positive_signal.signal_id = a.signal_id
LEFT JOIN buy_analysis b on positive_signal.signal_id = b.signal_id
ORDER BY buy_signal_date_time desc;


DO
$$
BEGIN
   IF EXISTS (
      SELECT -- SELECT list can stay empty for this
      FROM   pg_catalog.pg_roles
      WHERE  rolname = 'cryptolive') THEN

    grant usage on schema public to cryptolive;
    grant select on all tables in schema public to cryptolive;

   END IF;
END
$$;
