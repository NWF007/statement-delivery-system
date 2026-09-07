#!/bin/sh
# Runs once, at first initialisation of the primary's data directory (docker-entrypoint-initdb.d).
#
# The image's default pg_hba.conf allows replication connections from localhost only, so the
# opt-in replica (docker compose --profile replica up -d postgres-replica) could never take its
# base backup: "no pg_hba.conf entry for replication connection". This admits replication from
# the compose network, with the same SCRAM authentication as every other connection. A data
# directory created before this script existed does not have the line; `docker compose down -v`
# recreates it.
set -e
echo "host replication all samenet scram-sha-256" >> "$PGDATA/pg_hba.conf"
