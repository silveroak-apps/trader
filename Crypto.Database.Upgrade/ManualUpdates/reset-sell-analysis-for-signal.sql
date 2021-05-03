-- reset a sell-analysis for a signal
delete from sell_analysis_bids
where signal_id = 58678038;

delete from sell_analysis_gain_bands
where signal_id = 58678038;

delete from sell_analysis
where signal_id = 58678038;

update positive_signal
set buy_date_time = (select timezone('UTC', now())), status = 'BUY_FILLED', sell_price = null, sell_date_time = null, sell_signal_date_time = null
where signal_id = 58678038;