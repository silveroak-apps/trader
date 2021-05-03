drop view if exists futures_pnl CASCADE;

create or replace view futures_pnl
as
select fp.*,
	case
		when fp.position_status = 'CLOSED' and fp.position_type = 'LONG' then cast((fp.close_price * fp.executed_sell_qty) - (fp.entry_price * fp.executed_buy_qty) as numeric(18, 10))
		when fp.position_status = 'CLOSED' and fp.position_type = 'SHORT' then cast((fp.entry_price * fp.executed_sell_qty) - (fp.close_price * fp.executed_buy_qty) as numeric(18, 10))
		else 0
	end as pnl,
	case
		when fp.position_status = 'CLOSED' and fp.position_type = 'LONG'  and fp.entry_price * fp.executed_buy_qty  > 0 then cast(100 * ((fp.close_price * fp.executed_sell_qty) - (fp.entry_price * fp.executed_buy_qty)) / (fp.entry_price * fp.executed_buy_qty) as numeric(18, 10))
		when fp.position_status = 'CLOSED' and fp.position_type = 'SHORT' and fp.entry_price * fp.executed_sell_qty > 0 then cast(100 * ((fp.entry_price * fp.executed_sell_qty) - (fp.close_price * fp.executed_buy_qty)) / (fp.entry_price * fp.executed_sell_qty) as numeric(18, 10))
		else 0
	end as pnl_percent
from futures_positions fp
	join futures_signal fs2 on fs2.signal_id = fp.signal_id
order by signal_id desc