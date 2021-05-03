GRANT ALL ON TABLE public.futures_signal TO live_sim_user;
GRANT ALL ON TABLE public.futures_signal_command TO live_sim_user;
GRANT ALL ON TABLE public.kline_coin_analysis TO live_sim_user;
GRANT ALL ON TABLE public.kline_coin_analysis_history TO live_sim_user;
GRANT ALL ON TABLE public.kline_data TO live_sim_user;
GRANT ALL ON TABLE public.kline_stats TO live_sim_user;
GRANT ALL ON TABLE public.kline_stats_history TO live_sim_user;
GRANT ALL ON TABLE public.market_event TO live_sim_user;

GRANT SELECT ON TABLE public.futures_signals TO live_sim_user;
GRANT SELECT ON TABLE public.futures_pnl TO live_sim_user;
GRANT SELECT ON TABLE public.signal_order_summary TO live_sim_user;

GRANT ALL ON TABLE public.overview_sell_analysis TO live_sim_user;