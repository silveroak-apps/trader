create table symbols (
	name varchar(255) not null constraint symbol_pkey
			primary key
);

INSERT INTO symbols VALUES ('BTCUSDT');
INSERT INTO symbols VALUES ('BNBUSDT');
INSERT INTO symbols VALUES ('BTCUSD_PERP');
INSERT INTO symbols VALUES ('BNBUSD_PERP');
