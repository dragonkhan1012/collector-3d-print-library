# Collector development notes

- Keep this project as a C# / Avalonia desktop application for Windows and macOS.
- Do not delete, overwrite, or silently move user model files. Managed storage must make a separate copy. Missing or offline linked folders are not deletion.
- Never replace an unreadable catalog with a blank one. Keep recoverable catalog backups and report the error.
- The sample catalog is clearly marked as demo data and has no printable model files.
- Keep visual structure and styling in `.axaml` where practical; keep window behavior and application logic in C#.
- Preserve compatibility or implement explicit migrations when changing catalog storage.
- 3D rendering uses a CPU renderer. Keep work bounded, cancellable, and safe when the selected model changes during loading. Default framing must keep the complete model and pedestal visible.
- Project checks are dependency-light console programs under `tests/`. Build with `dotnet build Collector.slnx`; run relevant check projects after related changes.
- The local `.local-data`, `.local`, `Backups`, and `artifacts` folders are machine-specific and must not be committed.
