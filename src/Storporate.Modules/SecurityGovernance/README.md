# SecurityGovernance

Cross-cutting security concerns that don't naturally belong to any one feature module:

- **Permission enforcement (STOR-62).** `IPermissionService` resolves the calling user's
  permission set from their `ActorType` (`SystemRoles.Grants`); `PermissionAuthorizationHandler`
  gates `[Authorize(Policy = "permission:...")]` endpoints and short-circuits to success for
  `ActorTypes.Administrator`. The ambient `IAccountContext` (`UserId`, `AccountId`,
  `IsAdministrator`, `IpAddress`, `UserAgent`) is populated by
  `AccountContextMiddleware` and consumed by the handler, the EF Core global query filter,
  and the row-level-security interceptor.
- **Audit logging (STOR-63).** `IAuditLogWriter` appends tamper-evident rows to the
  `AuditLogEntries` table — each row's SHA-256 hash covers the previous row's hash so
  altering or deleting any past row breaks the chain. The writer pulls account/IP/User-Agent
  from the ambient `IAccountContext`, opens a raw `NpgsqlConnection` (bypassing EF Core so
  the audit write can run in its own transaction with its own advisory lock), and is a
  clean no-op under the EF InMemory provider the test suite uses. Phase 2 wires this into
  the OTP/session/permission handlers; Phase 3 adds the Administrator-only query endpoint.
