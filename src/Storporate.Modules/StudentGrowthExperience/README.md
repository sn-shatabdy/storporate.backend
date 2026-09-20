# StudentGrowthExperience

Module housing the STOR-40 advisor pipeline — the student-facing surface
that opens an exploration, asks the local LLM personal questions, stores
versioned gaps-and-suggestions summaries, and lets the student refresh,
rename, delete, or compare two explorations.

The advisor runs as a background job against the same local LLM (Bionic
on `localhost:1234`) the portfolio analyzer uses, with prompt budgets
sized for that model — see `AdvisorOptions.cs` for the measurement each
default is sized against.

## Phase 2 surface

`POST /api/growth/explorations` opens a new exploration, enqueues an
opening-turn `AdvisorTurn` job, and returns `{ id, status: "Working" }`.
The job is picked up by the polling worker
(`Storporate.Infrastructure.Jobs.PortfolioAnalysisWorker`), runs under
the owning account's `IBackgroundAccountScope` bracket, and writes one
`ExplorationMessage` plus optional `StudentContextNote`s and an
`ExplorationSummaryVersion` row.

The student replies, refreshes, retries, renames, deletes, or compares
two explorations via the endpoints listed below.

## Endpoints

| Method | Path | Permission | Body / params | Returns |
| --- | --- | --- | --- | --- |
| GET    | `/api/growth/explorations`                          | `Advisor.Read`   | —                                                       | `200 [{ id, title, status, updatedAt, latestVersionNumber? }]` |
| POST   | `/api/growth/explorations`                          | `Advisor.Create` | `{ direction?: string }`                                | `201 { id, status: "Working" }` |
| GET    | `/api/growth/explorations/{id}`                     | `Advisor.Read`   | —                                                       | `200` full detail, `404` cross-account |
| POST   | `/api/growth/explorations/{id}/messages`            | `Advisor.Update` | `{ content?: string, answers?: [{question,answer}] }`   | `202 { explorationId, newJobId }`, `409 exploration_busy` |
| POST   | `/api/growth/explorations/{id}/refresh`             | `Advisor.Update` | —                                                       | `202`, `409 exploration_busy` |
| POST   | `/api/growth/explorations/{id}/retry`               | `Advisor.Update` | —                                                       | `202`, `409 exploration_not_retryable` |
| PUT    | `/api/growth/explorations/{id}/title`               | `Advisor.Update` | `{ title: string }`                                     | `204`, `400 title_required / title_too_long` |
| DELETE | `/api/growth/explorations/{id}`                     | `Advisor.Delete` | —                                                       | `204`, `404` cross-account |
| POST   | `/api/growth/explorations/compare`                  | `Advisor.Update` | `{ firstExplorationId, secondExplorationId }`           | `202 { comparisonId }`, `400 compare_needs_two`, `404` cross-account, `409 exploration_has_no_summary` |
| GET    | `/api/growth/explorations/compare/{id}`             | `Advisor.Read`   | —                                                       | `200`, `404` cross-account |

Per-student cap: `AdvisorOptions.MaxExplorationsPerStudent` (default 20).
Hitting the cap on POST returns `409 exploration_limit_reached`.

## Response shapes

### Exploration detail (`GET /api/growth/explorations/{id}`)

```json
{
  "id": "guid",
  "title": "string",
  "status": "Idle | Working | Failed",
  "lastError": "string | null",
  "createdAt": "2026-09-19T00:00:00+00:00",
  "updatedAt": "2026-09-19T00:00:00+00:00",
  "messages": [
    {
      "id": "guid",
      "role": "Student | Advisor",
      "content": "string",
      "questions": [
        { "prompt": "string", "options": ["string"] }
      ],
      "createdAt": "..."
    }
  ],
  "latestSummary": {
    "versionNumber": 1,
    "createdAt": "...",
    "changeNote": "string | null",
    "gaps": [
      { "title": "string", "detail": "string", "band": "Developing | Missing | null" }
    ],
    "suggestions": [
      {
        "title": "string",
        "reason": "string",
        "nextStep": "string",
        "source": {
          "feedItemId": "guid",
          "title": "string",
          "url": "string",
          "sourceName": "string"
        }
      }
    ]
  }
}
```

### Comparison detail (`GET /api/growth/explorations/compare/{id}`)

```json
{
  "id": "guid",
  "firstExplorationId": "guid",
  "secondExplorationId": "guid",
  "status": "Pending | Completed | Failed",
  "resultText": "string | null",
  "createdAt": "..."
}
```

## JSON contracts

The advisor LLM is asked to return a single JSON object matching this
shape (see `AdvisorPromptBuilder.BuildSystemPrompt()`):

```json
{
  "reply": "string",
  "questions": [
    { "prompt": "string", "options": ["string"] }
  ],
  "title": "string",
  "contextNotes": ["string"],
  "summary": {
    "gaps": [
      { "title": "string", "detail": "string", "band": "Developing | Missing" }
    ],
    "suggestions": [
      {
        "title": "string",
        "reason": "string",
        "nextStep": "string",
        "sourceItemId": "guid"
      }
    ],
    "changeNote": "string"
  }
}
```

The compare LLM is asked for `{ "reply": "string" }` only.

## Layout

```
src/Storporate.Modules/StudentGrowthExperience/
  AdvisorOptions.cs                  # prompt budgets + per-student cap
  AdvisorDefaults.cs                 # DefaultExplorationTitle + FriendlyAdvisorError
  DependencyInjection.cs             # AddStudentGrowthExperienceHandlers()
  GrowthJobTypes.cs                  # AdvisorTurn / CompareExplorations + payload records
  StudentGrowthEndpoints.cs          # 10 endpoints, every one .RequirePermission(...)
  README.md                          # this file
  Advisor/
    AdvisorPromptBuilder.cs          # system + user prompt + JSON DTOs
    AdvisorResponseParser.cs         # cap-and-validate pipeline (5/6/8/6/8/300/6000/200)
    AdvisorResponse.cs               # typed result records (parser → DB)
    AdvisorResponseInvalidException.cs
    AdvisorTurnJobProcessor.cs       # claim → scope → prompt → LLM → write
    CompareExplorationsJobProcessor.cs
  Requests/                          # CreateExplorationRequest, AddExplorationMessageRequest, ...
  Responses/                         # ExplorationDetailResponse, ExplorationSummaryResponse, ...
  Validators/                        # FluentValidation rules + error codes
  Exceptions/                        # Busy / NotRetryable / LimitReached / HasNoSummary
  Handlers/                          # static handler methods (Create, List, Get, Add, Refresh, Retry, ...)
```

## Audit trail

| Action | Resource | Metadata (ids + counts only) |
| --- | --- | --- |
| `exploration_created` | `Exploration` | `hasOpeningDirection` (bool), `jobId` (string) |
| `exploration_deleted` | `Exploration` | `messageCount` (int), `summaryVersionCount` (int), `comparisonCount` (int) |

No message text, no AI output, no student-written text is ever written
to the audit log — only ids and counts.

## Logging policy

The advisor processors log a single structured line on each successful
turn with `Event`, `JobId`, `ExplorationId`, `AccountId`, `DurationMs`,
`PromptTokens`, `CompletionTokens`, `TotalTokens`, `QuestionCount`,
`GapCount`, `SuggestionCount`, `NoteCount`, `VersionNumber`, `Mode` —
counts only, never student text or AI output.
