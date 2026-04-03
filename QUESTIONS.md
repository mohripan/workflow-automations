# Open Questions for Host Management Feature — RESOLVED

> All questions have been answered by the user and implemented. See below for decisions made.

---

## 1. Job Database Provisioning ✅
**Decision:** Auto-provision where possible. Provisioning scripts provided for remote setups.
- WebApi auto-migrates known `JobConnections` on startup
- Scripts: `deploy/scripts/provision-job-db.sh` and `.ps1`
- Documentation updated in `docs/HOSTGROUPS.md`

## 2. Registration Token Rotation Policy ✅
**Decision:** Tokens have TTL (default 24h). Multiple active tokens per group supported.
- New `RegistrationToken` entity with `ExpiresAt` field
- TTL options: 1h, 6h, 24h, 7d, 30d
- Each token has optional label for identification

## 3. Remote Agent Network Requirements ✅
**Decision:** Documented secure networking options (VPN, tunnels, mTLS).
- See `docs/HOSTGROUPS.md` Security Considerations section

## 4. Agent Identity & Security ✅
**Decision:** Registration token for join, Redis/PG credentials for runtime.
- All token operations are audit-logged for traceability

## 5. Docker Compose Startup Order ✅
**Decision:** Added `depends_on: webapi: condition: service_healthy` + retry logic.
- WorkflowHost retries registration up to 5 times with exponential backoff

## 6. Host Group Deletion ✅
**Decision:** Supported with type-to-confirm safeguard (like AWS instance termination).
- Must type exact group name to confirm deletion
- All actions logged to audit trail (Activity tab in UI)

## 7. Frontend Scope ✅
**Decision:** Added host detail with tabbed interface, activity log, token management.
- Hosts tab: online/offline status, add/remove, heartbeat info
- Tokens tab: multi-token management with TTL, revoke individual tokens
- Activity tab: audit log feed

## 8. Keycloak User Setup ✅
**Decision:** Demo admin added to realm export.
- Username: `demo_admin`, Password: `webapi`, Role: `admin`
