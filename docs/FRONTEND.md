# FRONTEND.md — FlowForge React UI

This document covers everything about the FlowForge web frontend: what it is, what it does, how to run it, how to log in, and how each major feature works.

---

## Overview

The FlowForge frontend is a **React 18 + TypeScript** single-page application (SPA) that provides a full management interface for the FlowForge workflow orchestration platform. It lives in `src/frontend/` and runs on **port 3000** by default.

It communicates exclusively with the **WebAPI** (`http://localhost:8080`) over REST, and uses **SignalR WebSockets** for real-time job status updates. Authentication is handled by **Keycloak** (`http://localhost:8180`) using the OpenID Connect Authorization Code + PKCE flow.

---

## Tech Stack

| Concern | Library |
|---|---|
| UI framework | React 18 + TypeScript |
| Build tool | Vite |
| Styling | Tailwind CSS (dark theme) |
| Routing | React Router v6 |
| Server state | TanStack Query v5 |
| Auth | `keycloak-js` (PKCE) |
| Real-time | `@microsoft/signalr` |
| Forms | React Hook Form + Zod |
| Code editor | Monaco Editor (`@monaco-editor/react`) |
| Icons | Lucide React |
| Notifications | react-hot-toast |
| HTTP client | Axios (with auth interceptor) |
| Dates | date-fns |

---

## Running the Frontend

### Option 1 — Local dev server (recommended for development)

> **Prerequisites:** Docker stack running (`docker compose up -d` from `deploy/docker/`), Node.js 20+

```bash
cd src/frontend
npm install
npm run dev
# → http://localhost:3000
```

Vite proxies `/api/*` and `/hubs/*` to `http://localhost:8080` so you never have to worry about CORS in development.

### Option 2 — Via Docker Compose (full stack)

The frontend is included in `deploy/docker/compose.yaml` as `flowforge-frontend`. It is built as a static Nginx image.

```bash
cd deploy/docker
docker compose up -d
# Frontend: http://localhost:3000
```

### Environment Variables

Copy `.env.example` to `.env.local` and adjust if needed:

```env
VITE_API_URL=http://localhost:8080
VITE_KEYCLOAK_URL=http://localhost:8180
VITE_DEV_MODE=false      # set to true to bypass Keycloak (mock admin user)
```

Setting `VITE_DEV_MODE=true` skips the Keycloak redirect entirely and injects a mock `admin` user — useful if you want to poke around the UI without setting up auth.

---

## Authentication

### How It Works

1. On first load, `keycloak-js` detects no active session and redirects to Keycloak's login page at `http://localhost:8180/realms/flowforge/protocol/openid-connect/auth`.
2. The user logs in with their credentials.
3. Keycloak redirects back to `http://localhost:3000` with an authorization code.
4. `keycloak-js` exchanges the code for an **access token + refresh token** using PKCE (`S256`).
5. The access token is attached as a `Bearer` header on every API request via an Axios interceptor.
6. The token is silently refreshed in the background before it expires.

The Keycloak client used is **`flowforge-frontend`** — a public client (no secret required, uses PKCE for security).

---

## Test Accounts

The FlowForge Keycloak realm ships with **no pre-seeded users**. You must create them manually through the Keycloak Admin Console after starting the stack.

### Step-by-Step: Create a Test User

1. **Start the stack:**
   ```bash
   cd deploy/docker
   docker compose up -d
   ```
   Wait until Keycloak is healthy (check with `docker ps` — the health status shows `(healthy)`).

2. **Open the Keycloak Admin Console:**
   ```
   http://localhost:8180/admin
   ```

3. **Log in with the Keycloak admin credentials:**
   | Field | Value |
   |---|---|
   | Username | `admin` |
   | Password | `admin` |

4. **Switch to the `flowforge` realm:**
   - In the top-left dropdown (shows "Keycloak master"), click and select **`flowforge`**.

5. **Create a new user:**
   - Go to **Users → Add user**
   - Fill in:
     - Username: `alice` (or any name)
     - Email: `alice@example.com`
     - First name / Last name: optional
   - Click **Save**

