
## BehaviorModel Schema

The BehaviorModel abstraction describes an app's pages, UI elements, and behaviors in a platform-agnostic structure so that downstream generators can emit DPS configuration files.

### Top-level structure

```json
{
  "platform_key": "reddit",
  "version": "1.0",
  "source": {
    "web_hints": "web_behavior_hints/reddit.json",
    "vision": "screens/reddit/",
    "manual_notes": "notepads/.../annotations.json"
  },
  "pages": [ ... ],
  "elements": [ ... ],
  "behaviors": [ ... ],
  "metadata": {
    "generated_at": "2026-03-17T00:00:00Z",
    "confidence": 0.82,
    "notes": "Any global comments"
  }
}
```

### Pages

Each page entry captures the semantic role of an app screen:

```json
{
  "page_id": "feed_home",
  "semantic_role": "feed",
  "display_name": "Reddit Home Feed",
  "signature": {
    "selectors": [
      { "strategy": "resource-id", "value": "feed_container" },
      { "strategy": "text", "value": "Home" }
    ]
  },
  "screenshot": "screens/reddit/feed_home.png",
  "notes": "Scrollable vertical feed",
  "confidence": 0.9
}
```

### Elements

Elements describe actionable UI components discovered via vision analysis or manual hints.

```json
{
  "element_id": "post_unit",
  "page_id": "feed_home",
  "role": "content_card",
  "selector_hints": [
    { "strategy": "resource-id", "value": "post_unit" },
    { "strategy": "xpath", "value": "//android.view.ViewGroup[@content-desc='post']" }
  ],
  "preconditions": {
    "requires_scroll": false
  },
  "annotations": [
    {
      "source": "vision",
      "region": [120, 320, 1020, 1620],
      "note": "Primary card with title and subreddit"
    }
  ],
  "confidence": 0.85
}
```

### Behaviors

Behaviors map high-level intents (browse, read_post, like, etc.) to sequences of operations referencing elements.

```json
{
  "behavior_id": "read_post",
  "semantic_role": "read_post",
  "priority": 0.8,
  "steps": [
    {
      "action": "Locate",
      "element_id": "post_unit",
      "page_id": "feed_home",
      "description": "Select a visible post"
    },
    {
      "action": "Tap",
      "element_id": "post_unit",
      "wait_after_sec": 2
    },
    {
      "action": "VerifyPage",
      "page_id": "post_detail"
    },
    {
      "action": "Scroll",
      "page_id": "post_detail",
      "direction": "down",
      "distance": 400
    },
    {
      "action": "Wait",
      "seconds": 4
    },
    {
      "action": "Back",
      "page_id": "post_detail",
      "target_page": "feed_home"
    }
  ],
  "expected_page": "feed_home",
  "success_metrics": {
    "min_duration_sec": 6,
    "max_duration_sec": 40
  },
  "confidence": 0.78,
  "status": "complete"  // or "incomplete" with "missing_elements": []
}
```

### Metadata fields

- `confidence`: 0-1 indicator of how certain the system is about this page/element/behavior.
- `source`: indicates whether the information came from web docs, vision, manual annotations, or combined.
- `status`: `complete` vs `incomplete`, along with `missing_elements` list for review.

### Minimal requirements

A valid BehaviorModel must contain:
1. At least one `feed` or `home` page.
2. Elements covering navigation, content cards, and at least one interaction (like/comment).
3. Behaviors covering the core user journey: browse → read_post → back_to_feed. Additional behaviors (like/comment/follow/toggle settings) are optional but recommended.

### Example: Partial BehaviorModel for Reddit

