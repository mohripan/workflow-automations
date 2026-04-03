# Host Groups & Agent Deployment Guide

## Overview

FlowForge uses **Host Groups** to organize and manage pools of **Agents** (WorkflowHost instances) that execute jobs. Each host group has its own dedicated job database for isolation, and one or more agents that pull and execute work.

This model works like self-hosted CI/CD runners (GitHub Actions runners, GitLab Runners, Azure DevOps agents):

1. **Admin** creates a host group and generates a **registration token**
2. **Admin** deploys a FlowForge Agent on any machine (server, VM, container, laptop)
3. **Agent** uses the token to self-register with the platform
4. **Platform** routes jobs to online agents via round-robin load balancing

```
┌─────────────────────────────────────────────────────────────────┐
│  FlowForge Platform                                             │
│  ┌──────────┐  ┌──────────────────┐  ┌──────────────────────┐  │
│  │  WebApi   │  │  JobOrchestrator │  │  JobAutomator        │  │
│  │(REST/WS)  │  │  (routes jobs)   │  │  (trigger eval)      │  │
│  └─────┬─────┘  └────────┬─────────┘  └──────────────────────┘  │
│        │                 │                                       │
│        │    Redis Streams + Heartbeats                           │
│        │                 │                                       │
│  ┌─────┴─────────────────┴───────────────────────────────┐      │
│  │  Host Group: "production" (wf-jobs-prod)              │      │
│  │  ┌──────────┐  ┌──────────┐  ┌──────────┐            │      │
│  │  │ Agent A  │  │ Agent B  │  │ Agent C  │  ...       │      │
│  │  │ (EC2)    │  │ (Azure)  │  │ (Docker) │            │      │
│  │  └──────────┘  └──────────┘  └──────────┘            │      │
│  └───────────────────────────────────────────────────────┘      │
│                                                                  │
│  ┌───────────────────────────────────────────────────────┐      │
│  │  Host Group: "dev" (wf-jobs-dev)                      │      │
│  │  ┌──────────┐                                         │      │
│  │  │ Agent D  │  (your laptop)                          │      │
│  │  └──────────┘                                         │      │
│  └───────────────────────────────────────────────────────┘      │
└─────────────────────────────────────────────────────────────────┘
```

---

## Concepts

### Host Group

A **Host Group** is a logical pool of agents that share:
- A **name** (e.g., "production", "staging", "dev")
- A **Connection ID** that maps to a dedicated PostgreSQL job database
- One or more **registration tokens** for agent enrollment (each with its own TTL)

When you create an automation, you choose which host group runs its jobs.

### Agent (WorkflowHost)

An **Agent** is an instance of the FlowForge WorkflowHost service running on any machine. It:
- Registers itself with a host group on startup
- Sends heartbeats every 10 seconds (30-second TTL)
- Listens for job assignments on its own Redis stream
- Spawns a WorkflowEngine process for each job
- Reports job status back through Redis streams

### Registration Tokens (Multi-Token System)

Registration tokens authorize agents to join a specific host group. FlowForge supports **multiple active tokens per host group**, each with its own TTL and label:

- **Multiple tokens** — Generate separate tokens for different environments, teams, or machines
- **TTL** — Every token has an expiration time (default: 24 hours, configurable up to 1 year)
- **Labels** — Optional descriptive labels (e.g., "ec2-prod", "dev-laptop", "ci-runner")
- **SHA-256 hashed** — Only the hash is stored; the raw token is shown once on generation
- **Individual revocation** — Revoke specific tokens without affecting others
- **Expired tokens** — Automatically ignored during validation; visible in the UI for audit

---

## Quick Start

### 1. Create a Host Group

**Via UI:**
1. Navigate to **Host Groups** page
2. Click **New Group**
3. Enter a name and Connection ID (e.g., `wf-jobs-prod`)
4. Click **Create**

**Via API:**
```bash
curl -X POST http://localhost:8080/api/host-groups \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name": "production", "connectionId": "wf-jobs-prod"}'
```

### 2. Set Up the Job Database

Each host group needs its own PostgreSQL database for job isolation. You can either use the provided provisioning scripts or set it up manually.

**Option A: Provisioning Scripts (Recommended)**

```bash
# Linux / macOS
chmod +x deploy/scripts/provision-job-db.sh
./deploy/scripts/provision-job-db.sh \
  --host your-db-host \
  --port 5432 \
  --user postgres \
  --password postgres \
  --database flowforge_prod
```

