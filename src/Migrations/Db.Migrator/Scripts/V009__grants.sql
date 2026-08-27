-- =============================================================================================
-- V009  Grants on the business tables.
--
-- The matrix V001 recorded, now applied. Every grant is explicit, to a named role, on a named
-- object; PUBLIC gets nothing anywhere.
--
--   ROLE            statement                  audit_event   legal_hold              customer_key
--   ------------------------------------------------------------------------------------------
--   app_delivery    SELECT                     INSERT        SELECT                  SELECT
--   app_download    SELECT                     INSERT        SELECT                  SELECT
--   app_generation  SELECT, INSERT, UPDATE     INSERT        -                       SELECT
--   app_retention   SELECT, UPDATE, DELETE     INSERT        SELECT, INSERT, UPDATE  SELECT, UPDATE
--
-- app_retention is THE ONLY ROLE ANYWHERE WITH DELETE. That is the entire reason it is a separate
-- deployable with a separate identity and a separate approval path: the code that destroys data
-- does not share a process, or a credential, with the code that serves it.
--
-- NO ROLE HAS UPDATE OR DELETE ON audit_event. Not one. Combined with the triggers in V007, an
-- attacker must defeat both a trigger and a grant, using two different credentials.
-- =============================================================================================

-- ---------------------------------------------------------------------------------------------
-- Reference data. Every service reads customers and accounts; nobody but the ingestion path
-- writes them.
-- ---------------------------------------------------------------------------------------------
GRANT SELECT ON customer TO app_delivery, app_download, app_generation, app_retention;
GRANT SELECT ON account  TO app_delivery, app_download, app_generation, app_retention;

-- TODO(scaffold): app_generation holds INSERT and UPDATE on customer and account only because it
-- stands in for a customer-ingestion service that does not exist yet, and because the seed tool
-- connects as this role. When ingestion becomes its own deployable it gets its own role
-- (app_ingestion) and these two grants are revoked. Until then, note that the generation fleet can
-- create customers, which it has no business doing.
GRANT INSERT, UPDATE ON customer TO app_generation;
GRANT INSERT, UPDATE ON account  TO app_generation;

-- ---------------------------------------------------------------------------------------------
-- statement
-- ---------------------------------------------------------------------------------------------
-- The customer-facing API reads and never writes. It cannot create a statement, cannot change one,
-- and cannot delete one - so a compromised delivery credential can leak data but cannot destroy or
-- fabricate it.
GRANT SELECT ON statement TO app_delivery;
GRANT SELECT ON statement TO app_download;

-- The generation fleet creates statements and moves them through their lifecycle. No DELETE:
-- a failed render is marked FAILED, never removed, because a missing row and a never-scheduled
-- statement are indistinguishable afterwards.
GRANT SELECT, INSERT, UPDATE ON statement TO app_generation;

-- The only DELETE in the system.
GRANT SELECT, UPDATE, DELETE ON statement TO app_retention;

-- ---------------------------------------------------------------------------------------------
-- audit_event - INSERT ONLY, for everyone, forever.
-- ---------------------------------------------------------------------------------------------
GRANT INSERT ON audit_event TO app_delivery, app_download, app_generation, app_retention;

-- SELECT is granted separately and narrowly: the verifier and investigators need to read the
-- trail, and the services that write it do not need to read it back.
GRANT SELECT ON audit_event TO app_retention;

-- Belt and braces alongside the triggers in V007. Revoking from PUBLIC covers any role added later
-- by someone who did not read this file.
REVOKE UPDATE, DELETE, TRUNCATE ON audit_event FROM PUBLIC;
REVOKE UPDATE, DELETE, TRUNCATE ON audit_event FROM app_delivery, app_download, app_generation, app_retention;

-- ---------------------------------------------------------------------------------------------
-- audit_chain_head
--
-- THE RESIDUAL RISK, STATED PLAINLY. Appending to a hash chain means reading the head under
-- FOR UPDATE and advancing it, so every role that writes audit events necessarily holds SELECT and
-- UPDATE here. A compromised service credential can therefore corrupt a chain head.
--
-- What it CANNOT do is forge a consistent history: audit_event remains insert-only under both a
-- trigger and a revoked grant, so a rewritten head produces a chain that fails verification rather
-- than one that lies convincingly. The damage is detectable, which is the property being bought.
--
-- Closing this properly means a SECURITY DEFINER audit_append() function, so no service role holds
-- direct rights on this table at all. Deferred, and tracked in
-- docs/adr/0010-sharded-audit-hash-chains.md alongside the external-anchoring gap.
-- ---------------------------------------------------------------------------------------------
GRANT SELECT, UPDATE ON audit_chain_head TO app_delivery, app_download, app_generation, app_retention;
REVOKE INSERT, DELETE, TRUNCATE ON audit_chain_head FROM PUBLIC;
REVOKE INSERT, DELETE, TRUNCATE ON audit_chain_head FROM app_delivery, app_download, app_generation, app_retention;

-- ---------------------------------------------------------------------------------------------
-- legal_hold
-- ---------------------------------------------------------------------------------------------
-- The read paths need to know a hold exists so they can explain why something is still present.
GRANT SELECT ON legal_hold TO app_delivery, app_download;

-- Retention places, inspects and releases holds. It does not delete them: a released hold is the
-- record that data was preserved, by whom, and for how long.
GRANT SELECT, INSERT, UPDATE ON legal_hold TO app_retention;

-- app_generation gets nothing. Rendering a statement has no business knowing about litigation.

-- ---------------------------------------------------------------------------------------------
-- customer_key
-- ---------------------------------------------------------------------------------------------
-- Everyone that decrypts needs to resolve the key identifier. Nobody but retention changes it, and
-- nobody at all deletes it - the row survives destruction as the record that erasure happened.
GRANT SELECT ON customer_key TO app_delivery, app_download, app_generation;
GRANT SELECT, UPDATE ON customer_key TO app_retention;

REVOKE DELETE, TRUNCATE ON customer_key FROM PUBLIC;
REVOKE DELETE, TRUNCATE ON customer_key FROM app_delivery, app_download, app_generation, app_retention;

-- ---------------------------------------------------------------------------------------------
-- Sequences: there are none, by design. Every key is an application-supplied UUIDv7, so there is
-- no sequence to grant USAGE on and no way for a role to read another role's next value.
-- ---------------------------------------------------------------------------------------------
