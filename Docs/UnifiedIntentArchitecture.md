# Unified Intent Architecture

## Goal

Make one execution layer work across many apps by separating:

- what to do (intent)
- how to do it (platform operations)

## Layers

1. `Config/ActionCatalog.json`
Defines cross-app intents (core + extended).

2. `Config/UserStrategy.json`
Defines global behavior policy:
- success vs humanization balance
- AI direct execution
- human notification intents

3. `Config/IntentMappings/<platform>_intents.json`
Maps unified intents to platform operation sequences and fallback intents.

4. `Config/Operations/<platform>_operations.json`
Platform-specific executable steps for `ActionExecutor`.

5. `Modules/SessionRunner.cs`
Runtime flow:
- action -> intent
- intent fallback resolution
- intent -> operation sequence
- execute via `ActionExecutor`
- optional human prompt log/vars

## Current rollout

Implemented for:

- `reddit`
- `instagram`

Core intent-first strategy:

- browse_feed
- open_post
- read_post
- read_comments
- reply_post
- reply_comment

Extended intents remain configurable and can fallback to core intents.

## Add a new app

1. Add app config in `Config/PlatformsConfig.json`.
2. Add operation steps in `Config/Operations/<app>_operations.json`.
3. Add intent mapping in `Config/IntentMappings/<app>_intents.json`.
4. Add device mapping in `Config/device_app_mapping.json`.
5. Validate in ZennoDroid test flow.