```json
{
  "platform_key": "reddit",
  "version": "1.0",
  "pages": [
    {
      "page_id": "feed_home",
      "semantic_role": "feed",
      "display_name": "Reddit Home Feed",
      "signature": {
        "selectors": [
          { "strategy": "resource-id", "value": "feed_container" },
          { "strategy": "text", "value": "Home" }
        ]
      },
      "screenshot": "screens/reddit/feed_home.png",
      "confidence": 0.93
    },
    {
      "page_id": "post_detail",
      "semantic_role": "post_detail",
      "display_name": "Post Detail",
      "signature": {
        "selectors": [
          { "strategy": "resource-id", "value": "post_body" }
        ]
      },
      "screenshot": "screens/reddit/post_detail.png",
      "confidence": 0.88
    },
    {
      "page_id": "settings_main",
      "semantic_role": "settings",
      "display_name": "Settings",
      "signature": {
        "selectors": [
          { "strategy": "text", "value": "Dark mode" }
        ]
      },
      "screenshot": "screens/reddit/settings_main.png",
      "confidence": 0.75
    }
  ],
  "elements": [
    {
      "element_id": "post_unit",
      "page_id": "feed_home",
      "role": "content_card",
      "selector_hints": [
        { "strategy": "resource-id", "value": "post_unit" }
      ],
      "confidence": 0.9
    },
    {
      "element_id": "upvote_button",
      "page_id": "feed_home",
      "role": "like_button",
      "selector_hints": [
        { "strategy": "resource-id", "value": "upvote_button" }
      ],
      "confidence": 0.84
    },
    {
      "element_id": "back_button",
      "page_id": "post_detail",
      "role": "nav_back",
      "selector_hints": [
        { "strategy": "resource-id", "value": "back_button" }
      ],
      "confidence": 0.81
    },
    {
      "element_id": "dark_mode_toggle",
      "page_id": "settings_main",
      "role": "toggle_dark_mode",
      "selector_hints": [
        { "strategy": "text", "value": "Dark mode" }
      ],
      "confidence": 0.68
    }
  ],
  "behaviors": [
    {
      "behavior_id": "browse",
      "semantic_role": "browse",
      "steps": [
        { "action": "Locate", "element_id": "post_unit", "page_id": "feed_home" },
        { "action": "Wait", "seconds": 5 },
        { "action": "Scroll", "page_id": "feed_home", "direction": "down", "distance": 900 },
        { "action": "Wait", "seconds": 2 }
      ],
      "expected_page": "feed_home",
      "confidence": 0.77,
      "status": "complete"
    },
    {
      "behavior_id": "read_post",
      "semantic_role": "read_post",
      "steps": [
        { "action": "Locate", "element_id": "post_unit", "page_id": "feed_home" },
        { "action": "Tap", "element_id": "post_unit" },
        { "action": "VerifyPage", "page_id": "post_detail" },
        { "action": "Scroll", "page_id": "post_detail", "direction": "down", "distance": 400 },
        { "action": "Wait", "seconds": 4 },
        { "action": "Back", "page_id": "post_detail", "target_page": "feed_home" }
      ],
      "expected_page": "feed_home",
      "confidence": 0.78,
      "status": "complete"
    },
    {
      "behavior_id": "like",
      "semantic_role": "like_post",
      "steps": [
        { "action": "Locate", "element_id": "upvote_button", "page_id": "feed_home" },
        { "action": "Tap", "element_id": "upvote_button" },
        { "action": "Wait", "seconds": 2 }
      ],
      "expected_page": "feed_home",
      "confidence": 0.72,
      "status": "complete"
    },
    {
      "behavior_id": "toggle_dark_mode",
      "semantic_role": "toggle_dark_mode",
      "steps": [
        { "action": "Locate", "element_id": "nav_settings_tab", "page_id": "feed_home" },
        { "action": "Tap", "element_id": "nav_settings_tab" },
        { "action": "Wait", "seconds": 2 },
        { "action": "Locate", "element_id": "dark_mode_toggle", "page_id": "settings_main" },
        { "action": "Tap", "element_id": "dark_mode_toggle" },
        { "action": "Wait", "seconds": 1 }
      ],
      "expected_page": "settings_main",
      "confidence": 0.6,
      "status": "incomplete",
      "missing_elements": ["nav_settings_tab"],
      "notes": "Needs human confirmation for settings entry"
    }
  ]
}
```

This structure will be the canonical input for BehaviorModel→operations and operations→step plans generators.
