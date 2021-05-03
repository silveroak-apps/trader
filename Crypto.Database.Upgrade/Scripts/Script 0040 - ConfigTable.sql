create table configs (
	name varchar(128) not null constraint config_name_pkey
			primary key,
	json_value varchar(4096) not null
);

INSERT INTO configs (name, json_value) VALUES ('contracts', '{ "configuration": [{"key": "BNBUSD_PERP","value": "100 "},{"key": "BTCUSD_PERP","value": "10"},{"key": "ETHUSD_PERP","value": "100"},{"key": "DOTUSD_PERP","value": "100"},{"key": "LINKUSDT","value": "100"},{"key": "ADAUSDT","value": "100"},{"key": "DOGEUSDT","value": "100"}]}');