# Plan

| Requirement | Planned evidence |
|---|---|
| Exact 123-action registration | `Actions_RegistersExactlyThe123ReferenceDispatcherActions` |
| Unsupported extended actions are registered 1404 handlers | `UnsupportedExtendedActions_AreAllRegisteredAndReturn1404` |
| SDK profile/friend/moderation/avatar/guild mappings | `ExtendedProfileFriendModerationAvatarAndGuildActions_MapToFacade` |
| Forward and reaction mappings | `ForwardAndReactionActions_UseRegisteredReferencesAndFacade` |
| File storage confinement and recursive usage | `FileActions_StoreDownloadsInsidePluginStorageAndCalculateRecursiveUsage` |
| Existing behavior remains intact | Full `dotnet test` run |
