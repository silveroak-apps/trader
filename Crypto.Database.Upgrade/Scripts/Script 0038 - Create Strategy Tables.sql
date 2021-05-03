create sequence if not exists strategy_id_seq
;

create sequence if not exists strategy_conditions_id_seq
;

create table strategy (
	id int8 default nextval('strategy_id_seq'::regclass) not null
		constraint strategy_pkey
			primary key,
	name varchar(255) not null,
	position_type varchar(255) not null,
	exchange_type varchar(255) not null,
	symbol varchar not null,
	version int8 not null,
	status varchar(54) not null,
	updated_time timestamp not null,
	created_time timestamp not null,
	UNIQUE (name)
);


create table strategy_conditions (
	id int8 default nextval('strategy_conditions_id_seq'::regclass) not null
		constraint strategy_conditions_pkey
			primary key,
	strategy_id int8 not null,
    name varchar(255) not null,
    time_frame int8 not null,
    last_observed int8 not null,
    category varchar(255) not null,
	condition_group varchar(255) not null, -- OPEN / CLOSE
	created_time timestamp not null,
    sequence int8 not null,
	version int8 not null,
	condition_sub_group int8 NOT NULL,
    CONSTRAINT fk_strategy_conditions FOREIGN KEY (strategy_id) REFERENCES strategy(id)
);
