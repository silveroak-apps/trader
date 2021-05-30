	ALTER TABLE futures_signal_command DROP COLUMN exchange_order_id;
	
	ALTER TABLE exchange_order  ADD COLUMN signal_command_id int8;
	
	ALTER TABLE exchange_order ADD CONSTRAINT fk_exchange_order_futures_signal_command 
    FOREIGN KEY (signal_command_id) REFERENCES futures_signal_command(id);