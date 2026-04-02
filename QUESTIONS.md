# Open Questions for Host Management Feature

These are decisions/clarifications I'd like your input on. Everything listed here works with sensible defaults for now, so nothing is blocking.

---

## 1. Job Database Provisioning
Currently, each host group points to a separate PostgreSQL database for job storage (e.g., `wf-jobs-minion`, `wf-jobs-titan`). When a user creates a **new** host group via the UI, they provide a `ConnectionId` — but the actual database must already exist.

**Question:** Should we auto-provision the job database when a host group is created? Or is the current approach (admin pre-creates the DB, then references it) acceptable?

---

## 2. Registration Token Rotation Policy
Right now, generating a new token for a host group **replaces** the old one (any agent using the old token can no longer re-register). There's no expiry — tokens are valid until explicitly revoked or regenerated.

**Question:** Should tokens have a TTL (e.g., 24h, 7d)? Should we support multiple active tokens per group?

---

## 3. Remote Agent Network Requirements
For an agent running on a remote machine (e.g., EC2 instance) to work, it needs direct network access to:
- **Redis** (for heartbeats and job streams)
- **Job PostgreSQL** (for reading/writing job data)
- **Platform PostgreSQL** (for self-registration at startup)

This means these services would need to be exposed beyond localhost.

**Question:** Is exposing Redis/PostgreSQL to the network acceptable for your use case? Or would you prefer a gateway/tunnel approach (more secure but more complex)?

---

## 4. Agent Identity & Security
Currently, agents identify themselves by hostname. Two agents with the same name would conflict. The registration token authenticates the initial join, but after that, there's no ongoing auth between the agent and the platform (Redis/PG connections use their own credentials).

**Question:** Should we add per-agent API keys for ongoing authentication? Or is the current model (registration token for join, then Redis/PG credentials for runtime) sufficient?

---

## 5. Docker Compose Startup Order
The WorkflowHost containers attempt self-registration on startup, but they may start before WebApi applies database migrations. I added retry logic, but on a fresh `docker compose up`, the first attempt fails and succeeds after restart.

**Question:** Should I add a `depends_on` with health check for WebApi in the compose file? Or is the current retry-on-restart behavior acceptable?

---

## 6. Host Group Deletion
Currently there's no API endpoint to delete a host group. Deleting a group with active hosts and running jobs has implications (orphaned jobs, disconnected agents).

**Question:** Should we support host group deletion? If yes, what should happen to running jobs and registered hosts?

---

## 7. Frontend Scope
The HostGroups page now shows:
- Token generation (copy-once modal)
- Host list with online/offline status (auto-refreshes every 15s)
- Manual host creation and removal
- Setup instructions (Docker, Linux systemd, Windows service)

**Question:** Anything else you'd like on this page? E.g., host detail view, job history per host, host resource usage?

---

## 8. Keycloak User Setup
The Keycloak realm export (`flowforge-realm.json`) defines roles but **no users**. During testing, I created an `admin` user via the Keycloak admin API. 

**Question:** Should I add a default admin user to the realm export so it works out of the box on `docker compose up`? (Current credentials I used: `admin` / `admin123` with the `admin` realm role)

---

*These are non-blocking — the feature works end-to-end as-is. Just wanted your input on the direction for these areas.*
