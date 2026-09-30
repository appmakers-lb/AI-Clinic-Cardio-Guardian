# Compiler hygiene — v1.1.3

- Explicit `System.IO` in every source file that directly uses file-system APIs.
- `System.IO.File.ReadAllText` is fully qualified in `MainWindow.xaml.cs`.
- `System.Net.Http` remains explicit in `LocalAiGatewayService.cs`.
- No standalone backslash source artifacts remain.
