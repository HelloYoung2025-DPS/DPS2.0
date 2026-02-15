# Multi-Platform Social Media Automation Framework

> **DPS v4.5 - Multi-Platform Edition**  
> Version: 1.2  
> Last Updated: 2026-02-14

---

## Overview

The Multi-Platform Social Media Automation Framework extends DPS v4.5 to support **Reddit, Instagram, TikTok, and Facebook** with a unified architecture that balances code reuse with platform-specific flexibility.

### Key Features

- **Unified Core Framework**: Shared humanization, UI location, and error recovery
- **Platform Modules**: Platform-specific implementations for each social network
- **Rate Limiting**: Automatic enforcement of platform-specific rate limits
- **Humanized Behavior**: 4 behavior profiles (casual, active, lurker, new_user)
- **Multi-Strategy UI Location**: Resource-ID → XPath → Image fallback
- **Automatic Error Recovery**: Max 3 retries with exponential backoff
- **Cross-Platform Personas**: Unified persona schema with platform-specific preferences

---

## Architecture

### Hybrid Mode: Shared Core + Platform Modules

```
┌─────────────────────────────────────────────────────────────┐
│                      Core Framework                          │
│  (Shared across all platforms)                               │
├─────────────────────────────────────────────────────────────┤
│  • HumanizationEngine.cs  - Behavior profiles & timing       │
│  • UILocator.cs           - Multi-strategy element finding   │
│  • ErrorRecovery.cs       - Retry logic & error tracking     │
│  • PlatformBase.cs        - Standard operation interface     │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                    Platform Modules                          │
│  (Platform-specific implementations)                         │
├─────────────────────────────────────────────────────────────┤
│  Reddit      │ Instagram   │ TikTok      │ Facebook         │
│  Module      │ Module      │ Module      │ Module           │
│              │             │ (Phase 2)   │ (Phase 2)        │
└─────────────────────────────────────────────────────────────┘
```

### Standard Operations

Every platform module implements 6 standard operations:

| Operation | Description | Returns |
|-----------|-------------|---------|
| `Initialize` | Open app, verify state, initialize tracking | `{success, message, data, duration_ms}` |
| `Browse` | Scroll feed, detect posts | `{success, message, data, duration_ms}` |
| `Like` | Like/heart a post | `{success, message, data, duration_ms}` |
| `Comment` | Write and submit comment | `{success, message, data, duration_ms}` |
| `Follow` | Follow a user | `{success, message, data, duration_ms}` |
| `Share` | Share post to story/DM | `{success, message, data, duration_ms}` |

---

## Platform Configurations

### Reddit

- **Rate Limits**: 120 actions/hour, 60 likes/hour, 30 comments/hour
- **Package**: `com.reddit.frontpage`
- **UI Selectors**: `post_unit`, `upvote_button`, `comment_button`, etc.
- **Status**: ✅ Implemented

### Instagram

- **Rate Limits**: 60 actions/hour, 30 likes/hour, 15 comments/hour, 10 follows/hour
- **Package**: `com.instagram.android`
- **UI Selectors**: `media_container`, `like_button`, `comment_button`, etc.
- **Status**: ✅ Implemented

### TikTok

- **Rate Limits**: 100 actions/hour, 50 likes/hour, 15 comments/hour
- **Package**: `com.zhiliaoapp.musically`
- **Status**: ⏳ Phase 2

### Facebook

- **Rate Limits**: 80 actions/hour, 35 likes/hour, 12 comments/hour
- **Package**: `com.facebook.katana`
- **Status**: ⏳ Phase 2

---

## Usage Guide

### 1. Device-Platform Mapping

Configure which devices use which platforms in `Config/device_app_mapping.json`:

```json
{
  "devices": [
    {
      "device_id": "device_001",
      "platform": "reddit",
      "enabled": true
    },
    {
      "device_id": "device_002",
      "platform": "instagram",
      "enabled": true
    }
  ]
}
```

### 2. Platform Configuration

Each platform has its own configuration in `Config/PlatformsConfig.json`:

```json
{
  "platforms": {
    "instagram": {
      "name": "Instagram",
      "package_name": "com.instagram.android",
      "enabled": true,
      "rate_limits": {
        "max_actions_per_hour": 60,
        "max_likes_per_hour": 30,
        "max_comments_per_hour": 10,
        "max_follows_per_hour": 20
      },
      "ui_selectors": {
        "post_unit": {
          "strategy": "resource-id",
          "value": "media_container",
          "fallback_strategy": "class",
          "fallback_value": "android.widget.FrameLayout"
        },
        "like_button": {
          "strategy": "resource-id",
          "value": "like_button",
          "fallback_strategy": "content-desc",
          "fallback_value": "Like"
        },
        "comment_button": {
          "strategy": "resource-id",
          "value": "comment_button",
          "fallback_strategy": "content-desc",
          "fallback_value": "Comment"
        }
      }
    }
  }
}
```

