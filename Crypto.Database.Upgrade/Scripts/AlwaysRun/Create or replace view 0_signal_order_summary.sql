
DROP VIEW IF EXISTS public.signal_order_summary CASCADE;

CREATE OR REPLACE VIEW public.signal_order_summary
AS SELECT fs2.signal_id, exorder.status, exorder.order_side,
	    COALESCE(sum(executedOrder.executed_qty), 0) AS executed_qty,
		COALESCE(sum(executedOrder.executed_price * executedOrder.executed_qty) / sum(executedOrder.executed_qty), 0) AS avg_price,
		min(executedOrder.updated_time) as first_order_time,
		max(executedOrder.updated_time) as last_order_time,
	    case 
			when exorder.status not in ('CANCELED', 'REJECTED', 'FILLED', 'ERROR') then COALESCE(sum(exorder.original_qty), 0) - COALESCE(sum(exorder.executed_qty), 0) 
			else 0 
		end AS pending_qty,
	    case
			when exorder.status not in ('CANCELED', 'REJECTED', 'ERROR') then COALESCE(count(exorder.id), 0)  
			else 0 
		end AS order_count
	FROM signal fs2
	    LEFT JOIN exchange_order exorder ON exorder.signal_id = fs2.signal_id 
		LEFT JOIN exchange_order executedOrder ON executedOrder.signal_id = fs2.signal_id AND executedOrder.id = exorder.id AND executedOrder.executed_qty > 0 
GROUP BY fs2.signal_id, exorder.status, exorder.order_side;
