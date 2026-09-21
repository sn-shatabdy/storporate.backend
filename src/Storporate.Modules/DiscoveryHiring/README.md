# DiscoveryHiring

The discovery surface: students let employers find them, employers publish and
manage job postings, and students browse the postings with a plain-language
fit against their own analyzed skills. Three vertical slices share the same
route group:

- **Talent profile (shipped):** the student "let employers find me" toggle
  surface (`GET` + `PUT /api/discovery/searchable-profile`). Lives in
  `SearchableProfile/` and the `RefreshTalentIndexEntryProcessor` background job
  in `TalentIndex/`.
- **Talent search (shipped):** the employer plain-language talent search
  surface (`POST` + `GET /api/discovery/talent-searches[/id]`). Lives in
  `TalentSearch/` and the `SearchTalentJobProcessor` background job.
- **Job postings (STOR-66 Phase 1, this slice):** employers publish, edit,
  pause, close and reopen job / internship postings; students browse the
  open postings with a fit label (no score, no percent). Lives in
  `JobPostings/`; applications land in `JobApplications/`.

## Endpoints

### Talent profile + search

| Verb  | Path                                         | Permission                          | Returns                                  |
| ----- | -------------------------------------------- | ----------------------------------- | ---------------------------------------- |
| GET   | `/api/discovery/searchable-profile`          | `Permissions.SearchableProfile.Read` | The student's opt-in profile + visible-item count. |
| PUT   | `/api/discovery/searchable-profile`          | `Permissions.SearchableProfile.Update` | Upserts the profile + enqueues a refresh job.  |
| POST  | `/api/discovery/talent-searches`             | `Permissions.TalentSearch.Create`   | `202 Accepted` + `{ searchId }`.         |
| GET   | `/api/discovery/talent-searches/{id}`        | `Permissions.TalentSearch.Read`     | The polling response (see below).        |

### Job postings (STOR-66 Phase 1)

Employer endpoints under `/api/discovery/job-postings`, student endpoints
under `/api/discovery/jobs`. Every employer endpoint is scoped to
`OwnerAccountId == caller account`; a cross-account id returns `404` (the
"never 403 on a cross-account id" rule).

| Verb   | Path                                                       | Permission                              |
| ------ | ---------------------------------------------------------- | --------------------------------------- |
| GET    | `/api/discovery/job-postings`                             | `Permissions.JobPostings.Read`          |
| POST   | `/api/discovery/job-postings`                             | `Permissions.JobPostings.Create`        |
| GET    | `/api/discovery/job-postings/{id}`                        | `Permissions.JobPostings.Read`          |
| PUT    | `/api/discovery/job-postings/{id}`                        | `Permissions.JobPostings.Update`        |
| POST   | `/api/discovery/job-postings/{id}/status`                 | `Permissions.JobPostings.Update`        |
| GET    | `/api/discovery/jobs`                                      | `Permissions.JobPostings.Browse`        |
| GET    | `/api/discovery/jobs/{id}`                                 | `Permissions.JobPostings.Browse`        |

#### Posting fields

| Field                  | Required | Notes                                                                 |
| ---------------------- | -------- | --------------------------------------------------------------------- |
| `title`                | yes      | 4–120 characters after trimming.                                      |
| `kind`                 | yes      | `Job` or `Internship`.                                                |
| `companyName`          | yes      | 2–120 characters after trimming.                                      |
| `location`             | no       | Free text; blank / whitespace becomes null.                           |
| `workMode`             | yes      | `OnSite`, `Remote`, or `Hybrid`.                                      |
| `description`          | yes      | 40–4000 characters after trimming.                                    |
| `requiredSkills`       | yes      | 1–20 distinct, trimmed names (2–40 chars each) after normalization.   |
| `applicationDeadline`  | no       | UTC date; today through today + 366 days (inclusive).                |
| `openings`             | no       | Integer 1–500; defaults to 1 when omitted.                            |
| `compensation`         | no       | `{ min, max, visibleToStudents }`; min ≤ max; both 0–10_000_000 taka. |
| `status`               | yes (mutation) | `Open`, `Paused`, or `Closed`.                                   |

Pay bounds are monthly **Bangladeshi taka**. `visibleToStudents` is an
explicit opt-in: employers always see their own values, but students only
see them when the flag is true AND at least one bound is set. Validation
rejects inverted ranges and negative values with `job_posting_compensation_invalid`.

#### Deadline rule

A posting whose `applicationDeadline` is strictly before today (UTC) is
treated as expired: hidden from `/api/discovery/jobs`, returns `404` from
`/api/discovery/jobs/{id}` for non-applied students, and any apply attempt
fails with HTTP 409 `job_posting_deadline_passed`. Applied students can still
see expired postings (defect 3 fix).

#### Browse query (`q`) and sort

`GET /api/discovery/jobs` accepts:

- `kind` — exact match.
- `workMode` — exact match.
- `q` — word-substring search over the `SearchText` column. Trimmed and
  lower-cased; up to 8 words, each 2–40 characters; ALL words must
  substring-match. A query over 100 characters, an empty word list after
  splitting, or any word outside the 2–40 range returns
  HTTP 400 `job_posting_query_invalid`.
- `sort` — `fit` (default) or `newest`. Anything else returns
  HTTP 400 `unknown_sort_key`.
- `page` (default 1) and `pageSize` (default 20, max 50). Out-of-range
  returns HTTP 400 `job_posting_query_invalid`.

