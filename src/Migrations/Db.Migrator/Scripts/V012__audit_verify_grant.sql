-- =============================================================================================
-- V012  Let Delivery.Api read the audit trail, so the verification endpoint can exist.
--
-- WHAT THIS CHANGES, STATED PLAINLY: before this script, app_delivery could INSERT into
-- audit_event and nothing else. V009 gave SELECT to app_retention alone. GET /v1/audit/verify
-- lives on Delivery.Api - it is the authenticated, staff-facing service, and a verification
-- endpoint has to run somewhere a human can reach - so that service now needs to read what it has
-- been writing.
--
-- WHAT THIS DOES NOT CHANGE, WHICH IS THE PART THAT MATTERS:
--
--   * NO ROLE GAINS UPDATE OR DELETE. Not this one, not any. The audit trail's integrity property
--     is that it is append-only, and that property is untouched here. Reading a chain cannot
--     rewrite it; the hash chain plus the V007 triggers plus the absent UPDATE grant are what make
--     tampering detectable, and all three still hold.
--   * app_download gains nothing. The internet-facing service still cannot read the audit trail,
--     which is where the confidentiality exposure would actually be.
--
-- WHAT IT COSTS: a compromise of Delivery.Api can now read access history - which customer
-- downloaded which statement, from which address - across all customers, rather than only append
-- to it. That is a real widening and it is the price of having the verification endpoint at all.
-- It is bounded at the HTTP layer by the `audit.verify` scope (DeliveryApiExtensions.StaffPolicy),
-- which is a separate control from this grant and does not replace it.
--
-- THE ALTERNATIVE, AND WHY NOT: a dedicated read-only role with its own connection string, used
-- only by the verifier. That is strictly better and it needs IDbConnectionFactory to become
-- role-aware as well as intent-aware, which is a change to every service's connection handling for
-- one endpoint. Worth doing when a second read-only consumer appears; not worth doing for this one.
-- =============================================================================================

GRANT SELECT ON audit_event TO app_delivery;

-- The chain heads too: verification starts from a chain-specific genesis and needs to know how many
-- chains exist. SELECT was already granted in V009 alongside UPDATE, so this is a no-op that
-- documents the dependency rather than a new privilege.
GRANT SELECT ON audit_chain_head TO app_delivery;

-- Re-asserted, not merely inherited. This script widens a grant, and the line that must survive it
-- is the one saying the trail cannot be rewritten. Stating it here means a future reader of V012
-- sees the constraint in the same file as the exception to it.
REVOKE UPDATE, DELETE, TRUNCATE ON audit_event FROM PUBLIC;
REVOKE UPDATE, DELETE, TRUNCATE ON audit_event FROM app_delivery, app_download, app_generation, app_retention;
