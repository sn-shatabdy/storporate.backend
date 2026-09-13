namespace Storporate.Modules.PlatformFoundations.Diagnostics;

/// <summary>
/// A trivial validated command used only to exercise the FluentValidation + global exception
/// handler pipeline end-to-end (temporary diagnostics endpoint, mirrored by Phase 3/4's
/// llm-ping/storage-ping endpoints).
/// </summary>
public sealed record EchoRequest(string Message);
