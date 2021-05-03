ALTER TABLE buy_analysis ADD COLUMN exchange varchar(50) null;
ALTER TABLE coin_24hr_market ADD COLUMN exchange varchar(50) null;
ALTER TABLE exchange ADD COLUMN name varchar(50) null;
ALTER TABLE coin_stats ADD COLUMN exchange varchar(50) null;
ALTER TABLE positive_signal ADD COLUMN exchange varchar(50) null;
ALTER TABLE sell_analysis2 ADD COLUMN exchange varchar(50) null;
ALTER TABLE ticker ADD COLUMN exchange varchar(50) null;

create table if not exists exchage_coin_map
(
	id bigint not null,
	symbol varchar(25) not null,
	exchange varchar(50) not null,
	exchange_symbol varchar (50) not null
)
;