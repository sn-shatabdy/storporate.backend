using System.Runtime.CompilerServices;

// Grant the unit test assembly visibility into the internals of
// Storporate.Infrastructure. The current consumer is a single test seam in
// RowLevelSecurityInterceptor (BuildGucSetCommandForTest), exposed so the Phase 5 RLS
// fix can verify the SQL the interceptor emits without standing up a real Npgsql
// connection. Keep the list short — prefer production-visible test seams via this
// mechanism only when the alternative is a much heavier testing dependency.
[assembly: InternalsVisibleTo("Storporate.Tests.Unit")]
