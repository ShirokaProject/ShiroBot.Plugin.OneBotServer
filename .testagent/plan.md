# Plan

1. Replace the dispatcher action arrays with the exact reference-derived standard, extension, file, and unsupported sets; assert 123 unique names.
2. Extend the facade boundary and production facade with SDK 0.9.1 IQ operations for profile, requests, moderation, forwards, reactions, and file metadata.
3. Add a bounded plugin-storage helper for `download_file` and `get_file`, including filename confinement and atomic writes.
4. Implement dispatcher parameter validation and response shaping for every requested supported action; route all remaining reference actions to 1404.
5. Extend MSTest coverage for exact registration, representative supported behavior, null guild result, unsupported 1404, and storage traversal rejection.
6. Run the plugin test project with `dotnet test` and record the final quality review in `.testagent/status.md`.
