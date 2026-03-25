-- Creates the Keycloak database on the shared platform PostgreSQL instance.
-- This script runs automatically on first container start via docker-entrypoint-initdb.d.
CREATE DATABASE keycloak;