6. **Set a password:**
   - Go to the **Credentials** tab
   - Click **Set password**
   - Enter a password (e.g. `alice123`)
   - Toggle **Temporary** to **OFF** (so you're not forced to change it on first login)
   - Click **Save password**

7. **Assign a role:**
   - Go to the **Role mapping** tab
   - Click **Assign role**
   - Filter by realm roles and assign one of:
     - `admin` — full access (create/edit/delete automations, manage DLQ, host groups)
     - `operator` — create and manage automations and jobs, trigger webhooks
     - `viewer` — read-only access (browse automations, jobs, task types)
   - Click **Assign**

### Recommended Test Accounts

Create these three accounts to test all role scenarios:

| Username | Password | Role | What they can do |
|---|---|---|---|
| `admin` | `admin123` | `admin` | Everything: full CRUD, DLQ, host group management |
| `operator` | `op123` | `operator` | Create/edit automations, manage jobs, fire webhooks |
| `viewer` | `view123` | `viewer` | Browse and read everything, no write actions |

> ⚠️ **Note:** Don't confuse the FlowForge **application users** (above) with the **Keycloak admin user** (`admin/admin`). The Keycloak admin user is only used to manage the Keycloak console itself.

---

## Pages & Features

### Dashboard (`/dashboard`)

The home screen after login. Shows:
- **Stats cards**: Total automations, enabled automations, currently running jobs, jobs that errored today
- **Recent jobs table**: Last 20 jobs across all automations with status, automation name, and time elapsed
- **Quick action**: "New Automation" shortcut button

### Automations (`/automations`)

The main management screen for automations.

**List view:**
- Table showing all automations with name, enabled/disabled status badge, task type, trigger count, and last updated time
- Search bar to filter by name
- Enable / Disable toggle per row (operator/admin only)
- Click any row to go to the automation detail page

**Create Automation (`/automations/new`):**

A multi-step wizard:

| Step | What you configure |
|---|---|
| 1 — Basic Info | Name, description, host group (dropdown), enabled toggle |
| 2 — Task | Task type dropdown (from `GET /api/task-types`), dynamic parameter form or raw JSON editor |
| 3 — Triggers | Add one or more named triggers with type-specific config |
| 4 — Condition | AND/OR tree builder to combine triggers |
| 5 — Advanced | Timeout (seconds), max retries |
| Review | Summary before saving |

**Edit Automation (`/automations/:id/edit`):** Same wizard pre-populated with existing data.

### Automation Detail (`/automations/:id`)

Full detail view of a single automation:
- **Header**: name, status badge, Edit / Delete / Enable-Disable buttons
- **Info panel**: TaskId, HostGroup, timeout, max retries
- **Triggers panel**: all triggers listed with type badge, config preview, Add / Edit / Delete buttons, and a **Fire Webhook** button for webhook-type triggers
- **Condition tree**: read-only visual representation of the AND/OR tree. Edit button opens the condition builder
- **Task Config**: Monaco JSON editor showing the handler parameters
- **Recent Jobs**: mini table of the last 5 jobs for this automation with links to their detail pages

### Trigger Types & Config Forms

When adding or editing a trigger, the form is **dynamically generated** from the schema returned by `GET /api/triggers/types`. Each trigger type has its own fields:

| Trigger Type | Key Config Fields | Notes |
|---|---|---|
| `schedule` | Cron expression | Standard Quartz cron, e.g. `0 0 9 * * ?` |
| `sql` | Connection string, SQL query, polling interval | Connection string shown as `***` after save (encrypted at rest) |
| `webhook` | Optional secret hash | Hash of the shared secret; raw secret is never stored |
| `job-completed` | Target automation ID | Fires when another automation's job completes |
| `custom-script` | Python script body, interpreter, requirements.txt, timeout | Script edited in Monaco with Python syntax highlighting |

### Condition Tree Builder

A visual component for building the `ConditionRoot` logic tree that determines when an automation fires.

- **Leaf node**: a dropdown to select a trigger by name
- **AND / OR node**: a group with a toggle between AND / OR and a list of child nodes
- **Add child**: `+` button to add another leaf or group inside a composite node
- **Remove**: `×` button on any node

**Example**: automation fires when `daily-schedule` AND (`database-changed` OR `webhook-received`):
```
AND
├── daily-schedule
└── OR
    ├── database-changed
    └── webhook-received
```

### Jobs (`/jobs`)

**List view:**
- Columns: Automation name, status badge, host, retry attempt, created time, duration, actions
- Filter chips: All | Pending | Running | Completed | Error | Cancelled
- Search by automation name
- Pagination
- **Cancel** button on active jobs (Pending / Started / InProgress)

**Job Detail (`/jobs/:id`):**
- **Status timeline**: visual progress bar showing `Pending → Started → InProgress → [terminal]`
- **Real-time updates**: the page subscribes to SignalR group `job:{id}` via `JoinJobGroup`. Status, message, and output update live without refreshing
- **Metadata**: automation name, host, retry `N of M`, timeout
- **Task Config** accordion: Monaco JSON viewer (what the job was configured to do)
- **Output JSON** accordion: Monaco JSON viewer (what the job produced — shown once completed)
- **Message**: status message from the workflow engine (error details appear here)
- **Cancel** button (shown when job is still active)

### Host Groups (`/host-groups`)

Cards for each host group:
- Group name and ConnectionId
- List of WorkflowHost instances in the group with online/offline status indicator
- Host count badge

### DLQ — Dead Letter Queue (`/dlq`) — admin only

When a message fails to process after multiple attempts, it lands in the Dead Letter Queue. This page lets admins inspect and act on those failures.

- **Table**: source stream, error message, timestamp
- **Payload preview**: expandable JSON of the original message
- **Replay** (`POST /api/dlq/{id}/replay`): re-publishes the message back to its source stream. Use only after fixing the root cause.
- **Delete** (`DELETE /api/dlq/{id}`): removes the entry from the DLQ permanently

> ⚠️ **Warning**: Only replay a DLQ entry after resolving the underlying issue. Replaying without fixing the cause will re-fail and potentially cause duplicate side effects.

### Task Types (`/task-types`)

A read-only discovery page showing all registered workflow task handlers:

| Task ID | Display Name | Parameters |
|---|---|---|
| `send-email` | Send Email | `to`, `subject`, `body`, `smtpHost`, etc. |
| `http-request` | HTTP Request | `url`, `method`, `headers`, `body`, `timeoutSeconds` |
| `run-script` | Run Script | `script`, `interpreter`, `arguments`, `timeoutSeconds` |

This page is useful for knowing what `TaskId` to use when creating a new automation.

---

## Role-Based Access Control

The UI respects the user's Keycloak realm roles and hides/disables features accordingly:

| Feature | admin | operator | viewer |
|---|---|---|---|
| View automations & jobs | ✅ | ✅ | ✅ |
| Enable / Disable automation | ✅ | ✅ | ❌ |
| Create / Edit automation | ✅ | ✅ | ❌ |
| Delete automation | ✅ | ❌ | ❌ |
| Add / Edit / Delete trigger | ✅ | ✅ | ❌ |
| Fire webhook trigger | ✅ | ✅ | ❌ |
| Cancel job | ✅ | ✅ | ❌ |
| View host groups | ✅ | ✅ | ✅ |
| Create host group | ✅ | ❌ | ❌ |
| View DLQ | ✅ | ❌ | ❌ |
| Replay / Delete DLQ entry | ✅ | ❌ | ❌ |

---

## Real-Time Job Updates (SignalR)

The Job Detail page uses the SignalR hub at `ws://localhost:8080/hubs/job-status` to receive live status pushes.

**Protocol:**
1. Page loads → establishes SignalR connection with Bearer token
2. Calls `JoinJobGroup(jobId)` → subscribes to updates for that specific job
3. Backend pushes `OnJobStatusChanged(update)` whenever the job's status changes
4. Page updates the status badge, timeline, and output JSON in real time — no polling required
5. On page unmount → calls `LeaveJobGroup(jobId)` and stops the connection

**Update payload (`JobStatusUpdate`):**
```ts
{
  jobId: string;
  status: JobStatus;
  message?: string;
  outputJson?: string;
  updatedAt: string;
}
```

---

## Dev Mode (No Keycloak)

For rapid UI development without running the full Keycloak stack, set:

```env
VITE_DEV_MODE=true
```

This injects a mock user:
```ts
{ username: 'dev-admin', roles: ['admin'] }
```

All API calls still go to the real backend — only the authentication step is bypassed.

> ⚠️ Never enable `VITE_DEV_MODE` in a production or shared environment.

---

## Project Structure

```
src/frontend/
├── package.json
├── vite.config.ts           # dev proxy: /api → :8080, /hubs → :8080
├── tsconfig.json
├── tailwind.config.js
├── Dockerfile               # multi-stage: build → nginx:alpine
├── nginx.conf               # SPA fallback + /api proxy
├── .env.example
└── src/
    ├── main.tsx             # Keycloak init, QueryClient, Router bootstrap
    ├── App.tsx              # Route definitions
    ├── keycloak.ts          # keycloak-js instance (singleton)
    ├── api/
    │   ├── client.ts        # Axios instance; Bearer token interceptor; 401 → keycloak.login()
    │   ├── automations.ts   # CRUD + enable/disable + triggers
    │   ├── jobs.ts          # list, get, cancel, delete
    │   ├── triggers.ts      # type schemas, validate-config, webhook fire
    │   ├── taskTypes.ts     # discovery
    │   ├── hostGroups.ts    # host groups + hosts
    │   └── dlq.ts           # list, replay, delete
    ├── types/
    │   └── index.ts         # all TypeScript interfaces matching API DTOs
    ├── hooks/
    │   ├── useAuth.ts       # wraps keycloak context; exposes token, roles, userName
    │   └── useSignalR.ts    # SignalR connection lifecycle hook
    ├── components/
    │   ├── Layout/
    │   │   ├── AppShell.tsx          # root layout with sidebar + header
    │   │   ├── Sidebar.tsx           # nav links, logo, user pill
    │   │   └── Header.tsx            # page title, breadcrumb, user menu
    │   ├── ui/
    │   │   ├── Button.tsx            # primary, secondary, danger variants
    │   │   ├── Badge.tsx             # colored label chip
    │   │   ├── Card.tsx              # slate-800 card wrapper
    │   │   ├── Modal.tsx             # portal dialog
    │   │   ├── StatusBadge.tsx       # job status → colored badge
    │   │   ├── JsonViewer.tsx        # read-only Monaco wrapper
    │   │   └── ConfirmDialog.tsx     # "are you sure?" prompt
    │   ├── automation/
    │   │   ├── ConditionTreeBuilder.tsx   # recursive AND/OR tree editor
    │   │   ├── TriggerConfigForm.tsx      # dynamic form from schema fields
    │   │   └── AutomationStepForm.tsx     # multi-step wizard steps
    │   └── jobs/
    │       └── JobStatusTimeline.tsx      # visual status progress bar
    └── pages/
        ├── Dashboard.tsx
        ├── AutomationsList.tsx
        ├── AutomationDetail.tsx
        ├── AutomationForm.tsx        # create + edit wizard
        ├── JobsList.tsx
        ├── JobDetail.tsx
        ├── HostGroups.tsx
        ├── DLQPage.tsx
        └── TaskTypes.tsx
```

---

## How It Fits Into the Full Stack

```
Browser (localhost:3000)
    │  OIDC login redirect
    ▼
Keycloak (localhost:8180)           ← issues JWT access token
    │  redirect back with token
    ▼
Browser
    │  REST calls with Bearer token
    ▼
WebAPI (localhost:8080)             ← validates token, serves data
    │  Redis Streams
    ▼
JobAutomator / Orchestrator / WorkflowHost / WorkflowEngine
    │  job status events
    ▼
WebAPI (SignalR push)
    │  ws://localhost:8080/hubs/job-status
    ▼
Browser (live job status updates)
```

---

## Quick-Start Checklist

1. `cd deploy/docker && docker compose up -d` — start the full stack
2. Wait ~60s for Keycloak to be healthy (`docker compose ps`)
3. Go to `http://localhost:8180/admin` → log in as `admin / admin`
4. Switch to `flowforge` realm → create test users with roles (see [Test Accounts](#test-accounts))
5. Go to `http://localhost:3000` — you'll be redirected to Keycloak login
6. Log in with a test user
7. Explore! Start by creating a **Host Group**, then an **Automation** with a `schedule` trigger

---

## Troubleshooting

| Problem | Likely cause | Fix |
|---|---|---|
| Redirected to Keycloak but login fails | User not created or wrong realm | Check you're in the `flowforge` realm in Keycloak admin, not `master` |
| `401 Unauthorized` on API calls | Token not attached or expired | Check browser devtools → Network tab → check `Authorization` header |
| `CORS error` on API calls | WebAPI AllowedOrigins missing `localhost:3000` | Already configured; check `AllowedOrigins__0` env var in compose.yaml |
| Job detail page shows stale status | SignalR not connected | Check browser console for SignalR connection errors; verify Bearer token in WS handshake |
| `custom-script` trigger not firing | Python not installed in JobAutomator container | The `custom-script` evaluator requires `python3` on the container's PATH |
| Condition tree saves but automation never fires | ConditionRoot tree doesn't match trigger names exactly | Trigger names in the tree must match the `name` field of triggers exactly (case-sensitive) |
