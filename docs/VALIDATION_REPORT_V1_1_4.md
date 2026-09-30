# Validation Report — v1.1.4

Static validation completed in the build workspace:

- `MainWindow.xaml`: XML parse PASS
- `App.xaml`: XML parse PASS
- `ManualFindingWindow.xaml`: XML parse PASS
- `app.manifest`: XML parse PASS
- 30 XAML event handlers checked against `MainWindow.xaml.cs`: PASS
- Duplicate `x:Name` controls: none
- C# brace-balance sanity check: PASS
- Version metadata synchronized to 1.1.4
- Explicit `System.IO` / `System.Net.Http` compiler fixes retained

A real .NET compile was **not** run in this workspace because the .NET SDK is not installed here. The authoritative build remains Visual Studio 2022 / .NET 8 on Windows.

Clinical status: RESEARCH MODE ONLY. No validated raw-image diagnostic model is bundled.
