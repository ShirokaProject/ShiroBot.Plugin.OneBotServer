# Research

## Scope

- Production: `Actions/`, `Bridges/`, `Infrastructure/`
- Tests: `Tests/Actions/OneBotActionDispatcherTests.cs`
- Excluded: `Transports/`, `Plugin/`, runtime code, README files, and project files

## Reference Inventory

- 38 public OneBot v11 actions from `api.ts`
- 1 quick-operation action
- 34 LLOneBot extension actions from `extensions.ts`
- 16 file actions from `files.ts`
- 1 guild compatibility action
- 33 explicitly unsupported extended actions
- Required total: 123 unique registered actions

## Acceptance Checklist

- Register exactly 123 reference dispatcher actions.
- Register all 33 unsupported extended actions and return retcode 1404.
- Implement the SDK-supported profile, friend, request, moderation, forward, reaction, file, and guild actions named by the request.
- Restrict downloaded files to plugin storage.
- Assert total registration count and representative behavior.
- Pass the complete plugin test project.
