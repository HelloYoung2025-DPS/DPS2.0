# Persona Schema - Multi-Platform Extension

**Version**: 4.5.2  
**Date**: 2026-02-14  
**Purpose**: Document how to extend persona schema for multi-platform social media support

---

## Overview

This document describes the extensions to the DPS v4.5 persona schema to support multiple social media platforms (Reddit, Instagram, TikTok, Facebook). The extensions maintain backward compatibility while adding platform-specific behavior configurations.

---

## Schema Extensions

### 1. Extended `digital_behavior` Section

Add a new `platform_preferences` object to the existing `digital_behavior` section:

```json
{
  "digital_behavior": {
    "platform_usage": {
      "primary_platforms": ["Instagram", "Reddit", "What to Expect"],
      "usage_frequency": "moderate",
      "preferred_content_format": "mixed"
    },
    "usage_patterns": {
      "peak_hours": ["07:00-08:00", "20:00-22:00"],
      "session_triggers": ["morning coffee", "toddler nap time", "bedtime wind-down"],
      "average_session_minutes": 15,
      "sessions_per_day": 6
    },
    
    "platform_preferences": {
      "reddit": {
        "enabled": true,
        "humanization_profile": "casual",
        "action_weights": {
          "browse": 40,
          "like": 30,
          "comment": 15,
          "follow": 10,
          "share": 5
        },
        "peak_hours": ["07:00-08:00", "20:00-22:00"],
        "session_duration_minutes": 15,
        "sessions_per_day": 4,
        "preferred_communities": ["BabyBumps", "Mommit", "Parenting"],
        "content_focus": ["pregnancy_tips", "toddler_behavior", "product_recommendations"]
      },
      
      "instagram": {
        "enabled": true,
        "humanization_profile": "lurker",
        "action_weights": {
          "browse": 50,
          "like": 35,
          "comment": 5,
          "follow": 8,
          "share": 2
        },
        "peak_hours": ["12:00-13:00", "21:00-22:30"],
        "session_duration_minutes": 20,
        "sessions_per_day": 3,
        "preferred_hashtags": ["#pregnancy", "#momlife", "#toddlermom", "#secondpregnancy"],
        "content_focus": ["visual_inspiration", "lifestyle", "product_discovery"]
      },
      
      "tiktok": {
        "enabled": false,
        "humanization_profile": "active",
        "action_weights": {
          "browse": 60,
          "like": 25,
          "comment": 5,
          "follow": 8,
          "share": 2
        },
        "peak_hours": ["19:00-20:00", "22:00-23:00"],
        "session_duration_minutes": 25,
        "sessions_per_day": 2,
        "preferred_hashtags": ["#momtok", "#pregnancytiktok", "toddlerlife"],
        "content_focus": ["entertainment", "quick_tips", "relatable_content"]
      },
      
      "facebook": {
        "enabled": false,
        "humanization_profile": "casual",
        "action_weights": {
          "browse": 45,
          "like": 30,
          "comment": 10,
          "follow": 10,
          "share": 5
        },
        "peak_hours": ["08:00-09:00", "20:00-21:00"],
        "session_duration_minutes": 18,
        "sessions_per_day": 3,
        "preferred_groups": ["Local Moms Group", "Pregnancy Support"],
        "content_focus": ["community_support", "local_events", "marketplace"]
      }
    }
  }
}
```

---

## Field Definitions

### Platform-Level Fields

| Field | Type | Description | Example |
|-------|------|-------------|---------|
| `enabled` | boolean | Whether this platform is active for the persona | `true` |
| `humanization_profile` | string | Behavior profile from HumanizationEngine | `"casual"`, `"active"`, `"lurker"`, `"new_user"` |
| `action_weights` | object | Probability distribution for actions (must sum to 100) | See below |
| `peak_hours` | array[string] | Time ranges when persona is most active | `["07:00-08:00", "20:00-22:00"]` |
| `session_duration_minutes` | integer | Average session length | `15` |
| `sessions_per_day` | integer | Number of sessions per day | `4` |
| `preferred_communities` | array[string] | Platform-specific communities/groups | `["BabyBumps", "Mommit"]` |
| `preferred_hashtags` | array[string] | Hashtags to follow (Instagram/TikTok) | `["#pregnancy", "#momlife"]` |
| `preferred_groups` | array[string] | Groups to participate in (Facebook) | `["Local Moms Group"]` |
| `content_focus` | array[string] | Content themes persona seeks | `["pregnancy_tips", "product_recommendations"]` |

