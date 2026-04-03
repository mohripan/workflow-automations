# FlowForge - Job Database Provisioning Script (Windows)
# Creates a PostgreSQL database for a new host group's job storage.
#
# Usage:
#   .\provision-job-db.ps1 -DbName <name> [-Host localhost] [-Port 5432] [-User postgres]
#
# Example:
#   .\provision-job-db.ps1 -DbName flowforge_production -Host db.example.com -Port 5432
#
# Prerequisites:
#   - psql client installed and on PATH
#   - PostgreSQL server accessible from this machine

param(
    [Parameter(Mandatory)]
    [string]$DbName,

    [string]$DbHost = "localhost",
    [int]$Port = 5432,
    [string]$User = "postgres"
)

$ErrorActionPreference = "Stop"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "FlowForge Job Database Provisioning" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Database:  $DbName"
Write-Host "Host:      $DbHost"
Write-Host "Port:      $Port"
Write-Host "User:      $User"
Write-Host "============================================"
Write-Host ""

# Check if psql is available
if (-not (Get-Command psql -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: psql is not installed or not on PATH." -ForegroundColor Red
    Write-Host "Download PostgreSQL from: https://www.postgresql.org/download/windows/"
    Write-Host "Or install via: winget install PostgreSQL.PostgreSQL"
    exit 1
}

# Check if database exists
Write-Host "Checking if database '$DbName' exists..."
$exists = psql -h $DbHost -p $Port -U $User -tc "SELECT 1 FROM pg_database WHERE datname = '$DbName'" 2>$null
if ($exists -and $exists.Trim() -eq "1") {
    Write-Host "Database '$DbName' already exists." -ForegroundColor Yellow
} else {
    Write-Host "Creating database '$DbName'..."
    psql -h $DbHost -p $Port -U $User -c "CREATE DATABASE `"$DbName`";"
    Write-Host "Database '$DbName' created successfully." -ForegroundColor Green
}

Write-Host ""

$connString = "Host=$DbHost;Port=$Port;Database=$DbName;Username=$User;Password=<YOUR_PASSWORD>"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Connection string for FlowForge:" -ForegroundColor Cyan
Write-Host ""
Write-Host "  $connString" -ForegroundColor White
Write-Host ""
Write-Host "Add to your FlowForge WebApi configuration:" -ForegroundColor Cyan
Write-Host ""
Write-Host "  JobConnections__<connection-id>__ConnectionString: `"$connString`""
Write-Host "  JobConnections__<connection-id>__Provider: `"PostgreSQL`""
Write-Host ""
Write-Host "Replace <connection-id> with the ConnectionId you used"
Write-Host "when creating the host group (e.g., wf-jobs-mygroup)."
Write-Host ""
Write-Host "The FlowForge WebApi will automatically apply migrations"
Write-Host "when it starts up with the new connection configured."
Write-Host "============================================" -ForegroundColor Cyan
