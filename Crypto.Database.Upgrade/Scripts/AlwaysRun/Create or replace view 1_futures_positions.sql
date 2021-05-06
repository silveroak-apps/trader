DROP VIEW IF EXISTS public.futures_positions CASCADE;

CREATE OR REPLACE VIEW public.futures_positions
AS SELECT fs2.signal_id,
    fs2.symbol,
    fs2.position_type,
    fs2.exchange_id,
    fs3.strategy_pair_name,
    case
    	when COALESCE(open_command.status, '') = 'CREATED' and coalesce (close_command.status, '') = '' then 'CREATED'
    	when COALESCE(open_command.status, '') = 'EXPIRED' and coalesce (close_command.status, '') = '' then 'EXPIRED'
    	when COALESCE(open_command.status, '') = 'SUCCESS' and coalesce (close_command.status, '') IN ('', 'CREATED', 'EXPIRED', 'FAILED') and (COALESCE(buy.executed_qty, 0) > 0 OR COALESCE(sell.executed_qty, 0) > 0) then 'ACTIVE'
    	when COALESCE(open_command.status, '') = 'FAILED'  and coalesce (close_command.status, '') = '' and (COALESCE(buy.executed_qty, 0) + COALESCE(sell.executed_qty, 0) = 0) then 'ENDED'
        WHEN COALESCE(buy.executed_qty, 0) = COALESCE(sell.executed_qty, 0) AND COALESCE(buy.executed_qty, 0) > 0 AND (COALESCE(buy.pending_qty, 0) + COALESCE(sell.pending_qty, 0)) = 0 
        		and COALESCE(open_command.status, '') = 'SUCCESS' and COALESCE(close_command.status, '') = 'SUCCESS'
        	THEN 'ENDED'
        ELSE 'UNKNOWN' -- CONCAT('UNKNOWN (open: ', open_command.status, ', close: ', close_command.status, ')') -- bugs in the code could cause signals to stay active - we need to alert on this
    END AS signal_status,
    CASE
        WHEN COALESCE(buy.executed_qty, 0) = COALESCE(sell.executed_qty, 0) AND COALESCE(buy.executed_qty, 0) > 0 AND (COALESCE(buy.pending_qty, 0) + COALESCE(sell.pending_qty, 0)) = 0 THEN 'CLOSED'
        WHEN fs2.position_type = 'LONG' AND COALESCE(buy.pending_qty, 0) > 0 THEN 'PENDING_OPEN'
        WHEN fs2.position_type = 'LONG' AND COALESCE(sell.pending_qty, 0) > 0 THEN 'PENDING_CLOSE'
        WHEN fs2.position_type = 'SHORT' AND COALESCE(buy.pending_qty, 0) > 0 THEN 'PENDING_CLOSE'
        WHEN fs2.position_type = 'SHORT' AND COALESCE(sell.pending_qty, 0) > 0 THEN 'PENDING_OPEN'
        WHEN (COALESCE(buy.executed_qty, 0) + COALESCE(sell.executed_qty, 0)) = 0 AND (COALESCE(buy.pending_qty, 0) + COALESCE(sell.pending_qty, 0)) >= 0 THEN 'NOT_YET_OPEN'
        WHEN (COALESCE(buy.executed_qty, 0) + COALESCE(sell.executed_qty, 0)) > 0 THEN 'OPEN'
        ELSE 'UNKOWN'
    END AS position_status,
    COALESCE(buy.executed_qty, 0) AS executed_buy_qty,
    COALESCE(buy.pending_qty, 0) AS pending_buy_qty,
    COALESCE(sell.executed_qty, 0) AS executed_sell_qty,
    COALESCE(sell.pending_qty, 0) AS pending_sell_qty,
    CASE
        WHEN fs2.position_type = 'LONG' THEN buy.avg_price
        ELSE sell.avg_price
    END AS entry_price,
    CASE
        WHEN fs2.position_type = 'LONG' THEN sell.avg_price
        ELSE buy.avg_price
    END AS close_price,
    CASE
        WHEN fs2.position_type = 'LONG' THEN buy.first_order_time
        ELSE sell.first_order_time
    END AS entry_time,
    CASE
        WHEN fs2.position_type = 'LONG' THEN sell.last_order_time
        ELSE buy.last_order_time
    END AS exit_time
   FROM futures_signal fs2
   	 LEFT JOIN (SELECT max(id) AS id, signal_id, status, signal_action, count(signal_id) AS commandCount 
   	 		    FROM futures_signal_command 
   	 		    WHERE signal_action = 'OPEN'
			    GROUP BY signal_id, status, signal_action
			    ORDER BY id DESC LIMIT 1
   	 	) open_command ON open_command.signal_id = fs2.signal_id
   	 LEFT JOIN (SELECT max(id) AS id, signal_id, status, signal_action, count(signal_id) AS commandCount 
   	 		    FROM futures_signal_command 
   	 		    WHERE signal_action = 'CLOSE'
			    GROUP BY signal_id, status, signal_action
			    ORDER BY id DESC LIMIT 1
   	 	) close_command ON close_command.signal_id = fs2.signal_id
     LEFT JOIN ( SELECT signal_order_summary.signal_id,
            signal_order_summary.order_side,
            sum(signal_order_summary.executed_qty) AS executed_qty,
            sum(signal_order_summary.pending_qty) AS pending_qty,
            sum(signal_order_summary.order_count) AS order_count,
            avg(signal_order_summary.avg_price) AS avg_price,
            min(signal_order_summary.first_order_time) as first_order_time,
            max(signal_order_summary.last_order_time) as last_order_time
           FROM signal_order_summary
          WHERE signal_order_summary.order_side = 'BUY' AND signal_order_summary.executed_qty > 0
          GROUP BY signal_order_summary.order_side, signal_order_summary.signal_id) buy ON buy.signal_id = fs2.signal_id
     LEFT JOIN ( SELECT signal_order_summary.signal_id,
            signal_order_summary.order_side,
            sum(signal_order_summary.executed_qty) AS executed_qty,
            sum(signal_order_summary.pending_qty) AS pending_qty,
            sum(signal_order_summary.order_count) AS order_count,
            avg(signal_order_summary.avg_price) AS avg_price,
            min(signal_order_summary.first_order_time) as first_order_time,
            max(signal_order_summary.last_order_time) as last_order_time
           FROM signal_order_summary
          WHERE signal_order_summary.order_side = 'SELL' AND signal_order_summary.executed_qty > 0
          GROUP BY signal_order_summary.order_side, signal_order_summary.signal_id) sell ON sell.signal_id = fs2.signal_id
      LEFT JOIN (SELECT signal_id, min(strategy_pair_name) AS strategy_pair_name 
            FROM futures_signal
            GROUP BY signal_id) fs3 ON fs3.signal_id = fs2.signal_id