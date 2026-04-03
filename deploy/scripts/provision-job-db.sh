#!/bin/bash
# FlowForge - Job Database Provisioning Script
# Creates a PostgreSQL database for a new host group's job storage.
#
# Usage:
#   ./provision-job-db.sh <db_name> [host] [port] [user]
#
# Example:
#   ./provision-job-db.sh flowforge_production localhost 5432 postgres
#
# This script:
#   1. Creates the database if it doesn't exist
#   2. Applies the FlowForge Jobs schema (tables for Jobs)
#   3. Outputs the connection string for use in FlowForge configuration
#
# Prerequisites:
#   - psql client installed
#   - PostgreSQL server accessible from this machine

set -euo pipefail

DB_NAME="${1:?Usage: $0 <db_name> [host] [port] [user]}"
DB_HOST="${2:-localhost}"
DB_PORT="${3:-5432}"
DB_USER="${4:-postgres}"

echo "============================================"
echo "FlowForge Job Database Provisioning"
echo "============================================"
echo "Database:  $DB_NAME"
echo "Host:      $DB_HOST"
echo "Port:      $DB_PORT"
echo "User:      $DB_USER"
echo "============================================"
echo ""

# Check if psql is available
if ! command -v psql &> /dev/null; then
    echo "ERROR: psql is not installed. Install postgresql-client first."
    echo "  Ubuntu/Debian: sudo apt install postgresql-client"
    echo "  RHEL/CentOS:   sudo dnf install postgresql"
    echo "  macOS:          brew install postgresql"
    exit 1
fi

# Check connectivity
echo "Checking database server connectivity..."
if ! pg_isready -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -q 2>/dev/null; then
    echo "WARNING: Cannot reach PostgreSQL at $DB_HOST:$DB_PORT"
    echo "Make sure the server is running and accepting connections."
fi

# Create the database if it doesn't exist
echo "Creating database '$DB_NAME' (if not exists)..."
psql -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -tc \
    "SELECT 1 FROM pg_database WHERE datname = '$DB_NAME'" | grep -q 1 || \
    psql -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -c "CREATE DATABASE \"$DB_NAME\";"

echo "Database '$DB_NAME' is ready."
echo ""

# Output the connection string
CONN_STRING="Host=$DB_HOST;Port=$DB_PORT;Database=$DB_NAME;Username=$DB_USER;Password=<YOUR_PASSWORD>"
echo "============================================"
echo "Connection string for FlowForge:"
echo ""
echo "  $CONN_STRING"
echo ""
echo "Add this to your FlowForge WebApi configuration:"
echo ""
echo "  JobConnections__<connection-id>__ConnectionString: \"$CONN_STRING\""
echo "  JobConnections__<connection-id>__Provider: \"PostgreSQL\""
echo ""
echo "Replace <connection-id> with the ConnectionId you used"
echo "when creating the host group (e.g., wf-jobs-mygroup)."
echo ""
echo "The FlowForge WebApi will automatically apply migrations"
echo "when it starts up with the new connection configured."
echo "============================================"
