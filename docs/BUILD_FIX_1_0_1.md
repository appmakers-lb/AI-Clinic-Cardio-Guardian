# Build Fix 1.0.1

Fixed Visual Studio compiler errors caused by `AuditLogService.Path` shadowing `System.IO.Path` and missing System.IO namespace imports.

Changes:
- Renamed `AuditLogService.Path` to `LogFilePath`.
- Fully qualified `System.IO.Path.Combine` inside `AuditLogService`.
- Added explicit `System.IO` imports for file services.
- Enabled `ImplicitUsings` and nullable reference types in all projects.
- Updated internal runtime version to 1.0.1.