### 3. Persona Configuration

Extend personas with platform-specific preferences in `Personas/{persona_name}.json`:

```json
{
  "persona_id": "young_mom_001",
  "platform_preferences": {
    "reddit": {
      "active_hours": [9, 12, 14, 20],
      "favorite_subreddits": ["r/Parenting", "r/BabyBumps"],
      "engagement_rate": 0.3
    },
    "instagram": {
      "active_hours": [10, 13, 19, 21],
      "favorite_hashtags": ["#momlife", "#babygear"],
      "engagement_rate": 0.5
    }
  }
}
```

### 4. Running a Session

The SessionRunner automatically:
1. Reads `device_id` from project variables
2. Looks up platform in `device_app_mapping.json`
3. Loads the appropriate platform module
4. Executes operations with rate limiting and humanization

```csharp
// In MainProject.droid - Own Code block
// SessionRunner.cs handles everything automatically
string result = RunSession(project, instance);
```

---

## Core Modules

### HumanizationEngine.cs

Provides 4 behavior profiles with realistic timing and variance:

| Profile | Delay Range | Tap Offset | Swipe Curve | Use Case |
|---------|-------------|------------|-------------|----------|
| `casual` | 2000-5000ms | ±15px | 0.4 | Average users (default) |
| `active` | 800-2000ms | ±5px | 0.2 | Fast, engaged users |
| `lurker` | 5000-12000ms | ±10px | 0.3 | Read-only, minimal interaction |
| `new_user` | 3000-8000ms | ±20px | 0.5 | New accounts, cautious behavior |

**Functions**:
- `GetProfileConfig(profileName)` - Get profile parameters
- `HumanizedDelay(profile, baseMs)` - Add realistic delay
- `HumanizedTap(profile, x, y)` - Tap with human-like offset
- `HumanizedSwipe(profile, x1, y1, x2, y2)` - Swipe with curve
- `ShouldTriggerProbabilistic(probability)` - Random decision

### UILocator.cs

Multi-strategy UI element location with fallback:

**Strategy Chain**:
1. **Resource-ID** (fastest, most reliable)
2. **XPath** (fallback for complex queries)
3. **Image** (last resort for visual matching)

**Functions**:
- `FindByResourceId(layout, resourceId)` - Find by resource-id
- `FindByXPath(layout, xpath)` - Find by XPath (stub)
- `FindByImage(screenshot, templatePath)` - Find by image (stub)
- `ConvertToRelative(x, y, screenWidth, screenHeight)` - Absolute → Relative
- `ConvertToAbsolute(xPercent, yPercent, screenWidth, screenHeight)` - Relative → Absolute

### ErrorRecovery.cs

Automatic retry with exponential backoff:

**Retry Schedule**:
- Attempt 1: Immediate
- Attempt 2: +2 seconds
- Attempt 3: +4 seconds
- Attempt 4: +8 seconds

**Error Types**:
- `app_crash` - App crashed or unresponsive
- `ui_not_found` - UI element not found
- `network_error` - Network timeout or failure
- `timeout` - Operation timeout

**Functions**:
- `TryWithRetry(action, maxRetries)` - Retry action
- `TryWithRetryFunc(func, maxRetries)` - Retry function
- `RecoverFromError(errorType)` - Handle specific error
- `IsErrorThresholdExceeded(errorType)` - Check error count

---

## Rate Limiting

### How It Works

Each platform module tracks actions per hour:

```csharp
// Check before action
if (!CheckRateLimit("likes", 30)) {
    return CreateResult(false, "Rate limit exceeded", null, 0);
}

// Execute action
PerformLike();

// Increment counter
IncrementRateLimit("likes");
```

### Automatic Reset

Counters automatically reset every hour:

```csharp
string currentHour = DateTime.Now.ToString("yyyy-MM-dd HH:00:00");
if (hourStart != currentHour) {
    // Reset all counters
    SetVar("instagram_actions_this_hour", "0");
    SetVar("instagram_likes_this_hour", "0");
    // ...
}
```

---

## Testing

### Integration Tests

Run comprehensive tests with `ZDProjects/Tests/MultiPlatform_IntegrationTest.cs`:

| Test Scenario | Description |
|---------------|-------------|
| `reddit_basic` | Verify Reddit module structure and config |
| `instagram_basic` | Verify Instagram module structure and config |
| `platform_switching` | Test device-platform mapping |
| `rate_limit` | Verify rate limit enforcement |
| `error_recovery` | Verify error recovery functions |
| `config_loading` | Verify all configs and modules exist |

**Usage**:
```csharp
// Set test scenario
SetVar("test_scenario", "instagram_basic");

// Run test
// Execute MultiPlatform_IntegrationTest.cs

// Check result
string result = GetVar("test_result", ""); // "PASS" or "FAIL"
```

---

## File Structure

