create table futures_signal (
	signal_id bigint  not null
		constraint futures_signal_pkey
			primary key, 
	symbol varchar(255) not null,
	strategy_pair_name varchar(255) not null,
	position_type varchar(32) not null, -- LONG / SHORT
	created_date_time timestamp not null, -- date time of the first open signal/event
	updated_date_time timestamp null, -- date time of the last updated
	exchange_id bigint not null default 1
);

-- public.futures_signal foreign keys

alter table futures_signal add constraint fk_futures_signal_exchange foreign key (exchange_id) references exchange(id);

create table futures_signal_command (
	id bigint not null
		constraint futures_signal_command_pkey
			primary key,
	signal_id bigint not null, 
	price numeric(18,10) not null, -- suggested/requested price to perform the action
	quantity numeric(18,10) null, -- the amount/qty to increase/decrease position
	leverage int not null, -- only valid when opening a position eg 1, 2, 5, 10, 20 etc
	signal_action varchar(32) not null, -- OPEN, CLOSE, INCREASE, DECREASE
	status varchar(32) not null, -- CREATED, [SUCCESS, FAILED, PARTIAL_SUCCESS, COMMAND_EXPIRED, COMMAND_OVERRIDDEN] (statuses in [] are final)
	request_date_time timestamp not null, -- the time when this action/command was requested
	action_date_time timestamp null, -- the time when this action/command was performed
	exchange_order_id bigint null, --link to the exchange order that was executed as a result of this command
	strategy_name varchar(255) not null,
	strategy_hash varchar(255) not null,
	strategy_data varchar(8192) not null
);

-- public.futures_signal_command foreign keys

alter table futures_signal_command add constraint fk_futures_signal_command_exchange_order foreign key (exchange_order_id) references exchange_order(id);
alter table futures_signal_command add constraint fk_futures_signal_command_signal foreign key (signal_id) references futures_signal(signal_id);

-- market event

create table market_event (
	id bigint not null
		constraint market_event_pkey
			primary key,
	source varchar(255) not null,
	name varchar(255) not null,
	message varchar(8192) not null, -- not using JSON data type due to previous low performance in Postgres in queries etc
	event_time timestamp not null,
	symbol varchar(255) not null,
	price numeric(18,10) not null,
	market varchar(255) not null,
	time_frame int8 not null,
	category varchar(128) not null
);
