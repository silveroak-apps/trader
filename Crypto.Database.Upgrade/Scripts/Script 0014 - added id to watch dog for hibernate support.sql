ALTER TABLE public.wachdog ADD id bigint NULL;
ALTER TABLE public.wachdog DROP CONSTRAINT wachdog_pkey;
ALTER TABLE public.wachdog ADD CONSTRAINT wachdog_id_pk PRIMARY KEY (id);
CREATE UNIQUE INDEX wachdog_resource_uindex ON public.wachdog (resource);