DROP VIEW IF EXISTS public.futures_positions CASCADE;

CREATE OR REPLACE VIEW public.futures_positions
AS SELECT fs2.signal_id,
    fs2.symbol,
    fs2.position_type,
    fs2.exchange_id,
    fs3.strategy_pair_name,
    CASE
        WHEN (COALESCE(buy.executed_qty, 0) + COALESCE(sell.executed_qty, 0) = 0) AND (COALESCE(buy.pending_qty, 0) + COALESCE(sell.pending_qty, 0) = 0) AND COALESCE(pending_commands.commandCount, 0) > 0 THEN 'CREATED'
        WHEN (COALESCE(buy.executed_qty, 0) + COALESCE(sell.executed_qty, 0) = 0) AND (COALESCE(buy.pending_qty, 0) + COALESCE(sell.pending_qty, 0) = 0) AND (COALESCE(open_commands.commandCount, 0) + COALESCE(close_commands.commandCount, 0)) = 0 AND COALESCE(pending_commands.commandCount, 0) = 0 THEN 'INACTIVE'
        WHEN COALESCE(buy.pending_qty, 0) > 0 OR COALESCE(sell.pending_qty, 0) > 0 THEN 'ACTIVE' 
        WHEN (COALESCE(buy.executed_qty, 0) > 0 OR COALESCE(sell.executed_qty, 0) > 0) AND COALESCE(buy.executed_qty, 0) <> COALESCE(sell.executed_qty, 0) THEN 'ACTIVE'
        WHEN COALESCE(buy.executed_qty, 0) = COALESCE(sell.executed_qty, 0) AND COALESCE(buy.executed_qty, 0) > 0 AND COALESCE(buy.pending_qty, 0) + COALESCE(sell.pending_qty, 0) = 0 THEN 'ENDED'
        ELSE 'UNKNOWN'
    END AS signal_status,
    CASE
        WHEN COALESCE(buy.executed_qty, 0) = COALESCE(sell.executed_qty, 0) AND COALESCE(buy.executed_qty, 0) > 0 AND (COALESCE(buy.pending_qty, 0) + COALESCE(sell.pending_qty, 0)) = 0 THEN 'CLOSED'
        WHEN fs2.position_type = 'LONG' AND COALESCE(buy.pending_qty, 0) > 0 THEN 'PENDING_OPEN'
        WHEN fs2.position_type = 'LONG' AND COALESCE(sell.pending_qty, 0) > 0 THEN 'PENDING_CLOSE'
        WHEN fs2.position_type = 'SHORT' AND COALESCE(buy.pending_qty, 0) > 0 THEN 'PENDING_CLOSE'
        WHEN fs2.position_type = 'SHORT' AND COALESCE(sell.pending_qty, 0) > 0 THEN 'PENDING_OPEN'
        WHEN (COALESCE(buy.executed_qty, 0) + COALESCE(sell.executed_qty, 0)) = 0 AND (COALESCE(buy.pending_qty, 0) + COALESCE(sell.pending_qty, 0)) = 0 AND (COALESCE(open_commands.commandCount, 0) + COALESCE(close_commands.commandCount, 0)) = 0 AND COALESCE(pending_commands.commandCount, 0) = 0 THEN 'NOT_OPEN'
        WHEN (COALESCE(buy.executed_qty, 0) + COALESCE(sell.executed_qty, 0)) = 0 AND (COALESCE(buy.pending_qty, 0) + COALESCE(sell.pending_qty, 0)) > 0 THEN 'NOT_YET_OPEN'
        WHEN (COALESCE(buy.executed_qty, 0) + COALESCE(sell.executed_qty, 0)) > 0 THEN 'OPEN'
        ELSE 'UNKOWN'
    END AS position_status,
    COALESCE(buy.executed_qty, 0) AS executed_buy_qty,
    COALESCE(buy.pending_qty, 0) AS pending_buy_qty,
    COALESCE(sell.executed_qty, 0) AS executed_sell_qty,
    COALESCE(sell.pending_qty, 0) AS pending_sell_qty,
    COALESCE(open_commands.commandcount, 0) as open_commands_count,
    COALESCE(close_commands.commandcount, 0) as close_commands_count,
    COALESCE(pending_commands.commandcount, 0) as pending_commands_count,
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
     LEFT JOIN ( SELECT 
            futures_signal_command.signal_id,
            count(futures_signal_command.signal_id) AS commandcount
           FROM futures_signal_command
          WHERE futures_signal_command.signal_action = 'OPEN' AND futures_signal_command.status NOT IN ('EXPIRED', 'FAILED')
          GROUP BY futures_signal_command.signal_id
         ) open_commands ON open_commands.signal_id = fs2.signal_id
     LEFT JOIN ( SELECT
            futures_signal_command.signal_id,
            count(futures_signal_command.signal_id) AS commandcount
           FROM futures_signal_command
          WHERE futures_signal_command.signal_action = 'CLOSE' AND futures_signal_command.status NOT IN ('EXPIRED', 'FAILED')
          GROUP BY futures_signal_command.signal_id
         ) close_commands ON close_commands.signal_id = fs2.signal_id
     LEFT JOIN ( SELECT
            futures_signal_command.signal_id,
            count(futures_signal_command.signal_id) AS commandcount
          FROM futures_signal_command
          WHERE futures_signal_command.status = 'CREATED'
          GROUP BY futures_signal_command.signal_id
         ) pending_commands ON pending_commands.signal_id = fs2.signal_id
     LEFT JOIN ( SELECT signal_order_summary.signal_id,
            signal_order_summary.order_side,
            sum(signal_order_summary.executed_qty) AS executed_qty,
            sum(signal_order_summary.pending_qty) AS pending_qty,
            sum(signal_order_summary.order_count) AS order_count,
            avg(signal_order_summary.avg_price) AS avg_price,
            min(signal_order_summary.first_order_time) AS first_order_time,
            max(signal_order_summary.last_order_time) AS last_order_time
           FROM signal_order_summary
          WHERE signal_order_summary.order_side = 'BUY' AND signal_order_summary.executed_qty > 0
          GROUP BY signal_order_summary.order_side, signal_order_summary.signal_id) buy ON buy.signal_id = fs2.signal_id
     LEFT JOIN ( SELECT signal_order_summary.signal_id,
            signal_order_summary.order_side,
            sum(signal_order_summary.executed_qty) AS executed_qty,
            sum(signal_order_summary.pending_qty) AS pending_qty,
            sum(signal_order_summary.order_count) AS order_count,
            avg(signal_order_summary.avg_price) AS avg_price,
            min(signal_order_summary.first_order_time) AS first_order_time,
            max(signal_order_summary.last_order_time) AS last_order_time
           FROM signal_order_summary
          WHERE signal_order_summary.order_side = 'SELL' AND signal_order_summary.executed_qty > 0
          GROUP BY signal_order_summary.order_side, signal_order_summary.signal_id) sell ON sell.signal_id = fs2.signal_id
     LEFT JOIN ( SELECT futures_signal.signal_id,
            min(futures_signal.strategy_pair_name) AS strategy_pair_name
           FROM futures_signal
          GROUP BY futures_signal.signal_id) fs3 ON fs3.signal_id = fs2.signal_id
    ORDER BY signal_id desc;