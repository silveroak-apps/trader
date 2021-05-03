update positive_signal
set sell_signal_date_time = timezone('UTC', now()), status = 'SELL', sell_price = 0.0000018700
where signal_id = 58678038;