```powershell
# Windows
.\deploy\scripts\provision-job-db.ps1 `
  -Host your-db-host `
  -Port 5432 `
  -User postgres `
  -Password postgres `
  -Database flowforge_prod
```

**Option B: Manual Setup**

```bash
# Create the database
psql -h your-db-host -U postgres -c "CREATE DATABASE flowforge_prod;"

# Run migrations
dotnet ef database update \
  --project src/shared/FlowForge.Infrastructure \
  --startup-project src/services/FlowForge.WebApi \
  --context JobDbContext \
  --connection "Host=your-db-host;Database=flowforge_prod;Username=postgres;Password=postgres"
```

Then add the connection string to all services that need it (WebApi, JobOrchestrator, WorkflowHost agents):

```json
{
  "JobConnections": {
    "wf-jobs-prod": {
      "ConnectionString": "Host=your-db-host;Database=flowforge_prod;Username=postgres;Password=postgres",
      "Provider": "PostgreSQL"
    }
  }
}
```

### 3. Generate a Registration Token

**Via UI:**
1. Open the host group card → switch to the **Tokens** tab
2. Click **Generate Token**
3. Enter a label (optional) and TTL (default: 24 hours)
4. Copy the token immediately — **it will only be shown once**

**Via API:**
```bash
curl -X POST http://localhost:8080/api/host-groups/{groupId}/registration-token \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"label": "ec2-production", "expiresInHours": 168}'
# Response: { "token": "base64-encoded-token", "id": "uuid", "expiresAt": "...", "label": "ec2-production" }
```

### 4. Deploy an Agent

See the [Deployment Options](#deployment-options) section below for detailed instructions.

---

## Deployment Options

### Docker Container

The simplest way to deploy an agent:

```bash
docker run -d \
  --name flowforge-agent-01 \
  -e NODE_NAME="agent-01" \
  -e REGISTRATION_TOKEN="<your-registration-token>" \
  -e "ConnectionStrings__DefaultConnection=Host=<PLATFORM_DB>;Database=flowforge_platform;Username=postgres;Password=postgres" \
  -e "JobConnections__<CONNECTION_ID>__ConnectionString=Host=<JOB_DB>;Database=<JOB_DB_NAME>;Username=postgres;Password=postgres" \
  -e "JobConnections__<CONNECTION_ID>__Provider=PostgreSQL" \
  -e "Redis__ConnectionString=<REDIS_HOST>:6379" \
  flowforge-workflowhost:latest
