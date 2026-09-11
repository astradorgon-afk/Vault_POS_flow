-- Cluster initialisation. Runs once, as the superuser, before the application
-- ever connects.
--
-- Three roles with different powers is what makes the ledger's immutability
-- triggers meaningful: the application role cannot drop them, because it does
-- not own the tables.
--
-- Passwords come from the environment, which compose populates from Docker
-- secrets. This script never contains one.

\set app_password `echo "$POS_APP_PASSWORD"`
\set readonly_password `echo "$POS_READONLY_PASSWORD"`

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pos_app') THEN
        CREATE ROLE pos_app LOGIN;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pos_readonly') THEN
        CREATE ROLE pos_readonly LOGIN;
    END IF;
END
$$;

ALTER ROLE pos_app      WITH PASSWORD :'app_password';
ALTER ROLE pos_readonly WITH PASSWORD :'readonly_password';

GRANT CONNECT ON DATABASE vaultflow TO pos_app, pos_readonly;

-- No role other than the owner may create objects in public.
REVOKE ALL ON SCHEMA public FROM PUBLIC;
