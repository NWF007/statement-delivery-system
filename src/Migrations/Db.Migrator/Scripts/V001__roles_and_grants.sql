-- =============================================================================================
-- V001  Least-privilege database roles.
--
-- One role per service. This is cheap now and painful to retrofit: once four services share one
-- role, separating them means auditing every statement each of them issues, and nobody has time
-- for that during the incident that makes it urgent.
--
-- The principle: the application is assumed to be compromised. Row-level bugs are caught by
-- triggers and constraints; a compromised APPLICATION USER is caught by the grant matrix, and the
-- attacker has to defeat both, using two different credentials, to destroy data.
--
-- NOTE ON DOLLAR QUOTING. DbUp substitutes $name$ variables in these scripts. Anonymous blocks and
-- function bodies must therefore be quoted with a BARE $$ and never with a named tag such as
-- $do$ or $body$ - a named tag looks exactly like a DbUp variable and will be eaten.
-- =============================================================================================

-- ---------------------------------------------------------------------------------------------
-- Roles. Idempotent: CREATE ROLE has no IF NOT EXISTS, and this script must be safe to re-run
-- against a database that has been partially provisioned by hand.
-- ---------------------------------------------------------------------------------------------
DO
$$
DECLARE
    target RECORD;
BEGIN
    FOR target IN
        SELECT t.role_name, t.role_password FROM (VALUES
            ('app_delivery',   '$appDeliveryPassword$'),
            ('app_download',   '$appDownloadPassword$'),
            ('app_generation', '$appGenerationPassword$'),
            ('app_retention',  '$appRetentionPassword$'),
            ('app_migrator',   '$appMigratorPassword$')
        ) AS t(role_name, role_password)
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = target.role_name) THEN
            EXECUTE format('CREATE ROLE %I LOGIN PASSWORD %L', target.role_name, target.role_password);
        ELSE
            EXECUTE format('ALTER ROLE %I LOGIN PASSWORD %L', target.role_name, target.role_password);
        END IF;

        -- Connection caps per role. This is the last line of defence for the constraint that
        -- shapes the whole persistence design: PostgreSQL forks a backend per connection, and the
        -- 400-replica generation fleet would happily ask for thousands. PgBouncer is what actually
        -- keeps the number down (ADR-0008); this makes a misconfigured service that bypasses the
        -- pooler fail loudly, on its own, instead of taking the database with it.
        EXECUTE format('ALTER ROLE %I CONNECTION LIMIT %s', target.role_name,
            CASE target.role_name
                WHEN 'app_generation' THEN 40
                WHEN 'app_migrator'   THEN 5
                ELSE 60
            END);

        -- Nothing here is a superuser, and nothing here may create more roles or databases.
        EXECUTE format('ALTER ROLE %I NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS', target.role_name);
    END LOOP;
END
$$;

-- ---------------------------------------------------------------------------------------------
-- Database and schema level.
-- ---------------------------------------------------------------------------------------------
DO
$$
DECLARE
    role_name TEXT;
BEGIN
    -- PUBLIC gets nothing. Every privilege from here on is granted explicitly, to a named role,
    -- on a named object.
    EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());

    FOREACH role_name IN ARRAY ARRAY['app_delivery', 'app_download', 'app_generation', 'app_retention', 'app_migrator']
    LOOP
        EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), role_name);
        EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', role_name);
    END LOOP;
END
$$;

REVOKE ALL ON SCHEMA public FROM PUBLIC;

-- Only the migrator may create objects. A running service that can CREATE TABLE can also create
-- a table the audit trail knows nothing about.
GRANT CREATE ON SCHEMA public TO app_migrator;

-- Objects created by the migrator are readable by nobody until a later script grants explicitly.
-- Default privileges are deliberately NOT used to hand out blanket SELECT: a table that appears
-- with rights nobody chose is the failure this whole script exists to prevent.
ALTER DEFAULT PRIVILEGES FOR ROLE app_migrator IN SCHEMA public REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE app_migrator IN SCHEMA public REVOKE ALL ON SEQUENCES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE app_migrator IN SCHEMA public REVOKE ALL ON FUNCTIONS FROM PUBLIC;

-- ---------------------------------------------------------------------------------------------
-- THE INTENDED GRANT MATRIX.
--
-- The business tables do not exist yet. The matrix is recorded here, next to the roles, so that
-- the migration which creates each table grants against a decision that was already made rather
-- than one invented on the day.
--
--   ROLE            GRANTS
--   ------------------------------------------------------------------------------------------
--   app_delivery    SELECT on statement, account
--                   SELECT, INSERT, UPDATE on download_token
--                   INSERT on audit_event
--                   NO DELETE ANYWHERE.
--
--   app_download    SELECT on statement
--                   UPDATE on download_token          (the atomic single-use consume)
--                   INSERT on audit_event
--                   Nothing else. This role is reachable from the public internet.
--
--   app_generation  INSERT, UPDATE on statement and the run tables
--                   INSERT on outbox, audit_event
--
--   app_retention   SELECT, UPDATE, DELETE on statement
--                   INSERT on audit_event
--                   THE ONLY ROLE WITH DELETE. That is why it is a separate deployable with a
--                   separate identity and a separate approval path.
--
--   app_migrator    DDL owner. Used only by Db.Migrator, never by a running service.
--
-- And, on every role, once audit_event exists:
--
--   REVOKE UPDATE, DELETE ON audit_event FROM PUBLIC;
--
-- The append-only trigger catches application bugs. The grant revocation catches a compromised
-- application user. An attacker must defeat both, and defeating the grant needs a separate
-- credential.
-- ---------------------------------------------------------------------------------------------

-- Applied defensively now so that it is already true the moment audit_event appears, rather than
-- depending on whoever writes that migration remembering.
DO
$$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tablename = 'audit_event') THEN
        EXECUTE 'REVOKE UPDATE, DELETE ON public.audit_event FROM PUBLIC';
    END IF;
END
$$;
