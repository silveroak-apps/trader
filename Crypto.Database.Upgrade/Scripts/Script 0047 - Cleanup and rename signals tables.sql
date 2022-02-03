ALTER TABLE futures_signal RENAME TO signals;
ALTER TABLE futures_signal_command RENAME TO signal_command;

drop table positive_signal;
drop table positive_signal_excel;
ALTER TABLE signals  ADD COLUMN strategy_pair_id bigserial;

ALTER TABLE signal_command ADD COLUMN strategy_condition_id bigserial;

ALTER TABLE singnals 
ADD CONSTRAINT fk_strategy_pair 
FOREIGN KEY (strategy_pair_id) 
REFERENCES strategy (id);



ALTER TABLE signal_command 
ADD CONSTRAINT fk_strategy_condition
FOREIGN KEY (strategy_condition_id) 
REFERENCES strategy_conditions (id);