### Action Weights

Action weights define the probability distribution for different actions. They must sum to 100.

```json
"action_weights": {
  "browse": 40,    // Scroll feed, view content (no interaction)
  "like": 30,      // Like/upvote posts
  "comment": 15,   // Write comments
  "follow": 10,    // Follow users/communities
  "share": 5       // Share content
}
```

**Platform-Specific Patterns**:
- **Reddit**: Higher comment weight (more discussion-focused)
- **Instagram**: Higher browse/like, lower comment (visual platform)
- **TikTok**: Highest browse weight (fast-paced content)
- **Facebook**: Balanced weights (community-focused)

---

## Humanization Profiles

Each platform can use a different humanization profile from `Core/HumanizationEngine.cs`:

### Profile Characteristics

| Profile | Speed | Variance | Tap Offset | Swipe Bending | Accidental Actions | Best For |
|---------|-------|----------|------------|---------------|-------------------|----------|
| `active` | Fast (0.6x) | Low (15%) | 5px | 10 | Very rare (1-2%) | TikTok, quick browsing |
| `casual` | Normal (1.0x) | Medium (25%) | 15px | 30 | Occasional (2-3%) | Reddit, general use |
| `lurker` | Slow (1.4x) | High (35%) | 20px | 50 | Common (5-8%) | Instagram, reading posts |
| `new_user` | Variable (1.2x) | Very High (45%) | 25px | 60 | Frequent (8-12%) | New accounts, cautious |

### Profile Selection Guidelines

- **Reddit**: `casual` or `lurker` (discussion requires reading)
- **Instagram**: `lurker` (visual content, longer viewing)
- **TikTok**: `active` (fast-paced, quick scrolling)
- **Facebook**: `casual` (mixed content types)

---

## Cross-Platform Behavior Consistency

### Consistency Rules

1. **Peak Hours Alignment**
   - Peak hours should align with persona's `usage_patterns.peak_hours`
   - Platform-specific variations allowed (±1 hour)
   - Example: If general peak is 20:00-22:00, Instagram might be 21:00-22:30

2. **Session Duration Correlation**
   - Total daily session time across platforms should match persona's lifestyle
   - Formula: `sum(sessions_per_day * session_duration_minutes) ≈ usage_patterns.average_session_minutes * usage_patterns.sessions_per_day`

3. **Action Weight Consistency**
   - Action weights should reflect persona's `community_engagement.lurker_vs_poster`
   - Lurkers: Higher browse, lower comment/share
   - Posters: More balanced distribution

4. **Content Focus Alignment**
   - `content_focus` should align with persona's `interests_and_hobbies` and `current_concerns`
   - Example: Pregnant persona → pregnancy-related content across all platforms

### Example Consistency Check

For persona Sarah (from R58M4816G8Y.json):
- **Personality**: Organized, empathetic, cautious
- **Community Engagement**: Occasional poster
- **Peak Hours**: 07:00-08:00 (morning coffee), 20:00-22:00 (bedtime wind-down)

**Consistent Configuration**:
```json
{
  "reddit": {
    "humanization_profile": "casual",
    "action_weights": { "browse": 40, "like": 30, "comment": 15, "follow": 10, "share": 5 },
    "peak_hours": ["07:00-08:00", "20:00-22:00"]
  },
  "instagram": {
    "humanization_profile": "lurker",
    "action_weights": { "browse": 50, "like": 35, "comment": 5, "follow": 8, "share": 2 },
    "peak_hours": ["12:00-13:00", "21:00-22:30"]
  }
}
```