```
DPS_v4.5/
├── Core/
│   ├── ScriptHelpers.cs           ✅ ZD API 封装
│   ├── HumanizationEngine.cs      ✅ Shared humanization
│   ├── UILocator.cs               ✅ Multi-strategy UI location
│   ├── ErrorRecovery.cs           ✅ Automatic error recovery
│   └── PlatformBase.cs            ✅ Platform interface
├── Modules/
│   ├── Core/                      ✅ Core libraries
│   ├── SessionRunner.cs           ✅ Multi-platform session runner
│   ├── MemoryManager.cs           ✅ Interaction dedup & recording
│   └── RuleEngine.cs              ✅ Post evaluation gating
├── Config/
│   ├── PlatformsConfig.json       ✅ Platform configurations
│   ├── DecisionConfig.json        ✅ Rules/fatigue/memory config
│   ├── Operations/                ✅ Platform operation steps
│   └── Selectors/                 ✅ UI element selectors
├── docs/
│   ├── 子项目调用架构.md           ✅ Architecture (updated)
│   ├── PersonaSchema_MultiPlatform.md ✅ Persona schema
│   └── MultiPlatformFramework.md  ✅ This document
└── ZDProjects/
    ├── ModuleLoader.cs            ✅ Module loader with cache
    └── *_OwnCode.cs (10)          ✅ ZD Own Code entries
```

---

## Best Practices

### 1. Rate Limit Safety

Always check rate limits before actions:

```csharp
if (!CheckRateLimit("actions", maxPerHour)) {
    return CreateResult(false, "Rate limited", null, 0);
}
```

### 2. Humanization

Use HumanizationEngine for all timing and interactions:

```csharp
// Bad
Thread.Sleep(2000);
input.Tap(x, y);

// Good
HumanizedDelay(profile, 2000);
HumanizedTap(profile, x, y);
```

### 3. Error Handling

Wrap operations in ErrorRecovery:

```csharp
var result = TryWithRetryFunc(() => {
    return PerformOperation();
}, maxRetries: 3);
```

### 4. UI Location

Use config-driven selectors with `GetSelectorValue`:

```csharp
// Read selector from PlatformsConfig.json (nested object format)
string selectorsJson = GetJsonSection(platformConfig, "ui_selectors");
string likeSelector = GetSelectorValue(selectorsJson, "like_button", "like_button");

// Use selector to find element
var bounds = FindByResourceId(layout, likeSelector);
```

> **v4.5.1**: `ui_selectors` uses nested objects `{"strategy":"...","value":"..."}`. Use `GetSelectorValue()` to extract the `value` field correctly.

---

## Troubleshooting

### Rate Limit Exceeded

**Symptom**: Actions return "Rate limit exceeded"

**Solution**: 
- Check `{platform}_actions_this_hour` variable
- Wait for hourly reset
- Adjust `max_actions_per_hour` in PlatformsConfig.json

### UI Element Not Found

**Symptom**: Operations fail with "not found"

**Solution**:
- Verify resource-id in `ui_selectors` config
- Check if app UI has changed
- Add XPath or image fallback

### Platform Module Not Loading

**Symptom**: SessionRunner fails to load module

**Solution**:
- Verify device_id in device_app_mapping.json
- Check platform module file exists
- Verify platform is enabled in PlatformsConfig.json

---

## Extending to New Platforms

### Step 1: Add Platform Config

Add to `Config/PlatformsConfig.json`:

```json
{
  "platforms": {
    "newplatform": {
      "name": "NewPlatform",
      "package_name": "com.newplatform.app",
      "enabled": true,
      "rate_limits": { ... },
      "ui_selectors": { ... }
    }
  }
}
```

### Step 2: Create Platform Module

Create `Platforms/NewPlatform/NewPlatformModule.cs`:

```csharp
// Implement 6 standard operations:
Func<dynamic, Dictionary<string, object>> Initialize = ...
Func<dynamic, Dictionary<string, object>> Browse = ...
Func<dynamic, Dictionary<string, object>> Like = ...
Func<dynamic, Dictionary<string, object>> Comment = ...
Func<dynamic, Dictionary<string, object>> Follow = ...
Func<dynamic, Dictionary<string, object>> Share = ...
```

### Step 3: Update Device Mapping

Add devices to `Config/device_app_mapping.json`:

```json
{
  "device_id": "device_005",
  "platform": "newplatform",
  "enabled": true
}
```

### Step 4: Test

Run integration tests to verify setup.

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.2 | 2026-02-14 | Fixed file structure (removed phantom Platforms/), added MemoryManager/RuleEngine, Config/Operations+Selectors |
| 1.1 | 2026-02-13 | Fixed ui_selectors to nested object format, updated behavior profiles to casual/active/lurker/new_user |
| 1.0 | 2026-02-07 | Initial multi-platform release with Reddit and Instagram |

---

*For architecture details, see `docs/子项目调用架构.md`*  
*For persona schema, see `docs/PersonaSchema_MultiPlatform.md`*
