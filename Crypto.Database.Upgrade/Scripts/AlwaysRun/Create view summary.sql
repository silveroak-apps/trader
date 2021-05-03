CREATE OR REPLACE VIEW public.summary
AS select 
exchange_name,
signal_id,
symbol,
case
when status IN ('SIGNAL_LAPSED', 'SIG_INVALID', 'BUY_CANCELED', 'BUY_REJECTED') then 'CANCELED'::varchar(255)
when status = 'SELL_FILLED' then 'SOLD'::varchar(255)
else ('ACTIVE'::varchar(255))
end as status, 
status as signal_status,
current_gain, 
signal_sold_gain_percent,
actual_sold_gain_percent,
actual_buy_price, 
actual_sell_price,
buy_date_time, 
sell_date_time,
sell_date_time - buy_date_time as total_duration,
current_bid_price, 
sell_analysis_updated_time
from overview
where buy_date_time is not null
order by buy_date_time desc;
