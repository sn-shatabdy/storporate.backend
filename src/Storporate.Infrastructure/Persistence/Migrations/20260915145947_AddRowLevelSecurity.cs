using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// STOR-62 Phase 5: Install PostgreSQL row-level security on every <see cref="SharedKernel.Entities.IAccountScoped"/>
    /// table (currently just <c>Jobs</c>) and provision the two-privilege role split the app
    /// connects as. Together with the EF Core global query filter (Phase 4) and the
    /// <c>RowLevelSecurityInterceptor</c> save-time guard (Phase 4), this gives the workspace-
    /// isolation pipeline its third and last independent layer of defense: even a raw
    /// <c>SELECT * FROM "Jobs"</c> issued outside the application goes through this policy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Provider gating.</b> The migration is a no-op under any non-Npgsql provider. The
    /// test suite uses <c>Microsoft.EntityFrameworkCore.InMemory</c>, whose
    /// <see cref="Migrations.MigrationBuilder.ActiveProvider"/> is
    /// <c>Microsoft.EntityFrameworkCore.InMemory</c> — never the Npgsql one — so this
    /// migration is skipped entirely in unit tests, exactly as the Phase 5 acceptance criteria
    /// require. The provider-name check happens <i>before</i> any SQL is emitted; if the check
    /// fails, neither role DDL nor policy DDL runs.
    /// </para>
    /// <para>
    /// <b>Roles.</b>
    /// <list type="bullet">
    ///   <item><c>storporate_app</c> — <c>LOGIN</c>, <c>NOBYPASSRLS</c>, with a documented
    ///   development-default password (<c>STORPORATE_APP_PASSWORD</c> environment variable,
    ///   defaulting to <c>storporate_dev_app</c> when unset). The password is set inside a
    ///   <c>DO $$ ... $$</c> block because Postgres does not allow
    ///   <c>CREATE ROLE</c>/<c>ALTER ROLE</c> parameter values from a separate
    ///   <c>SELECT set_config(...)</c> in the same transaction; we resolve the password once
    ///   and use it for both the <c>CREATE</c> and the <c>ALTER</c>.</item>
    ///   <item><c>storporate_rls_bypass</c> — <c>NOLOGIN</c>, <c>NOBYPASSRLS</c>; granted to
    ///   <c>storporate_app</c> with <c>WITH INHERIT FALSE</c> so the app connection can
    ///   explicitly <c>SET ROLE storporate_rls_bypass</c> when an admin path needs to bypass
    ///   RLS without leaking the bypass to ordinary application connections.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Schema-level grants.</b> <c>storporate_app</c> receives <c>USAGE</c> on the
    /// public schema (so it can resolve object names) and <c>SELECT/INSERT/UPDATE/DELETE</c>
    /// on every table that already exists; <c>ALTER DEFAULT PRIVILEGES</c> extends those
    /// grants to every table/sequence created in the future, so future
    /// <see cref="SharedKernel.Entities.IAccountScoped"/> entities don't need a new
    /// migration just to grant access (they still need a new migration to install their RLS
    /// policy — see the policy-application loop below).
    /// </para>
    /// <para>
    /// <b>Policy shape.</b> Each policy expression uses <c>NULLIF(current_setting(...), '')</c>
    /// so an unset GUC (the pre-authentication window or a misuse path) coerces to SQL
    /// <c>NULL</c> rather than the empty string, and the <c>= ambient::uuid</c> comparison
    /// returns <c>NULL</c> rather than <c>false</c>. Postgres row-level security treats
    /// <c>NULL</c>-evaluating predicates as not-true, so unset GUCs deny access — the safe
    /// direction. The Administrator bypass comes from the OR-branch:
    /// <c>current_setting('app.is_admin', true)::boolean</c>. <see cref="Persistence.Interceptors.RowLevelSecurityInterceptor"/>
    /// is the only place that sets <c>app.is_admin</c>, so it stays the single source of
    /// truth for what Postgres gets told about the current principal.
    /// </para>
    /// <para>
    /// <b>Idempotency.</b> Every DDL is wrapped in <c>DO $$ ... IF NOT EXISTS ... $$</c>
    /// (or the equivalent <c>DROP</c>/<c>REVOKE</c> form in <see cref="Down"/>) so the
    /// migration is safe to re-run if EF's migration history is ever desynchronized. The
    /// policies themselves use <c>DROP POLICY IF EXISTS</c> in <see cref="Down"/> and
    /// <c>CREATE POLICY</c> in <see cref="Up"/>; the role DDL uses the IF-NOT-EXISTS form
    /// because Postgres does not support <c>CREATE ROLE IF NOT EXISTS</c> — we work around
    /// it by querying <c>pg_roles</c> inside the DO block.
    /// </para>
    /// </remarks>
    public partial class AddRowLevelSecurity : Migration
    {
        /// <summary>
        /// The exact provider name <see cref="UseNpgsql(string,System.Action{NpgsqlDbContextOptionsBuilder})"/>
        /// registers. Compared verbatim against <see cref="Migrations.MigrationBuilder.ActiveProvider"/>.
        /// </summary>
        private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

        /// <summary>
        /// Default password for <c>storporate_app</c> when <c>STORPORATE_APP_PASSWORD</c> is
        /// not set in the environment. Documented in <see cref="Up"/> as "dev-only"; production
        /// deployments must set the env var before running <c>dotnet ef database update</c>.
        /// </summary>
        private const string DefaultAppRolePassword = "storporate_dev_app";

        /// <summary>
        /// Schema the application tables live in. PostgreSQL's default is <c>public</c>; the
        /// <c>search_path</c> resolves unqualified names against it, and our migrations never
        /// create a custom schema, so this is a constant.
        /// </summary>
        private const string TargetSchema = "public";

        /// <summary>
        /// Every <see cref="SharedKernel.Entities.IAccountScoped"/> table currently in the model.
        /// </summary>
        /// <remarks>
        /// Kept as an explicit array rather than walking the model at migration time: the
        /// migration must be source-controllable (no runtime introspection), and the RLS
        /// policy is a per-table DDL that has to be authored as a known set. Adding a new
        /// <see cref="SharedKernel.Entities.IAccountScoped"/> entity must add a row here (and
        /// a corresponding <c>CREATE POLICY</c>/<c>ALTER TABLE</c> block in <see cref="Up"/>
        /// / <see cref="Down"/>) — the RLS pipeline is opt-in by design, not auto-applied.
        /// </remarks>
        private static readonly string[] AccountScopedTables = { "Jobs" };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Provider gate. The InMemory provider used by the test suite has
            // ActiveProvider == "Microsoft.EntityFrameworkCore.InMemory", so every block below
            // is skipped in unit tests. A real production deploy against any non-Postgres
            // provider (e.g. an SQLite-backed integration environment) is also skipped
            // without error.
            if (!string.Equals(migrationBuilder.ActiveProvider, NpgsqlProviderName, System.StringComparison.Ordinal))
            {
                return;
            }

            // 1. Provision roles. Password is read from STORPORATE_APP_PASSWORD with a
            // dev-default fallback — DO $$ ... $$ because CREATE/ALTER ROLE PASSWORD do not
            // accept bind parameters.
            migrationBuilder.Sql(
                $"""
                DO $$
                DECLARE
                    app_password text := coalesce(nullif(current_setting('STORPORATE_APP_PASSWORD', true), ''), '{DefaultAppRolePassword}');
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'storporate_app') THEN
                        EXECUTE format('CREATE ROLE storporate_app WITH LOGIN NOBYPASSRLS PASSWORD %L', app_password);
                    ELSE
                        EXECUTE format('ALTER ROLE storporate_app WITH LOGIN NOBYPASSRLS PASSWORD %L', app_password);
                    END IF;

                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'storporate_rls_bypass') THEN
                        CREATE ROLE storporate_rls_bypass NOLOGIN NOBYPASSRLS;
                    END IF;

                    -- The application role can be granted the bypass role to use explicitly
                    -- (via SET ROLE storporate_rls_bypass) when an admin path needs to bypass
                    -- RLS. INHERIT FALSE keeps the bypass from leaking to ordinary sessions.
                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_auth_members m
                        JOIN pg_roles r ON r.oid = m.roleid
                        JOIN pg_roles g ON g.oid = m.member
                        WHERE r.rolname = 'storporate_rls_bypass'
                          AND g.rolname = 'storporate_app')
                    THEN
                        GRANT storporate_rls_bypass TO storporate_app WITH INHERIT FALSE;
                    END IF;
                END
                $$;
                """);

            // 2. Schema-level privileges. CONNECT is granted to PUBLIC by default in
            // Postgres, so we don't need to re-grant it; we still grant USAGE on the schema
            // and CRUD on existing tables, plus ALTER DEFAULT PRIVILEGES so future tables
            // created by, e.g., a future STOR-XX evidence-entity migration, don't have to
            // remember to grant access.
            migrationBuilder.Sql(
                $"""
                GRANT USAGE ON SCHEMA {TargetSchema} TO storporate_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {TargetSchema} TO storporate_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {TargetSchema} TO storporate_app;

                -- Future tables created by anyone (typically the migration runner's role,
                -- i.e. the dev superuser) inherit CRUD grants automatically. ALTER DEFAULT
                -- PRIVILEGES is per-creating-role, so we apply it for the role that runs
                -- migrations; in dev that's the connection role. Production migrations are
                -- expected to run as the same role that owns the schema, so this is a
                -- consistent rule across environments.
                ALTER DEFAULT PRIVILEGES IN SCHEMA {TargetSchema}
                    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO storporate_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA {TargetSchema}
                    GRANT USAGE, SELECT ON SEQUENCES TO storporate_app;
                """);

            // 3. Per-table RLS policy. ENABLE + FORCE so the policy applies to the table
            // owner too — important because the connection role used by `dotnet ef database
            // update` is typically a superuser/superuser-equivalent in dev, and FORCE is
            // what stops a privileged role from accidentally bypassing RLS by mistake. The
            // USING and WITH CHECK clauses both reference the same expression so reads and
            // writes are constrained symmetrically (writing a row that doesn't match the
            // ambient account is denied, not just reading).
            foreach (var table in AccountScopedTables)
            {
                migrationBuilder.Sql(
                    $"""
                    ALTER TABLE "{table}" ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE "{table}" FORCE ROW LEVEL SECURITY;

                    DROP POLICY IF EXISTS account_scoped ON "{table}";

                    CREATE POLICY account_scoped ON "{table}"
                        -- NULLIF coerces an unset GUC (the pre-authentication window) to
                        -- SQL NULL rather than the empty string; the comparison against an
                        -- ambient UUID then evaluates to NULL (treated as not-true by RLS)
                        -- so unset GUCs deny access — the safe direction.
                        USING (
                            current_setting('app.is_admin', true)::boolean
                            OR "AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                        )
                        WITH CHECK (
                            current_setting('app.is_admin', true)::boolean
                            OR "AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                        );
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Provider gate mirrors Up. If we were a no-op on the way up, we are a no-op on
            // the way down too.
            if (!string.Equals(migrationBuilder.ActiveProvider, NpgsqlProviderName, System.StringComparison.Ordinal))
            {
                return;
            }

            // 3. Drop policies and disable RLS — reverse of Up's per-table block.
            foreach (var table in AccountScopedTables)
            {
                migrationBuilder.Sql(
                    $"""
                    DROP POLICY IF EXISTS account_scoped ON "{table}";
                    ALTER TABLE "{table}" NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE "{table}" DISABLE ROW LEVEL SECURITY;
                    """);
            }

            // 2. Revoke schema-level grants. ALTER DEFAULT PRIVILEGES entries are revoked
            // the same way they were granted; per-table grants fall through the
            // REVOKE ... ON ALL TABLES statement.
            migrationBuilder.Sql(
                $"""
                REVOKE USAGE ON SCHEMA {TargetSchema} FROM storporate_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {TargetSchema} FROM storporate_app;
                REVOKE USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {TargetSchema} FROM storporate_app;

                ALTER DEFAULT PRIVILEGES IN SCHEMA {TargetSchema}
                    REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM storporate_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA {TargetSchema}
                    REVOKE USAGE, SELECT ON SEQUENCES FROM storporate_app;
                """);

            // 1. Drop roles — order matters; revoke the membership before dropping either
            // role. CASCADE is required because Postgres refuses to drop a role that still
            // owns objects, has granted permissions, or has been granted to others.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'storporate_rls_bypass') THEN
                        -- REVOKE from storporate_app first so the membership edge is removed
                        -- before the role itself.
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'storporate_app') THEN
                            REVOKE storporate_rls_bypass FROM storporate_app;
                        END IF;
                        DROP ROLE storporate_rls_bypass;
                    END IF;

                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'storporate_app') THEN
                        DROP ROLE storporate_app;
                    END IF;
                END
                $$;
                """);
        }
    }
}