Fit sort computes the label against the **newest 300 open non-expired
postings** (a hard cap that bounds the heaviest cost — the per-row fit
calculation), then orders: Strong match → Good match → Early match → Not yet,
ties broken by `CreatedAt` descending then `Id` ascending, then pages.
`total` in the response is the unpaged count of candidates inside the 300 cap.

#### Fit label rules

Per posting, the fit label is computed from the intersection of the
posting's normalized required-skills list and the student's best Strong /
Developing findings on Analyzed portfolio items:

- **Strong match** — every required skill is in the student's skills AND
  at least half of them are Strong.
- **Good match** — at least half of the required skills match.
- **Early match** — at least one skill matches but fewer than half.
- **Not yet** — zero matches.

No score, number, or percentage is ever sent to the student.

### Job applications (STOR-67)

`POST /api/discovery/jobs/{id}/applications` is the student-only apply flow.
`GET /api/discovery/applications` lists the student's own applications.
`GET /api/discovery/job-postings/{id}/applications[/...]` and the
`/status` mutation are the employer-side review flow.

### POST validation

| Error code                       | When                                                   |
| -------------------------------- | ------------------------------------------------------ |
| `talent_search_query_required`   | The query field is empty or whitespace.                |
| `talent_search_query_too_short`  | The trimmed query is shorter than 10 characters.       |
| `talent_search_query_too_long`   | The trimmed query is longer than 1000 characters.      |

### Job posting validation

| Error code                            | When                                                                |
| ------------------------------------- | ------------------------------------------------------------------- |
| `job_posting_title_invalid`           | Title trimmed length outside 4–120.                                 |
| `job_posting_kind_invalid`            | `kind` is not `Job` or `Internship`.                                |
| `job_posting_company_invalid`         | `companyName` trimmed length outside 2–120.                         |
| `job_posting_work_mode_invalid`       | `workMode` is not `OnSite`, `Remote`, or `Hybrid`.                  |
| `job_posting_description_invalid`     | `description` trimmed length outside 40–4000.                       |
| `job_posting_skills_invalid`          | `requiredSkills` is null, missing, or contains a null entry.        |
| `job_posting_skills_count_invalid`    | After normalization, fewer than 1 or more than 20 distinct skills.  |
| `job_posting_skill_length_invalid`    | Any skill trimmed length is outside 2–40.                           |
| `job_posting_deadline_invalid`        | `applicationDeadline` is before today or more than 366 days ahead.  |
| `job_posting_openings_invalid`        | `openings` is outside 1–500.                                        |
| `job_posting_compensation_invalid`    | Negative bound, `min > max`, or bound outside 0–10_000_000 taka.     |
| `job_posting_closed`                  | PUT or status change on a Closed posting.                           |
| `job_posting_not_found`               | Cross-account or unknown id (returns 404).                          |
| `job_posting_query_invalid`           | Browse `q` / `page` / `pageSize` is out of range.                   |
| `job_posting_deadline_passed`         | Apply on a posting whose UTC deadline is before today.              |
| `job_posting_conflict`                | Stale xmin on PUT or status change (concurrent writer won).         |
| `unknown_sort_key`                    | Browse `sort` is not `fit` or `newest`.                             |

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
| `job_posting_created`        | `JobPosting`    | `{ "jobPostingId": "<guid>" }` (never the title / description). |
| `job_posting_updated`        | `JobPosting`    | `{ "jobPostingId": "<guid>" }`.                                |
| `job_posting_status_changed` | `JobPosting`    | `{ "jobPostingId": "<guid>", "status": "<new status>" }`.      |
| `job_application_submitted`  | `JobApplication`| `{ "jobApplicationId": "<guid>", "jobPostingId": "<guid>" }`.  |

## Concurrency

`JobPostings` carries an Npgsql `xmin` system column mapped as a
`Property<uint>("xmin")` concurrency token
(`JobPostingConfiguration`). Every `PUT /api/discovery/job-postings/{id}`
and every status change runs an UPDATE with a `WHERE xmin = @p_xmin`
clause; a stale read followed by a save raises
`DbUpdateConcurrencyException`, which the handler maps to
`JobPostingConflictException` → HTTP 409 `job_posting_conflict`. The
end-to-end proof against a real Postgres lives in
`JobPostingConcurrencyPostgresTests` (gated by `STORPORATE_TEST_PG`,
`[Collection("LivePg")]`, skipped when the env var is unset).

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
- `JobPostingEndpointsTests` exercises every employer / student endpoint via
  the real pipeline with real-issued JWTs against the in-memory provider,
  including the deadline / openings / compensation validation,
  applied-student visibility on Paused / Closed / expired postings, and the
  sort + paging behavior across the 300-candidate cap.

## Open items

- The employer's `languageCode` field is intentionally out of scope for
  Phase 2 — single-locale English; the request payload has no locale field.
- The prompt builders are pure-static helpers, no DI, so they are easy to
  re-test in isolation. Any future change to the literal system prompt
  strings MUST be paired with the hard-coding guard test above.
- The job-postings `SearchText` column is built on every write by
  `JobPostingSkills.BuildSearchText` (lower-case + collapse whitespace +
  drop brackets / quotes / commas from the JSON skill list). The
  `JobPostingRedo` migration backfills it on existing rows so the browse
  endpoint's word-substring search has something to match on rows that
  pre-date the redo. Any change to the normalisation MUST be paired with a
  backfill step in the next migration.
- Compensation bounds are stored in raw monthly taka as `int` — no
  currency column, no FX conversion, no rounding. Multi-currency or
  fraction-of-taka support is out of scope for STOR-66 Phase 1.