```

Replace:
- `<your-registration-token>` — from step 3
- `<PLATFORM_DB>` — platform PostgreSQL host
- `<JOB_DB>` / `<JOB_DB_NAME>` — job database host and name
- `<CONNECTION_ID>` — the host group's Connection ID (e.g., `wf-jobs-prod`)
- `<REDIS_HOST>` — Redis host

### Linux (systemd Service)

1. **Install .NET 10 runtime:**
   ```bash
   # Ubuntu/Debian
   sudo apt-get install -y dotnet-runtime-10.0
   ```

2. **Download or build the agent:**
   ```bash
   # Option A: Build from source
   dotnet publish src/services/FlowForge.WorkflowHost -c Release -o /opt/flowforge-agent

   # Option B: Copy a published build
   scp -r publish/* user@host:/opt/flowforge-agent/
   ```

3. **Create a systemd service:**
   ```ini
   # /etc/systemd/system/flowforge-agent.service
   [Unit]
   Description=FlowForge Agent
   After=network.target

   [Service]
   Type=simple
   User=flowforge
   WorkingDirectory=/opt/flowforge-agent
   ExecStart=/usr/bin/dotnet /opt/flowforge-agent/FlowForge.WorkflowHost.dll
   Restart=always
   RestartSec=10

   Environment=NODE_NAME=agent-linux-01
   Environment=REGISTRATION_TOKEN=<your-token>
   Environment=ConnectionStrings__DefaultConnection=Host=<PLATFORM_DB>;Database=flowforge_platform;Username=postgres;Password=postgres
   Environment=JobConnections__<CONN_ID>__ConnectionString=Host=<JOB_DB>;Database=<JOB_DB_NAME>;Username=postgres;Password=postgres
   Environment=JobConnections__<CONN_ID>__Provider=PostgreSQL
   Environment=Redis__ConnectionString=<REDIS_HOST>:6379

   [Install]
   WantedBy=multi-user.target
   ```

4. **Enable and start:**
   ```bash
   sudo systemctl daemon-reload
   sudo systemctl enable flowforge-agent
   sudo systemctl start flowforge-agent
   sudo journalctl -u flowforge-agent -f  # watch logs
   ```

### Windows Service

1. **Install .NET 10 runtime** from https://dotnet.microsoft.com/download

2. **Publish the agent:**
   ```powershell
   dotnet publish src\services\FlowForge.WorkflowHost -c Release -o C:\FlowForge\Agent
   ```

3. **Create a Windows service:**
   ```powershell
   # Set environment variables
   [Environment]::SetEnvironmentVariable("NODE_NAME", "agent-win-01", "Machine")
   [Environment]::SetEnvironmentVariable("REGISTRATION_TOKEN", "<your-token>", "Machine")
   # ... set other env vars similarly ...

   # Create and start the service
   New-Service -Name "FlowForgeAgent" `
     -BinaryPathName "C:\FlowForge\Agent\FlowForge.WorkflowHost.exe" `
     -DisplayName "FlowForge Agent" `
     -StartupType Automatic

   Start-Service FlowForgeAgent
   ```

### Docker Compose (Multiple Agents)

Add to your existing `compose.yaml`:

```yaml
workflowhost-custom:
  build:
    context: ../..
    dockerfile: src/services/FlowForge.WorkflowHost/Dockerfile
  container_name: flowforge-workflowhost-custom
  environment:
    ASPNETCORE_ENVIRONMENT: Development
    NODE_NAME: "custom-agent"
    HOST_CONNECTION_ID: "wf-jobs-custom"  # or use REGISTRATION_TOKEN
    WorkflowHost__EnginePath: "/app/engine/FlowForge.WorkflowEngine.dll"
    ConnectionStrings__DefaultConnection: "Host=postgres-platform;..."
    JobConnections__wf-jobs-custom__ConnectionString: "Host=postgres-custom;..."
    JobConnections__wf-jobs-custom__Provider: "PostgreSQL"
    Redis__ConnectionString: "redis:6379"
  depends_on:
    postgres-platform:
      condition: service_healthy
    redis:
      condition: service_healthy
```

### Cloud VMs (EC2, Azure VM, GCP)

1. Provision a VM with network access to:
   - Platform PostgreSQL database
   - Job PostgreSQL database
   - Redis server
2. Follow the **Linux** or **Windows** instructions above
3. Configure security groups/firewall to allow outbound traffic to the database and Redis ports

---

## Agent Registration Modes

The agent supports three registration modes, checked in order:

| Mode | Env Variable | Use Case |
|------|-------------|----------|
| **Direct** | `HOST_GROUP_ID=<uuid>` | Trusted network, known group ID |
| **Token** | `REGISTRATION_TOKEN=<token>` | Remote agents, secure enrollment |
| **Connection ID** | `HOST_CONNECTION_ID=<conn-id>` | Docker compose, matches by Connection ID |

**How it works on startup:**
1. Agent reads `NODE_NAME` (or falls back to machine hostname)
2. Checks if a host record already exists in platform DB (by name)
3. If not found, resolves the host group using the mode above
4. Creates a `WorkflowHost` record in the platform DB
5. Begins heartbeat loop and job consumption
6. Retries up to 5 times with exponential backoff if registration fails

---

## Host Group Deletion

Host group deletion is a **destructive, irreversible operation** that requires explicit confirmation (similar to AWS resource termination):

### How It Works

1. **Type-to-confirm** — You must type the exact host group name to proceed
2. **Cascade delete** — All hosts in the group are removed first, then the group itself
3. **Audit logged** — The deletion is recorded with the username, timestamp, and number of hosts deleted
4. **Job disruption** — Active jobs in the group will be orphaned (no hosts to execute them)

### Via UI
1. Navigate to the host group detail page
2. Click **Delete Group**
3. Type the exact group name in the confirmation dialog
4. Click **Delete Host Group**

### Via API
```bash
curl -X DELETE http://localhost:8080/api/host-groups/{groupId} \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"confirmName": "exact-group-name"}'
```

---

## Audit Logging

All host group operations are recorded in the audit log for compliance and troubleshooting:

| Action | Description |
|--------|-------------|
| `HostGroup.Created` | Host group created with name and connection ID |
| `HostGroup.Deleted` | Host group deleted, including count of hosts removed |
| `RegistrationToken.Generated` | Token generated with label and TTL |
| `RegistrationToken.Revoked` | Specific token revoked by ID |
| `Host.Added` | Host manually added to a group |
| `Host.Removed` | Host removed from a group |

### Viewing Activity

**Via UI:** Open a host group → switch to the **Activity** tab (shows last 50 events)

**Via API:**
```bash
curl http://localhost:8080/api/host-groups/{groupId}/activity \
  -H "Authorization: Bearer $TOKEN"
```

Each entry includes: action, detail message, username, and timestamp.

---

## API Reference

### Host Groups

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `GET` | `/api/host-groups` | ViewerOrAbove | List all host groups |
| `GET` | `/api/host-groups/{id}` | ViewerOrAbove | Get host group by ID |
| `POST` | `/api/host-groups` | AdminOnly | Create a new host group |
| `DELETE` | `/api/host-groups/{id}` | AdminOnly | Delete host group (requires `confirmName` in body) |
| `GET` | `/api/host-groups/{id}/hosts` | ViewerOrAbove | List hosts in a group |
| `POST` | `/api/host-groups/{id}/hosts` | AdminOnly | Manually add a host entry |
| `DELETE` | `/api/host-groups/{id}/hosts/{hostId}` | AdminOnly | Remove a host |
| `GET` | `/api/host-groups/{id}/tokens` | ViewerOrAbove | List all tokens for a group |
| `POST` | `/api/host-groups/{id}/registration-token` | AdminOnly | Generate registration token (body: `{label?, expiresInHours?}`) |
| `DELETE` | `/api/host-groups/{id}/registration-token/{tokenId}` | AdminOnly | Revoke a specific token |
| `GET` | `/api/host-groups/{id}/activity` | ViewerOrAbove | Get audit log for a group (last 50) |

### Agent Registration

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `POST` | `/api/hosts/register` | AllowAnonymous (Token-based) | Self-register an agent |

**Request body:**
```json
{
  "token": "base64-registration-token",
  "hostName": "my-agent-01"
}
```

**Response:**
```json
{
  "hostId": "uuid",
  "hostName": "my-agent-01",
  "hostGroupId": "uuid",
  "hostGroupName": "production",
  "connectionId": "wf-jobs-prod"
}
```

---

## Frontend Features

### Host Groups List Page

The host groups list page (`/host-groups`) provides:
- **Card-based layout** with expandable sections per group
- **Tabbed interface** per group: Hosts, Tokens, Activity
- **Summary badges** showing online host count and active token count
- **Quick token generation** with label and TTL picker
- **Delete with confirmation** (type-to-confirm modal)
- **Link to detail page** for full management

### Host Group Detail Page

The host group detail page (`/host-groups/:id`) provides:
- **Summary cards** — total hosts, online count, active tokens, creation date
- **Hosts tab** — list of hosts with online/offline status, last heartbeat, expandable detail with job history per host and resource usage placeholder
- **Tokens tab** — active and expired tokens, generate new tokens, revoke individual tokens
- **Jobs tab** — all jobs in this host group with status, automation name, host assignment, creation time
- **Activity tab** — chronological audit log of all actions on this group

---

## Network Requirements

Agents need network access to three services:

| Service | Default Port | Purpose |
|---------|-------------|---------|
| **Platform PostgreSQL** | 5432 | Host registration, metadata |
| **Job PostgreSQL** | Varies | Read/write job data |
| **Redis** | 6379 | Heartbeats, job stream, event bus |

For cloud deployments, ensure:
- Security groups allow the agent's IP to connect to these ports
- Use TLS for Redis in production (`redis:6379,ssl=true`)
- Use SSL for PostgreSQL connections (`;SSL Mode=Require`)

---

## Monitoring & Troubleshooting

### Agent Health

Each agent exposes health endpoints:
- `GET /health/live` — Process is running (always 200)
- `GET /health/ready` — PostgreSQL + Redis connectivity

### Heartbeat System

- Agents publish `host:heartbeat:{name}` to Redis every **10 seconds** with a **30-second TTL**
- JobOrchestrator's `HeartbeatMonitorWorker` checks every **15 seconds**
- If the heartbeat key expires → host marked offline → no new jobs dispatched
- When heartbeat resumes → host marked online → jobs resume

### Common Issues

| Symptom | Cause | Fix |
|---------|-------|-----|
| Agent shows "Offline" | Heartbeat not reaching Redis | Check Redis connectivity |
| Agent registered but no jobs | HostGroupId mismatch | Verify Connection ID matches the automation's host group |
| "Could not self-register" log | Missing env var | Set `HOST_GROUP_ID`, `REGISTRATION_TOKEN`, or `HOST_CONNECTION_ID` |
| Jobs stuck in Pending | No online hosts in group | Check agent status, deploy agents |
| Registration token rejected | Token expired or revoked | Generate a new token via the Tokens tab |
| Agent fails on startup | Platform DB unreachable | Agent retries 5 times with exponential backoff; check connectivity |

### Logs

```bash
# Docker
docker logs flowforge-workflowhost-minion -f

# systemd
journalctl -u flowforge-agent -f

# Look for:
# "Host {name} registered to group {id}" — successful registration
# "Host {name} already registered" — idempotent re-registration
# "Host {name} could not self-register" — missing configuration
```

---

## Architecture Details

### Job Routing Flow

```
1. Automation triggered → AutomationTriggeredEvent
2. WebApi creates Job (status: Pending) in host group's job DB
3. WebApi publishes JobCreatedEvent → Redis stream
4. JobOrchestrator.JobDispatcherWorker:
   a. Reads job from DB
   b. Queries online hosts in the job's host group
   c. Selects host via round-robin
   d. Transitions job to Started, assigns HostId
   e. Publishes JobAssignedEvent to host-specific stream
5. Agent.JobConsumerWorker:
   a. Receives JobAssignedEvent from its stream
   b. Spawns WorkflowEngine child process
   c. Engine executes the job handler
   d. Publishes JobStatusChangedEvent on completion
```

### Database Isolation

Each host group has a separate job database:
```
flowforge_platform  ← Automations, HostGroups, WorkflowHosts, RegistrationTokens, Triggers, Outbox, AuditLogs
flowforge_minion    ← Jobs for host group "minion"
flowforge_titan     ← Jobs for host group "titan"
flowforge_prod      ← Jobs for host group "production"
```

This provides:
- Data isolation between environments
- Independent scaling and backup
- No cross-group query interference

### Multi-Token Architecture

```
HostGroup (1) ──── (*) RegistrationToken
                        ├── Id (GUID)
                        ├── HostGroupId (FK)
                        ├── TokenHash (SHA-256)
                        ├── Label (optional)
                        ├── ExpiresAt (UTC)
                        └── CreatedAt (UTC)
```

- Tokens are stored as SHA-256 hashes (raw token shown once on creation)
- Validation iterates all non-expired tokens for the group
- Expired tokens are retained for audit visibility but ignored during validation
- Revoking a token removes it permanently from the database
- Cascade delete: deleting a host group removes all its tokens

---

## Demo / Development Setup

### Default Credentials

The Docker Compose setup includes a demo admin user for testing:

| Service | Username | Password | URL |
|---------|----------|----------|-----|
| FlowForge UI | `demo_admin` | `webapi` | http://localhost:5173 |
| Keycloak Admin | `admin` | `admin` | http://localhost:9090 |

### Docker Compose Host Groups

The default `compose.yaml` includes two host groups:
- **minion** (`wf-jobs-minion`) — lightweight jobs
- **titan** (`wf-jobs-titan`) — heavy computation jobs

Each has a pre-configured WorkflowHost agent that auto-registers using `HOST_CONNECTION_ID`.

---

## Future Roadmap

### Planned Enhancements

- **WebSocket Gateway** — Allow agents behind NAT/firewalls to connect via WebSocket instead of requiring direct Redis/PostgreSQL access
- **SSH-based Remote Execution** — Execute jobs on remote hosts via SSH without deploying a full agent
- **On-Demand Container Provisioning** — Spin up ephemeral Docker containers per job, auto-destroy after completion
- **Automatic Database Provisioning** — Create job databases automatically when a new host group is created via the API
- **Agent Labels/Tags** — Route specific jobs to agents with matching capabilities (e.g., `gpu`, `linux`, `arm64`)
- **Agent Auto-Update** — Push new agent versions from the platform
- **Agent Telemetry** — Resource usage reporting (CPU, memory, disk) from agents to the platform
- **Per-Agent API Keys** — Unique API keys for each registered agent for enhanced security
