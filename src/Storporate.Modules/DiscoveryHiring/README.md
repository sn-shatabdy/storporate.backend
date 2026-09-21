# DiscoveryHiring

The employer-facing half of the STOR-43 "discovery" feature, built as two vertical
slices on the same route group:

- **Phase 1 (shipped):** the student "let employers find me" toggle surface
  (`GET` + `PUT /api/discovery/searchable-profile`). Lives in
  `SearchableProfile/` and the `RefreshTalentIndexEntryProcessor` background job
  in `TalentIndex/`.
- **Phase 2 (this slice):** the employer plain-language talent search surface
  (`POST` + `GET /api/discovery/talent-searches[/id]`). Lives in
  `TalentSearch/` and the `SearchTalentJobProcessor` background job.

## Endpoints

| Verb  | Path                                         | Permission                          | Returns                                  |
| ----- | -------------------------------------------- | ----------------------------------- | ---------------------------------------- |
| GET   | `/api/discovery/searchable-profile`          | `Permissions.SearchableProfile.Read` | The student's opt-in profile + visible-item count. |
| PUT   | `/api/discovery/searchable-profile`          | `Permissions.SearchableProfile.Update` | Upserts the profile + enqueues a refresh job.  |
| POST  | `/api/discovery/talent-searches`             | `Permissions.TalentSearch.Create`   | `202 Accepted` + `{ searchId }`.         |
| GET   | `/api/discovery/talent-searches/{id}`        | `Permissions.TalentSearch.Read`     | The polling response (see below).        |

### POST validation

| Error code                       | When                                                   |
| -------------------------------- | ------------------------------------------------------ |
| `talent_search_query_required`   | The query field is empty or whitespace.                |
| `talent_search_query_too_short`  | The trimmed query is shorter than 10 characters.       |
| `talent_search_query_too_long`   | The trimmed query is longer than 1000 characters.      |

### GET polling contract

The FE polls every few seconds until `status` flips from `Pending`. The response
shape is always:

```jsonc
{
  "id": "<guid>",
  "status": "Pending" | "Completed" | "Failed",
  "query": "<trimmed query>",
  "createdAt": "<iso>",
  "completedAt": "<iso | null>",
  "results": [ /* null until status is Completed */ ],
  "errorCode": "<llm_provider_error | null>"
}
```

A cross-account `id` returns `404` (the global query filter hides the row from
the caller, mirroring the rest of the codebase's tenant-isolation rule of
"never 403 on a cross-account id").

## Background jobs

`IBackgroundJobProcessor` registrations in `DependencyInjection.cs`:

- `RefreshTalentIndexEntryProcessor` (Type = `TalentIndexJobTypes.RefreshEntry`)
- `SearchTalentJobProcessor` (Type = `DiscoveryJobTypes.SearchTalent`)

The polling worker in `Storporate.Infrastructure.Jobs.PortfolioAnalysisWorker`
picks them up via `IEnumerable<IBackgroundJobProcessor>` per tick. Each
processor holds its own `WriteDbContext` + supporting collaborators and
runs inside a two-scope bracket (`BeginSystemScope` → claim →
`BeginAccountScope` → process).

## Audit

| Action                       | ResourceType    | Metadata                                                      |
| ---------------------------- | --------------- | ------------------------------------------------------------- |
| `talent_search_requested`    | `TalentSearch`  | `{ "queryLength": int, "queryHash": "<64-char hex>" }` (never the raw query) |
| `talent_search_completed`    | `TalentSearch`  | `{ "resultCount": int }`                                      |
| `talent_search_failed`       | `TalentSearch`  | `{ "errorCode": string }`                                     |

## Test hooks

- `FakeLlmClient` / `FakeEmbeddingClient` / `FakeTalentIndexRepository` —
  fakes the Phase 2 processor uses in unit tests. The processor is
  constructed via DI, so the test host swaps the real `ILlmClient` /
  `IEmbeddingClient` / `ITalentIndexRepository` for these.
- `SearchTalentHardCodingGuardTests` scans the build output for the plan's
  banned words (`evidence`, `proof`) in every prompt builder string + every
  Phase 2 server-generated user-visible string. Intended to fail loudly if a
  future change reintroduces them.
- `TalentSearchResponseShapeTests` walks the serialized GET response for any
  field name that smells like a score (rank / score / percent / similarity /
  distance / email / accountId / fileName). Intended to fail loudly if a
  future change leaks a numeric or PII field.

## Open items

- The employer's `languageCode` field is intentionally out of scope for
  Phase 2 — single-locale English; the request payload has no locale field.
- The prompt builders are pure-static helpers, no DI, so they are easy to
  re-test in isolation. Any future change to the literal system prompt
  strings MUST be paired with the hard-coding guard test above.
