-- =============================================================================================
-- V003  Range-partition maintenance helpers.
--
-- An INSERT into a range-partitioned table with no partition covering the row FAILS. A missing
-- partition is an OUTAGE, not a warning, so pre-creation is a scheduled job with a health check
-- behind it rather than a cron script somebody remembers to write.
-- See docs/adr/0007-partitioning-strategy.md.
--
-- Both functions are mechanism-independent: they read the catalogue rather than a naming
-- convention, so they keep working if partition creation is ever handed to pg_partman.
--
-- NOTE ON DOLLAR QUOTING: bare $$ only, never a named tag. DbUp substitutes $name$ variables in
-- these scripts and would eat $body$ or $fn$.
-- =============================================================================================

-- ---------------------------------------------------------------------------------------------
-- ensure_range_partitions - create partitions from the period containing p_from through
-- p_periods_ahead further periods. Idempotent; returns how many it actually created.
--
-- SECURITY DEFINER, owned by app_migrator. The services that call this deliberately have no
-- CREATE privilege on the schema - a running service that can CREATE TABLE can also create one
-- the audit trail knows nothing about. This function is the single, narrow, reviewable exception,
-- and search_path is pinned so the definer's privileges cannot be redirected at a schema the
-- caller controls.
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION ensure_range_partitions(
    p_parent        regclass,
    p_granularity   text,
    p_periods_ahead integer,
    p_from          timestamptz DEFAULT now()
)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS
$$
DECLARE
    v_created integer := 0;
    v_schema  text;
    v_base    text;
    v_step    interval;
    v_pattern text;
    v_start   timestamp;
    v_lower   timestamp;
    v_upper   timestamp;
    v_child   text;
    v_period  integer;
BEGIN
    IF p_granularity NOT IN ('day', 'month') THEN
        RAISE EXCEPTION 'Unsupported partition granularity %. Expected day or month.', p_granularity;
    END IF;

    IF p_periods_ahead < 0 OR p_periods_ahead > 400 THEN
        RAISE EXCEPTION 'p_periods_ahead must be between 0 and 400, got %.', p_periods_ahead;
    END IF;

    SELECT n.nspname, c.relname
      INTO v_schema, v_base
      FROM pg_class c
      JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE c.oid = p_parent;

    IF NOT EXISTS (SELECT 1 FROM pg_partitioned_table WHERE partrelid = p_parent) THEN
        RAISE EXCEPTION '%.% is not a partitioned table.', v_schema, v_base;
    END IF;

    v_step    := CASE p_granularity WHEN 'day' THEN interval '1 day' ELSE interval '1 month' END;
    v_pattern := CASE p_granularity WHEN 'day' THEN 'YYYY_MM_DD' ELSE 'YYYY_MM' END;

    -- Truncate in UTC explicitly. Deriving bounds from the session TimeZone would make the same
    -- migration produce different partition boundaries on two differently configured servers.
    v_start := date_trunc(p_granularity, p_from AT TIME ZONE 'UTC');

    FOR v_period IN 0..p_periods_ahead LOOP
        v_lower := v_start + (v_step * v_period);
        v_upper := v_lower + v_step;
        v_child := format('%s_%s', v_base, to_char(v_lower, v_pattern));

        IF to_regclass(format('%I.%I', v_schema, v_child)) IS NULL THEN
            -- Bounds written with an explicit +00 offset so they are an absolute instant rather
            -- than something reinterpreted by whatever TimeZone the reader happens to be using.
            EXECUTE format(
                'CREATE TABLE %I.%I PARTITION OF %I.%I FOR VALUES FROM (%L) TO (%L)',
                v_schema,
                v_child,
                v_schema,
                v_base,
                to_char(v_lower, 'YYYY-MM-DD HH24:MI:SS') || '+00',
                to_char(v_upper, 'YYYY-MM-DD HH24:MI:SS') || '+00');

            v_created := v_created + 1;
        END IF;
    END LOOP;

    RETURN v_created;
END
$$;

COMMENT ON FUNCTION ensure_range_partitions(regclass, text, integer, timestamptz) IS
    'Idempotently pre-creates range partitions N periods ahead. SECURITY DEFINER so services need no CREATE privilege.';

-- ---------------------------------------------------------------------------------------------
-- range_partition_exists - does a partition covering p_at exist on p_parent?
--
-- Reads the actual partition bounds out of the catalogue rather than guessing at a name, so it
-- VERIFIES THE OUTCOME instead of trusting the mechanism. A check that only knows the names its
-- own creator would have used is a check that passes for the wrong reason the moment anything
-- else creates a partition.
--
-- SECURITY INVOKER: it reads only catalogue tables, which are readable by every role anyway.
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION range_partition_exists(
    p_parent      regclass,
    p_granularity text,
    p_at          timestamptz
)
RETURNS boolean
LANGUAGE sql
STABLE
SET search_path = pg_catalog, public
AS
$$
    SELECT EXISTS (
        SELECT 1
          FROM pg_inherits i
          JOIN pg_class c ON c.oid = i.inhrelid
    CROSS JOIN LATERAL (
                SELECT regexp_match(
                           pg_get_expr(c.relpartbound, c.oid),
                           'FROM \(''([^'']+)''\) TO \(''([^'']+)''\)') AS bounds
               ) m
         WHERE i.inhparent = p_parent
           AND m.bounds IS NOT NULL
           AND p_at >= (m.bounds[1])::timestamptz
           AND p_at <  (m.bounds[2])::timestamptz
    );
$$;

COMMENT ON FUNCTION range_partition_exists(regclass, text, timestamptz) IS
    'True when a range partition covering p_at exists. Reads catalogue bounds, so it is independent of how partitions were created.';

-- ---------------------------------------------------------------------------------------------
-- Ownership and grants.
--
-- The owner matters more than usual: a SECURITY DEFINER function runs with its OWNER's
-- privileges. If the migration is applied by a superuser and the owner is left alone, this
-- function would execute as that superuser - a privilege escalation handed to every role that can
-- call it. Pinning the owner to app_migrator caps it at "may create tables in public".
-- ---------------------------------------------------------------------------------------------
ALTER FUNCTION ensure_range_partitions(regclass, text, integer, timestamptz) OWNER TO app_migrator;
ALTER FUNCTION range_partition_exists(regclass, text, timestamptz) OWNER TO app_migrator;

-- EXECUTE is granted to PUBLIC by default on new functions. Revoke first, then grant by name.
REVOKE ALL ON FUNCTION ensure_range_partitions(regclass, text, integer, timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION range_partition_exists(regclass, text, timestamptz) FROM PUBLIC;

-- Only the workers create partitions. The APIs must never be able to run DDL, even indirectly.
GRANT EXECUTE ON FUNCTION ensure_range_partitions(regclass, text, integer, timestamptz) TO app_generation;
GRANT EXECUTE ON FUNCTION ensure_range_partitions(regclass, text, integer, timestamptz) TO app_retention;

-- Every service runs the partitions-ready readiness check, so every service may ask the question.
GRANT EXECUTE ON FUNCTION range_partition_exists(regclass, text, timestamptz) TO app_delivery;
GRANT EXECUTE ON FUNCTION range_partition_exists(regclass, text, timestamptz) TO app_download;
GRANT EXECUTE ON FUNCTION range_partition_exists(regclass, text, timestamptz) TO app_generation;
GRANT EXECUTE ON FUNCTION range_partition_exists(regclass, text, timestamptz) TO app_retention;