**Why This Works**:
- Reddit gets more comments (discussion-focused, matches "occasional poster")
- Instagram gets more browse/like (visual platform, less commenting)
- Peak hours align with general usage patterns
- Both use moderate-to-slow profiles (matches "cautious" personality)

---

## Rate Limit Compliance

Each platform has different rate limits defined in `Config/PlatformsConfig.json`:

| Platform | Max Actions/Hour | Max Likes/Hour | Max Comments/Hour | Max Follows/Hour |
|----------|------------------|----------------|-------------------|------------------|
| Reddit | 120 | 40 | 20 | 15 |
| Instagram | 60 | 30 | 10 | 20 |
| TikTok | 100 | 50 | 15 | 25 |
| Facebook | 80 | 35 | 12 | 15 |

**Action Weight Calculation**:
The framework automatically adjusts action execution to stay within rate limits:

```
actions_per_session = session_duration_minutes * (max_actions_per_hour / 60)
action_probability = action_weight / 100
expected_action_count = actions_per_session * action_probability
```

**Example** (Instagram, 20-minute session):
```
actions_per_session = 20 * (60 / 60) = 20 actions
like_probability = 35 / 100 = 0.35
expected_likes = 20 * 0.35 = 7 likes
```

This stays well within Instagram's 30 likes/hour limit.

---

## Migration Guide

### Extending Existing Personas

To add multi-platform support to an existing persona:

1. **Read existing persona JSON**
2. **Add `platform_preferences` to `digital_behavior`**
3. **Configure enabled platforms** (start with 1-2)
4. **Set humanization profiles** based on personality
5. **Define action weights** based on engagement style
6. **Align peak hours** with existing usage patterns
7. **Validate consistency** using rules above

### Example Migration Script

```csharp
// Read existing persona
string personaJson = CoreHelper.ReadFile(personaPath);
var persona = JsonHelper.Parse(personaJson);

// Extract relevant fields
string engagementStyle = JsonHelper.Get(persona, "digital_behavior.community_engagement.lurker_vs_poster");
string peakHours = JsonHelper.Get(persona, "digital_behavior.usage_patterns.peak_hours");

// Determine action weights based on engagement
var actionWeights = engagementStyle == "lurker" 
    ? new { browse = 60, like = 25, comment = 5, follow = 8, share = 2 }
    : new { browse = 40, like = 30, comment = 15, follow = 10, share = 5 };

// Add platform preferences
var platformPrefs = new {
    reddit = new {
        enabled = true,
        humanization_profile = "casual",
        action_weights = actionWeights,
        peak_hours = peakHours
    }
};

// Merge and save
JsonHelper.AddField(persona, "digital_behavior.platform_preferences", platformPrefs);
CoreHelper.WriteFile(personaPath, JsonHelper.Stringify(persona));
```

---

## Validation Rules

### Required Fields
- `enabled` (boolean)
- `humanization_profile` (must be one of: casual, active, lurker, new_user)
- `action_weights` (must sum to 100)
- `peak_hours` (array, at least 1 time range)
- `session_duration_minutes` (integer, 5-60)
- `sessions_per_day` (integer, 1-10)

### Validation Checks
1. **Action weights sum to 100**
2. **Peak hours format**: "HH:MM-HH:MM"
3. **Humanization profile exists** in HumanizationEngine
4. **Session duration realistic** (5-60 minutes)
5. **Total daily time reasonable** (< 4 hours across all platforms)
6. **At least one platform enabled**

---

## References

- **Core Modules**: `Core/HumanizationEngine.cs`, `Core/PlatformBase.cs`
- **Platform Config**: `Config/PlatformsConfig.json`
- **Example Persona**: `Persons/R58M4816G8Y.json`
- **Architecture**: `docs/子项目调用架构.md`

---

## Change Log

| Date | Version | Changes |
|------|---------|---------|
| 2026-02-14 | 1.1 | Updated version/date, fixed doc path references |
| 2026-02-07 | 1.0 | Initial multi-platform schema extension |
