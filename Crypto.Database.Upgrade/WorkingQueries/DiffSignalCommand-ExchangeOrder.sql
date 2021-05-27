select 
-- fs2.signal_id, openCmd.id as open_cmd_id, openCmd.status as open_cmd_status, closeCmd.id as close_cmd_id, closeCmd.status as close_cmd_status, openOrder.id as openOrderId, closeOrder.id as closeOrderId
min(openOrder.created_time - openCmd.request_date_time) as minOpenDiff, avg(openOrder.created_time - openCmd.request_date_time) as avgOpenDiff, max(openOrder.created_time - openCmd.request_date_time) as maxOpenDiff
--,
--min(closeOrder.created_time - closeCmd.request_date_time) as minCloseDiff, avg(closeOrder.created_time - closeCmd.request_date_time) as avgCloseDiff, max(closeOrder.created_time - closeCmd.request_date_time) as maxCloseDiff
	from
		futures_signal fs2
		left join futures_signal_command openCmd on fs2.signal_id = openCmd.signal_id and openCmd.signal_action = 'OPEN'
		--left join futures_signal_command closeCmd on fs2.signal_id = closeCmd.signal_id and closeCmd.signal_action = 'CLOSE'
		left join exchange_order openOrder on openCmd.signal_id = openOrder.signal_id and
		    ((openOrder.order_side = 'BUY' and fs2.position_type = 'LONG') or 
		     (openOrder.order_side = 'SELL' and fs2.position_type = 'SHORT'))
--		left join exchange_order closeOrder on closeCmd.signal_id = closeOrder.signal_id and
--		    ((closeOrder.order_side = 'SELL' and fs2.position_type = 'LONG') or 
--		     (closeOrder.order_side = 'buy' and fs2.position_type = 'SHORT'))
where openCmd.request_date_time > '2021-05-19'
-- or closeCmd.request_date_time > '2021-05-19'
--order by 1, 2, 3, 4