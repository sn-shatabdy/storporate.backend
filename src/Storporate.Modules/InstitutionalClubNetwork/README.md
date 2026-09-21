# InstitutionalClubNetwork

Club profiles and audience snapshots (STOR-69) and company sponsorship goals (STOR-70). A club builds a structured public profile that a company can read in one minute: who the club is, member count, the fields of study and study years its audience comes from, and the events it runs with typical attendance, frequency and the support each event needs. It works for a brand-new club with no past partnerships.

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
