# StudentGrowthExperience

Module housing the STOR-40 advisor and matching pipeline (Phase 1+ work).
The advisor runs as a background job against the same local LLM the
portfolio analyzer uses, with prompt budgets sized for that model.

## Phase 1 surface

- `AdvisorOptions` — prompt budgets, history window, output token cap, HTTP
  timeout. All values have sensible defaults; see `AdvisorOptions.cs` for
  the LM-Studio measurement each default is sized against.
- `DependencyInjection.AddStudentGrowthExperienceHandlers()` — registration
  hook called by `Program.cs` alongside the other modules' DI wiring.
  Currently a no-op; future phases add scoped services here as the
  endpoints and processors arrive.
- `README.md` — this file.

## Layout

```
src/Storporate.Modules/StudentGrowthExperience/
  AdvisorOptions.cs        # prompt-budget knobs, no [Required] members
  DependencyInjection.cs   # AddStudentGrowthExperienceHandlers()
  README.md
```

Phase 2+ adds the six tenant entities (`Exploration`,
`ExplorationMessage`, `ExplorationSummaryVersion`, `StudentContextNote`,
`ExplorationComparison`, `StudentFeedEntry`), the global `FeedItem`,
their `IEntityTypeConfiguration<T>` mappings, the migrations, and the
advisor endpoints.
