# Research

- Scope: `Actions/`, `Bridges/`, `Infrastructure/`, and `Tests/` in ShiroBot.Plugin.OneBotServer.
- Constraints: do not edit Transports, Plugin runtime, README, or either csproj; use ShiroBot.SDK 0.9.1.
- Test framework: MSTest 3.8.3, explicit compile globs already include `Actions/**/*.cs`, net10.0.
- Reference dispatcher composition: 38 public actions + `.handle_quick_operation` + 34 extension actions + 16 file actions + `get_guild_list` + 33 unsupported extended actions = 123 unique actions.
- Reference-only aliases `_del_group_notice` and `set_group_file_folder` are not in the reference dispatcher and must not inflate the count.
- SDK capabilities used: IQFriendApi request APIs; IQGroupApi notifications, moderation, and reactions; IQFileApi file APIs; IQSystemApi profile/custom-face APIs; IQMessageApi native forward APIs.

## Acceptance Checklist

- Registered action names exactly match the 123-name reference dispatcher set.
- Registered unsupported actions return OneBot business retcode 1404.
- Requested IQ-backed profile, request, moderation, forward, reaction, and file actions execute real facade methods.
- `download_file` and `get_file` write only plain file names beneath plugin `storage`, with bounded reads and atomic replacement.
- `get_guild_list` succeeds with null data.
- Tests assert exact action count, representative IQ-backed forwarding, unsupported behavior, reaction fetching, and safe file storage.
