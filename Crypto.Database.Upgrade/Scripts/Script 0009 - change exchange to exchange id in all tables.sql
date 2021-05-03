INSERT INTO exchange (id, name) VALUES (1, 'Binance') 
ON CONFLICT (id) DO NOTHING;

--unused
INSERT INTO exchange (id, name) VALUES (2, 'Poloniex')
ON CONFLICT (id) DO NOTHING;

ALTER TABLE buy_analysis DROP COLUMN exchange;
ALTER TABLE buy_analysis ADD exchange_id bigint NOT NULL DEFAULT(1);
ALTER TABLE buy_analysis ADD CONSTRAINT fk_buy_analysis_exchange FOREIGN KEY (exchange_id) REFERENCES exchange (id);

ALTER TABLE coin_24hr_market DROP COLUMN exchange;
ALTER TABLE coin_24hr_market ADD exchange_id bigint NOT NULL DEFAULT(1);
ALTER TABLE coin_24hr_market ADD CONSTRAINT fk_coin_24hr_market_exchange FOREIGN KEY (exchange_id) REFERENCES exchange (id);

ALTER TABLE coin_stats DROP COLUMN exchange;
ALTER TABLE coin_stats ADD exchange_id bigint NOT NULL DEFAULT(1);
ALTER TABLE coin_stats ADD CONSTRAINT fk_coin_stats_exchange FOREIGN KEY (exchange_id) REFERENCES exchange (id);

ALTER TABLE positive_signal DROP COLUMN exchange;
ALTER TABLE positive_signal ADD exchange_id bigint NOT NULL DEFAULT(1);
ALTER TABLE positive_signal ADD CONSTRAINT fk_positive_signal_exchange FOREIGN KEY (exchange_id) REFERENCES exchange (id);

ALTER TABLE sell_analysis2 DROP COLUMN exchange;
ALTER TABLE sell_analysis2 ADD exchange_id bigint NOT NULL DEFAULT(1);
ALTER TABLE sell_analysis2 ADD CONSTRAINT fk_sell_analysis2_exchange FOREIGN KEY (exchange_id) REFERENCES exchange (id);

ALTER TABLE ticker DROP COLUMN exchange;
ALTER TABLE ticker ADD exchange_id bigint NOT NULL DEFAULT(1);
ALTER TABLE ticker ADD CONSTRAINT fk_ticker_exchange FOREIGN KEY (exchange_id) REFERENCES exchange (id);

DROP TABLE IF EXISTS exchage_coin_map;

CREATE TABLE IF NOT EXISTS exchange_coin_map
(
	id bigint primary key not null,
	symbol varchar(25) not null,
	exchange_id bigint not null,
	exchange_symbol varchar(50) not null,
    active boolean not null
);

ALTER TABLE exchange_coin_map
ADD CONSTRAINT fk_exchange_coin_map FOREIGN KEY (exchange_id) REFERENCES exchange(id);