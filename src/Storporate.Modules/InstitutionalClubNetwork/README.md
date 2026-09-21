# InstitutionalClubNetwork

Club profiles and audience snapshots (STOR-69). A club builds a structured public profile that a company can read in one minute: who the club is, member count, the fields of study and study years its audience comes from, and the events it runs with typical attendance, frequency and the support each event needs. It works for a brand-new club with no past partnerships.

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
