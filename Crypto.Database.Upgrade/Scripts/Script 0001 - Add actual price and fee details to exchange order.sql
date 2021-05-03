ALTER TABLE exchange_order 
ADD COLUMN fee_currency varchar(32) null,
ADD COLUMN fee_amount numeric(18, 10) null,
ADD COLUMN executed_price numeric(18, 10) null
;