# InstitutionalClubNetwork

Club profiles and audience snapshots (STOR-69) company sponsorship goals (STOR-70) and sponsor-club matching (STOR-71). A club builds a structured public profile that a company can read in one minute: who the club is, member count, the fields of study and study years its audience comes from, and the events it runs with typical attendance, frequency and the support each event needs. It works for a brand-new club with no past partnerships.

## Endpoints

Club (`club-profile:manage`, always the caller's own profile):

- `GET /api/clubs/profile`
- `PUT /api/clubs/profile` (creates a Draft on first save, edits keep the status)
- `POST /api/clubs/profile/publish` (400 `club_profile_incomplete` until complete)
- `POST /api/clubs/profile/unpublish`

Company (`club-profile:read`, Published profiles only):

- `GET /api/clubs?q=&field=&university=`
- `GET /api/clubs/{id}` (the profile id, never the owner account id)

## Layout

- `ClubProfiles/` requests, responses, validators, `ManageClubProfileHandler` (club side) and `BrowseClubsHandler` (company side).
- `Exceptions/` `ClubProfileIncompleteException`, mapped in `GlobalExceptionHandler`.
- Data: non-tenant `ClubProfile` entity (unique owner account, events and audience stored as JSON text); scoping is explicit in the handlers, like `JobPosting`.
- Audit rows (ids only): `club_profile_updated`, `club_profile_published`, `club_profile_unpublished`.

## Sponsorship goals (STOR-70)

A company records what it wants from sponsoring student activity: objectives (Brand awareness, Recruiting, CSR education, Community outreach, Product launch, Other), the audience it wants to reach (fields of study, study years, cities, universities), the event kinds it is open to (Hackathon, Competition, Workshop, Career fair, Conference, Cultural event, Sports event, Seminar) and a rough budget range. Budget amounts are in BDT (Bangladeshi taka); there is no currency column. A company can keep several sets, and clubs read the Active ones.

Company (`sponsorship-goals:manage`, Organization only, always the caller's own sets; foreign ids give 404 `sponsorship_goal_not_found`):

- `POST /api/sponsorship/goals` (201, starts Active), `GET /api/sponsorship/goals`
- `GET|PUT|DELETE /api/sponsorship/goals/{id}` (delete is a hard delete)
- `POST /api/sponsorship/goals/{id}/status` (`Active` or `Paused`; same status is a no-op)

Club (`sponsorship-goals:read`, Club only, Active sets only):

- `GET /api/sponsorship/companies?objective=&eventKind=&q=` (newest first, max 50)
- `GET /api/sponsorship/companies/{id}` (the goal set id, never the owner account id)

The budget is shown to clubs only when the company set `visibleToClubs` and at least one bound exists; otherwise it is `null`.

Layout: `SponsorshipGoals/` (requests, responses, validators, `SponsorshipGoalOptions`, `ManageSponsorshipGoalsHandler`, `BrowseCompanyGoalsHandler`). Data: non-tenant `SponsorshipGoalSet` entity (owner indexed, not unique; lists stored as JSON text); scoping is explicit in the handlers. Audit rows (ids and status only): `sponsorship_goal_created`, `sponsorship_goal_updated`, `sponsorship_goal_status_changed`, `sponsorship_goal_deleted`.

## Sponsor-club matching (STOR-71)

Suggestions in both directions, computed on the fly from existing tables (no new data, no audit rows). Fit is a word, `Strong`, `Good` or `Partial`; there is no numeric score anywhere in a response. Every match carries up to 4 plain reasons.

Permission `sponsorship-matches:read` is granted to both Organization and Club. Because one permission covers both sides, each endpoint also checks the caller's actor type (403 for the wrong side; Administrator passes both).

- Organization: `GET /api/sponsorship/goals/{goalId}/club-matches?q=` returns `{ items: ClubMatch[] }` (max 30). `goalId` must be one of the caller's own goal sets in any status, else 404 `sponsorship_goal_not_found`. Candidates are Published clubs. `ClubMatch = { fit, reasons, club }` where `club` is the same summary `GET /api/clubs` returns.
- Club: `GET /api/clubs/profile/company-matches?q=` returns `{ items: CompanyMatch[] }` (max 30). No club profile is 404 `club_profile_not_found`, a Draft profile is 409 `club_profile_not_published`. Candidates are Active goal sets. `CompanyMatch = { fit, reasons, company }` where `company` is the same summary `GET /api/sponsorship/companies` returns (budget null unless the company chose to show it and a bound exists).
- `q` longer than 200 characters is 400 `sponsorship_match_query_invalid`.

Rules (`SponsorshipMatcher`, pure and unit tested). Five dimensions are compared case-insensitively after trim: field of study, event kind, university, study year, city (only when the club profile has one). Club events have no structured kind, so the goal's event kinds are matched against event title and description text through a fixed keyword list per kind (for example Career fair also matches "job fair").

- Strong: event kind overlaps, and field or year overlaps, and at least 3 dimensions overlap.
- Good: at least 2 dimensions overlap and one of them is field of study or event kind.
- Partial: any other overlap (exactly 1 dimension, or 2 or more without field of study or event kind).
- No overlap: not listed, unless a search matches it, in which case it is Partial with the search reason.

Order: Strong, Good, Partial; inside a band by internal points (field 3, event kind 3, year 2, university 2, city 1, never returned), then newest first.

Plain-word search (`SponsorshipMatchQuery`) is a deliberate simplification of natural-language search: no language model and no embeddings. The query is lower-cased, split on non letters and digits, stop words and one-letter tokens are dropped, and each remaining word is expanded through a small synonym map (coding, software, tech to hackathon and Computer Science; hiring, jobs to Recruiting and Career fair; csr, charity to CSR education and Community outreach; sports; culture, music; startup, business; and a few more). A candidate matches when at least one word or synonym appears as a whole word or phrase in its searchable text (club: name, university, city, fields, event titles and detected event kinds; goal: name, company name, objectives, event kinds, fields, cities, universities). A "year 2" or "2nd year" mention becomes a study-year filter. A query with nothing usable left is treated as no query. Search reasons read "Matches your words: hackathon." (max 3 words).

Layout: `SponsorshipMatching/` (`SponsorshipMatcher`, `SponsorshipMatchQuery`, `SponsorshipMatchHandler`, `SponsorshipMatchResponses`). At most 500 newest candidates are read per call.
