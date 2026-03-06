# DPS v4.5 TechManual_技术手册

> 更新: 2026-03-05 (v4.5.10)
> 来源: MultiPlatformFramework.md + SessionRunner使用说明.md + UnifiedIntentArchitecture.md + PersonaSchema_MultiPlatform.md + GLOSSARY.md + if_exists_usage.md + PLAYWRIGHT_ANDROID_ANALYSIS.md

---

## Part 1: 系统架构

### 1.1 总体架构

Multi-Platform Social Media Automation Framework 将 DPS v4.5 扩展为支持 **Reddit, Instagram, BabyCenter, TikTok, Facebook** 的统一架构，兼顾代码复用和平台特异性。

#### 核心特性

- **Unified Core Framework**: 共享人性化引擎、UI 定位、错误恢复
- **Config-Driven Execution**: 平台行为由 `operations.json + intents.json` 定义
- **Rate Limiting**: 自动执行平台级速率限制
- **Humanized Behavior**: 4 种行为配置文件 (`speed_demon`, `casual`, `deep_reader`, `distracted`)
- **Multi-Strategy UI Location**: Resource-ID → XPath → Image fallback
- **Automatic Error Recovery**: 最多 3 次重试 + 指数退避
- **Cross-Platform Personas**: 统一 persona schema + 平台特定偏好
- **Intent-Based Execution**: v4.5.6+ 统一意图/ZDCommand/ZDResult 框架

#### v4.5.8 稳定性更新

- Session success gate 升级: 有效动作成功率 `>= 95%` + 最低成功动作数阈值
- ActionExecutor `find` 现在回填语义字段（`post_title/post_body/...`）供 RuleEngine 评分
- 状态一致性: 跳过状态统一为 `SKIP`，核心 I/O 增强 per-file 错误处理

#### 混合模式: Shared Core + Platform Adapters

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
│                    Platform Adapters                         │
│  (config-driven operations + optional C# modules)            │
├─────────────────────────────────────────────────────────────┤
│  Reddit      │ Instagram   │ BabyCenter  │ TikTok      │ Facebook         │
│  Module      │ Module      │ Config      │ (Phase 2)   │ (Phase 2)        │
└─────────────────────────────────────────────────────────────┘
```

#### 标准操作

每个启用的平台应提供 6 个标准操作（通过模块或 `operations.json`）：

| Operation | Description | Returns |
|-----------|-------------|---------|
| `Initialize` | Open app, verify state, initialize tracking | `{success, message, data, duration_ms}` |
| `Browse` | Scroll feed, detect posts | `{success, message, data, duration_ms}` |
| `Like` | Like/heart a post | `{success, message, data, duration_ms}` |
| `Comment` | Write and submit comment | `{success, message, data, duration_ms}` |
| `Follow` | Follow a user | `{success, message, data, duration_ms}` |
| `Share` | Share post to story/DM | `{success, message, data, duration_ms}` |

---

### 1.2 统一意图架构 (Unified Intent Architecture)

#### 目标

让一个执行层跨多个 APP 工作，将"做什么"和"怎么做"分离：

- **what to do** (intent) — 意图
- **how to do it** (platform operations) — 平台操作

#### 5 层结构

1. **`Config/ActionCatalog.json`** — 定义跨 APP 意图（core + extended）

2. **`Config/UserStrategy.json`** — 定义全局行为策略:
   - success vs humanization 平衡
   - AI 直接执行
   - human notification intents

3. **`Config/IntentMappings/<platform>_intents.json`** — 将统一意图映射到平台操作序列和回退意图

4. **`Config/Operations/<platform>_operations.json`** — 平台特定的可执行步骤，供 `ActionExecutor` 使用

5. **`Modules/SessionRunner.cs`** — 运行时流程:
   - action → intent
   - intent fallback resolution
   - intent → operation sequence
   - execute via `ActionExecutor`
   - optional human prompt log/vars

#### 当前已实现

已实现平台:
- `reddit`
- `instagram`
- `babycenter` (v4.5.7+, config-driven mode)

Core intent-first 策略:
- browse_feed, open_post, read_post, read_comments
- like_content, reply_post, reply_comment
- follow_entity, share_content

Extended intents 保持可配置，可回退到 core intents。

---

### 1.3 三层分离设计

```
┌──────────────────────────────────────────────────────────┐
│                    SessionRunner.cs                        │
│                  （平台无关的会话循环）                       │
│                                                          │
│  ┌─ 动作选择 ──────────────────────────────────────────┐ │
│  │  加权随机 → 疲劳调权 → RuleEngine 门控 → 去重检测     │ │
│  └──────────────────────────────────────────────────────┘ │
│                         │                                 │
│                    选中动作: "like"                        │
│                         │                                 │
│  ┌─ 意图解析 ──────────────────────────────────────────┐ │
│  │  动作→意图映射 → 回退链解析 → 操作序列生成             │ │
│  │  "like" → "like_content" → ["like"]                  │ │
│  └──────────────────────────────────────────────────────┘ │
│                         │                                 │
│  ┌─ 统一引擎执行 ──────────────────────────────────────┐ │
│  │  页面检测 → ActionExecutor 逐步执行 → 状态更新        │ │
│  └──────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────┘
```

| 层级 | 文件 | 职责 |
|------|------|------|
| **决策层（大脑）** | `{platform}_intents.json` | 定义"做什么"：意图名 → 操作序列 + 回退链 |
| **执行层（手）** | `{platform}_operations.json` | 定义"怎么做"：每个操作的具体 UI 步骤 |
| **协调层（身体）** | `SessionRunner.cs` | 会话循环、动作选择、疲劳管理、记忆去重 |

核心优势：**添加新 APP 不需要改任何 C# 代码，只需添加 JSON 配置文件**。

---

### 1.4 文件结构

```
DPS_v4.5/
├── Core/
│   ├── ScriptHelpers.cs           ✅ ZD API 封装
│   ├── HumanizationEngine.cs      ✅ Shared humanization
│   ├── UILocator.cs               ✅ Multi-strategy UI location
│   ├── ErrorRecovery.cs           ✅ Automatic error recovery
│   └── PlatformBase.cs            ✅ Platform interface
├── Modules/
│   ├── Core/                      ✅ Core libraries (Intent, ZDCommand, etc.)
│   ├── SessionRunner.cs           ✅ Multi-platform session runner
│   ├── MemoryManager.cs           ✅ Interaction dedup & recording
│   └── RuleEngine.cs              ✅ Post evaluation gating
├── Config/
│   ├── PlatformsConfig.json       ✅ Platform configurations
│   ├── Operations/                ✅ Platform operation steps
│   │   ├── reddit_operations.json
│   │   ├── instagram_operations.json
│   │   └── babycenter_operations.json
│   └── IntentMappings/            ✅ Intent mappings
│       ├── reddit_intents.json
│       ├── instagram_intents.json
│       └── babycenter_intents.json
├── ZDProjects/
│   ├── ModuleLoader.cs            ✅ Module loader with cache
│   ├── *_OwnCode.cs (10)          ✅ ZD Own Code entries
│   └── Tests/                     ✅ E2E test scripts
│       ├── Reddit_E2E_Test.cs
│       ├── Instagram_E2E_Test.cs
│       ├── BabyCenter_E2E_Test.cs
│       └── playwright_dps_test.js
└── Docs/
    ├── README.md
    ├── ConfigGuide_配置指南.md
    ├── TechManual_技术手册.md      ✅ 本文档
    ├── PLATFORM_MODULE_TEMPLATE.md
    ├── GIT_WORKFLOW.md
    └── Platforms/
        └── BabyCenter_APP_Guide.md
```

---

## Part 2: 核心模块

### 2.1 HumanizationEngine.cs

提供 4 种行为配置文件，模拟真实用户的时间和交互方差：

| Profile | Delay Range | Tap Offset | Swipe Curve | Use Case |
|---------|-------------|------------|-------------|----------|
| `casual` | 2000-5000ms | ±15px | 0.4 | Average users (default) |
| `speed_demon` | 800-2000ms | ±5px | 0.1 | Fast, engaged users |
| `deep_reader` | 5000-12000ms | ±10px | 0.2 | Read-heavy behavior |
| `distracted` | 3000-8000ms | ±20px | 0.4 | Erratic, interruption-prone behavior |

**Functions**:
- `GetProfileConfig(profileName)` - Get profile parameters
- `HumanizedDelay(profile, baseMs)` - Add realistic delay
- `HumanizedTap(profile, x, y)` - Tap with human-like offset
- `HumanizedSwipe(profile, x1, y1, x2, y2)` - Swipe with curve
- `ShouldTriggerProbabilistic(probability)` - Random decision

---

### 2.2 UILocator.cs

多策略 UI 元素定位，带回退链：

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

---

### 2.3 ErrorRecovery.cs

自动重试 + 指数退避：

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

### 2.4 Rate Limiting

每个平台模块跟踪每小时操作数：

```csharp
// 操作前检查
if (!CheckRateLimit("likes", 30)) {
    return CreateResult(false, "Rate limit exceeded", null, 0);
}

// 执行操作
PerformLike();

// 递增计数器
IncrementRateLimit("likes");
```

**自动重置** — 计数器每小时自动重置：

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

### 2.5 最佳实践

#### Rate Limit Safety
```csharp
if (!CheckRateLimit("actions", maxPerHour)) {
    return CreateResult(false, "Rate limited", null, 0);
}
```

#### Humanization
```csharp
// Bad
Thread.Sleep(2000);
input.Tap(x, y);

// Good
HumanizedDelay(profile, 2000);
HumanizedTap(profile, x, y);
```

#### Error Handling
```csharp
var result = TryWithRetryFunc(() => {
    return PerformOperation();
}, maxRetries: 3);
```

#### UI Location — Config-Driven Selectors
```csharp
// Read selector from PlatformsConfig.json (nested object format)
string selectorsJson = GetJsonSection(platformConfig, "ui_selectors");
string likeSelector = GetSelectorValue(selectorsJson, "like_button", "like_button");

// Use selector to find element
var bounds = FindByResourceId(layout, likeSelector);
```

> **v4.5.1**: `ui_selectors` uses nested objects `{"strategy":"...","value":"..."}`. Use `GetSelectorValue()` to extract the `value` field correctly.

---

## Part 3: SessionRunner 详解

> **模块**: `Modules/SessionRunner.cs`
> **版本**: v4.5.10（兼容 v4.5.8/v4.5.9 关键语义）
> **语法**: C# 5.0（外部 .cs 编译限制）

### 3.1 模块定位

SessionRunner 是 DPS v4.5 的**会话执行核心**。它模拟真实用户在社交 APP 中的一次完整使用会话（打开 APP → 浏览/点赞/评论 → 退出），所有平台复用同一套代码，通过 JSON 配置文件切换不同 APP 的行为。

**一句话概括**：SessionRunner 是一个**平台无关的会话模拟器**，靠配置驱动，不靠硬编码。

---

### 3.2 完整执行流程

```
SessionRunner.Run()
│
├── [1] 初始化
│   ├── CoreHelper.Init() — 注入 project/instance
│   ├── 读取 ZD 变量: project_root, device_id, persona_json, session_plan_json
│   ├── 加载 BehaviorConfig.json — 动作权重、打字速度、会话时长
│   ├── 加载 DecisionConfig.json — RuleEngine 评分阈值
│   └── 初始化疲劳模型 SessionState
│
├── [2] 平台确定
│   ├── DeterminePlatform() — 从 device_app_mapping.json 查设备→平台映射
│   ├── 加载 PlatformsConfig.json — 获取平台配置（包名、速率限制、UI 选择器）
│   ├── 检查平台是否启用 (enabled: true/false)
│   └── 初始化 MemoryManager — 交互记录去重
│
├── [3] 加载配置文件（统一引擎）
│   ├── Config/Operations/{platform}_operations.json  → _operationsJson
│   ├── Config/IntentMappings/{platform}_intents.json → _intentMappingJson
│   ├── Config/UserStrategy.json                     → _userStrategyJson
│   ├── Data/Keywords/{platform}/interests.json      → _interestsJson
│   └── Data/Keywords/{platform}/triggers.json       → _triggersJson
│
├── [4] 会话循环 (while DateTime.Now < sessionEnd)
│   │
│   ├── Step 1: 动作选择
│   │   ├── AdjustWeightsForFatigue() — 能量不足时禁用高消耗动作
│   │   ├── WeightedChoice() — 加权随机：browse(0.50) > read(0.30) > like(0.15) > comment(0.04) > post(0.01)
│   │   └── ResolveIntentForAction() — 动作名→意图名映射
│   │
│   ├── Step 2: 门控检查
│   │   ├── MemoryManager.IsDuplicate() — 已交互的帖子降级为 browse
│   │   └── RuleEngine 评估 — 帖子评分不达标则降级为 browse
│   │
│   ├── Step 3: 执行
│   │   ├── ExecuteWithUnifiedEngine() — 统一引擎执行（优先）
│   │   │   ├── PageDetector.Detect() — 检测当前页面
│   │   │   ├── ResolveIntentWithFallback() — 意图回退链解析
│   │   │   ├── GetOperationsByIntent() — 意图→操作序列
│   │   │   └── ActionExecutor.Execute() — 逐步执行操作
│   │   └── LoadPlatformModule() — 旧模块模式（回退）
│   │
│   ├── Step 4: 结果处理
│   │   ├── 视觉验证（仅 like/comment 且启用时）
│   │   ├── MemoryManager.RecordInteraction() — 记录成功交互
│   │   └── 记录到日记忆文件
│   │
│   └── Step 5: 能量更新
│       ├── UpdateEnergy() — 扣除动作消耗 + 恢复等待时间
│       └── 最大动作数检查（50 次硬限制）
│
└── [5] 会话结束
    ├── 保存记忆文件到 Memory/{device_id}/{date}.json
    ├── MemoryManager 清理过期记忆
    ├── 计算成功率，设置 session_result
    └── 返回 "SUCCESS" 或 "ERROR"
```

---

### 3.3 配置文件详解

#### 必需配置文件

| 文件 | 路径 | 作用 |
|------|------|------|
| **BehaviorConfig.json** | `Config/` | 动作权重、会话时长、打字速度 |
| **PlatformsConfig.json** | `Config/` | 平台定义（包名、UI 选择器、速率限制、页面签名） |
| **device_app_mapping.json** | `Config/` | 设备 ID → 平台映射 |
| **{platform}_operations.json** | `Config/Operations/` | 平台操作步骤定义 |
| **{platform}_intents.json** | `Config/IntentMappings/` | 平台意图映射与回退链 |

#### 可选配置文件

| 文件 | 路径 | 作用 | 缺失时行为 |
|------|------|------|------------|
| **DecisionConfig.json** | `Config/` | RuleEngine 评分阈值 + 疲劳模型 | 使用默认规则 |
| **UserStrategy.json** | `Config/` | AI 控制策略、人工通知 | 默认全自动 |
| **interests.json** | `Data/Keywords/{platform}/` | 兴趣关键词（用于帖子评分） | 跳过相关性评分 |
| **triggers.json** | `Data/Keywords/{platform}/` | 避免话题 + 触发词 | 不过滤话题 |

#### ZD 变量（运行前必须设置）

| 变量名 | 必需 | 说明 | 示例 |
|--------|------|------|------|
| `project_root` | 是 | 项目根目录（末尾带斜杠） | `C:\DPS_v4.5\` |
| `device_id` | 是 | 设备标识符 | `device_001` |
| `persona_json` | 否 | 画像 JSON（含 TypingSkillLevel） | `{"TypingSkillLevel":"proficient"}` |
| `session_plan_json` | 否 | 会话计划（含时长） | `{"session_duration_minutes":15}` |
| `behavior_config_json` | 否 | 行为配置覆盖（优先于文件） | - |

---

### 3.4 意图映射系统

#### 动作→意图→操作 完整路径

```
SessionRunner 动作    →  统一意图            →  操作序列 (operations.json)
─────────────────────────────────────────────────────────────────
browse               →  browse_feed         →  ["browse"]
read_post            →  read_post           →  ["open_post", "read_post", "back_to_feed"]
like                 →  like_content        →  ["like"]
comment              →  reply_post          →  ["open_post", "comment", "back_to_feed"]
follow               →  follow_entity       →  ["follow"]
share                →  share_content       →  ["share"]
```

#### 意图回退链

当某个意图在当前状态下不可执行时，沿回退链降级：

```json
// reddit_intents.json 示例
"reply_post": {
    "operations": ["open_post", "comment", "back_to_feed"],
    "fallback_intents": ["read_post", "browse_feed"]
}
```

解析顺序：`reply_post` → 不可执行 → 尝试 `read_post` → 不可执行 → 最终回退到 `browse_feed`

#### 页面感知

SessionRunner 会根据当前页面自动调整操作序列：

| 当前页面 | 动作 "read_post" 的实际操作 |
|----------|---------------------------|
| `feed` | `open_post` → `read_post` → `back_to_feed` |
| `post_detail` | `read_post` → `back_to_feed`（跳过 open_post） |
| 其他页面 | `back_to_feed` → `open_post` → `read_post` → `back_to_feed` |

---

### 3.5 operations.json 编写规范

#### 文件结构

```json
{
    "platform": "myapp",
    "version": "1.0",
    "operations": {
        "操作名": {
            "description": "操作描述",
            "require_page": "可选：要求的页面状态",
            "steps": [
                { "action": "步骤类型", ...参数 }
            ]
        }
    }
}
```

#### 编写示例：为新 APP 添加 "browse" 操作

```json
{
    "browse": {
        "description": "浏览主页 feed",
        "require_page": "feed",
        "steps": [
            { "action": "log", "message": "开始浏览 feed" },
            { "action": "find", "selector": "feed_item", "save_as": "items", "on_fail": "abort" },
            { "action": "delay", "min_ms": 2000, "max_ms": 5000 },
            { "action": "scroll", "direction": "down", "distance": 800 },
            { "action": "delay", "min_ms": 1000, "max_ms": 3000 },
            { "action": "refresh_layout" },
            { "action": "verify", "selector": "feed_item", "on_fail": "retry", "max_retries": 3, "retry_delay_ms": 2000 },
            { "action": "set_var", "name": "last_action", "value": "browse" }
        ]
    }
}
```

---

### 3.6 intents.json 编写规范

#### 文件结构

```json
{
    "platform": "myapp",
    "version": "1.0",
    "intents": {
        "意图名": {
            "operations": ["操作1", "操作2"],
            "fallback_intents": ["降级意图1", "降级意图2"]
        }
    },
    "action_to_intent": {
        "SessionRunner动作名": "意图名"
    }
}
```

#### 核心意图（建议所有平台实现）

| 意图 | 说明 | 典型操作序列 |
|------|------|-------------|
| `browse_feed` | 浏览首页 | `["browse"]` |
| `open_post` | 打开帖子 | `["open_post"]` |
| `read_post` | 阅读帖子 | `["open_post", "read_post", "back_to_feed"]` |
| `like_content` | 点赞 | `["like"]` |
| `reply_post` | 评论帖子 | `["open_post", "comment", "back_to_feed"]` |
| `follow_entity` | 关注用户 | `["follow"]` |

#### action_to_intent 映射表

此表将 SessionRunner 内部的 5 种动作名映射到平台意图。**每个平台必须提供此映射**：

```json
"action_to_intent": {
    "browse": "browse_feed",
    "read_post": "read_post",
    "like": "like_content",
    "comment": "reply_post",
    "post": "reply_post"
}
```

---

### 3.7 疲劳模型

SessionRunner 内置能量系统，模拟用户使用疲劳：

#### 配置（DecisionConfig.json）

```json
"fatigue_model": {
    "enabled": true,
    "initial_energy": 1.0,
    "recovery_per_pause_sec": 0.01,
    "min_energy_to_comment": 0.3,
    "min_energy_to_like": 0.15,
    "decay_per_action": {
        "browse": 0.02,
        "read": 0.05,
        "like": 0.03,
        "comment": 0.10
    }
}
```

#### 行为效果

| 能量区间 | 可用动作 | 说明 |
|----------|---------|------|
| 1.0 ~ 0.30 | 全部 | 正常状态 |
| 0.30 ~ 0.15 | browse, read, like | comment 被禁用 |
| 0.15 ~ 0.00 | browse, read | like + comment 都被禁用 |

被禁用动作的权重会按比例重新分配给剩余动作，确保权重总和为 1.0。

---

### 3.8 RuleEngine 门控

在执行动作前，RuleEngine 会评估当前帖子是否值得交互：

#### 评分维度

| 维度 | 来源 | 权重（默认） |
|------|------|-------------|
| **热度评分** (hot_score) | 帖子的 upvotes、comments、时间衰减 | upvotes 0.4, comments 0.3, time 0.3 |
| **活跃度评分** (activity_score) | 近期评论比例、频率、独立作者数 | ratio 0.5, freq 0.3, authors 0.2 |
| **相关性评分** (relevance_score) | 关键词匹配、subreddit 匹配、话题相似度 | keyword 0.5, sub 0.3, topic 0.2 |

#### 决策阈值（DecisionConfig.json）

```json
"decision_thresholds": {
    "click_post":    { "min_score": 0.3 },
    "like_post":     { "min_score": 0.5 },
    "comment_post":  { "min_score": 0.7 }
}
```

评分不达标 → 动作自动降级为 `browse`。

---

### 3.9 MemoryManager 去重

防止在短时间内对同一帖子重复交互：

```
会话中第1次看到帖子 A → like → 记录到 MemoryManager
会话中第2次看到帖子 A → 检测到已交互 → 自动降级为 browse
```

- 去重窗口由 `DecisionConfig.json` 配置
- 每次会话结束自动清理过期记忆
- 记忆按 `device_id + platform + post_id` 索引

---

### 3.10 输出变量

SessionRunner 执行完毕后设置的 ZD 变量：

| 变量名 | 值 | 说明 |
|--------|------|------|
| `session_result` | SUCCESS / ERROR | 会话总体结果 |
| `run_result` | SUCCESS / ERROR | 同上（兼容旧版） |
| `action_count` | 数字 | 成功执行的动作总数（仅成功动作） |
| `action_attempt_count` | 数字 | 总动作尝试数（含成功+失败+跳过） |
| `session_success_rate` | 数字 | 有效动作成功率（百分比） |
| `session_successful_actions` | 数字 | 成功动作数 |
| `session_failed_actions` | 数字 | 失败动作数 |
| `session_skipped_actions` | 数字 | 跳过动作数（SKIP 状态） |
| `current_platform` | 平台名 | 本次使用的平台 |
| `current_page` | 页面名 | 会话结束时的页面状态 |
| `last_error` | 错误信息 | 仅异常时设置 |

#### 成功判定（v4.5.8+）

```
成功条件: session_success_rate >= 95% && session_successful_actions >= 6
即：有效动作成功率不低于 95%，且至少有 6 个成功动作
```

> **注**: v4.5.8 之前的旧条件为 `failedActions <= actionCount / 2`（50% 容错）。v4.5.8 起升级为更严格的成功门控，`action_count` 语义也调整为仅统计成功动作。

---

### 3.11 调用方式

#### ZennoDroid Own Code 调用

```csharp
// 在 ZD Own Code 块中：
// 1. 设置必需变量
project.Variables["project_root"].Value = @"C:\DPS_v4.5\";
project.Variables["device_id"].Value = "device_001";

// 2. 通过 ModuleLoader 编译并执行
string result = SessionRunner.Run(project, instance);
project.SendInfoToLog("SessionRunner 结果: " + result);
```

#### 带画像和会话计划

```csharp
project.Variables["project_root"].Value = @"C:\DPS_v4.5\";
project.Variables["device_id"].Value = "device_001";
project.Variables["persona_json"].Value = "{\"TypingSkillLevel\":\"proficient\"}";
project.Variables["session_plan_json"].Value = "{\"session_duration_minutes\":20}";

string result = SessionRunner.Run(project, instance);
```

---

### 3.12 故障排查

#### 问题 1: "ERROR: 变量未设置"
**原因**: `project_root` 或 `device_id` 为空。
**解决**: 在调用前确保两个变量已设置。

#### 问题 2: "ERROR: 平台配置不存在"
**原因**: `device_app_mapping.json` 中的设备映射指向了 `PlatformsConfig.json` 中不存在的平台。
**解决**: 检查两个文件的平台名是否一致（大小写敏感）。

#### 问题 3: "操作配置不存在，将回退到旧模块模式"
**原因**: `Config/Operations/{platform}_operations.json` 文件不存在。
**解决**: 创建对应的 operations JSON 文件。回退到旧模块模式意味着使用 `Platforms/{Platform}/{Platform}Module.cs`。

#### 问题 4: 动作全部变成 browse
**可能原因**:
1. RuleEngine 评分阈值过高 → 降低 `DecisionConfig.json` 中的 `min_score`
2. 疲劳模型能量耗尽 → 检查 `fatigue_model` 配置
3. MemoryManager 去重 → 所有可见帖子已交互过
4. 意图回退链最终都落到 `browse_feed` → 检查 `intents.json` 配置

#### 问题 5: 会话时间很短就结束
**原因**: `session_duration_minutes` 被 `BehaviorConfig.json` 中的 `min/max_duration_minutes` 钳制。
**解决**: 检查 `session.min_duration_minutes` 和 `session.max_duration_minutes`。

---

### 3.13 性能与限制

| 项目 | 值 | 说明 |
|------|------|------|
| 最大动作数 | 50 | 硬编码保护，防止无限循环 |
| 会话时长 | 3~30 分钟 | 由 BehaviorConfig 钳制 |
| 实际等待 | 1~5 秒 | Thread.Sleep 截断范围 |
| 页面检测 | 每次操作后 | 通过 XML layout + page_signatures |
| 旧模块回退 | 自动 | 无 operations.json 时回退到 C# 平台模块 |

---

## Part 4: 平台配置

### 4.1 已支持平台

#### Reddit
- **Rate Limits**: 120 actions/hour, 40 likes/hour, 20 comments/hour, 15 follows/hour
- **Package**: `com.reddit.frontpage`
- **UI Selectors**: `post_unit`, `upvote_button`, `comment_button`, etc.
- **Status**: ✅ Implemented

#### Instagram
- **Rate Limits**: 60 actions/hour, 30 likes/hour, 10 comments/hour, 20 follows/hour
- **Package**: `com.instagram.android`
- **UI Selectors**: `media_container`, `like_button`, `comment_button`, etc.
- **Status**: ✅ Implemented

#### BabyCenter (v4.5.7 新增)
- **Rate Limits**: 80 actions/hour, 24 likes/hour, 12 comments/hour
- **Package**: `com.babycenter.pregnancytracker`
- **UI Selectors**: `post_unit`, `post_title`, `post_body`, `like_button`, `comment_button`, etc.
- **Status**: ✅ Implemented
- **Primary Communities**: Pregnancy, Baby, Toddler
- **Note**: BabyCenter 使用 config-driven mode (operations.json + intents.json)，无 Platforms/ 目录下的 C# 模块
- **详细指南**: 参见 `Docs/Platforms/BabyCenter_APP_Guide_平台指南.md`

#### TikTok (Phase 2)
- **Rate Limits**: 100 actions/hour, 50 likes/hour, 15 comments/hour
- **Package**: `com.zhiliaoapp.musically`
- **Status**: ⏳ Phase 2

#### Facebook (Phase 2)
- **Rate Limits**: 80 actions/hour, 35 likes/hour, 12 comments/hour
- **Package**: `com.facebook.katana`
- **Status**: ⏳ Phase 2

---

### 4.2 设备映射

`Config/device_app_mapping.json` 配置设备→平台映射：

```json
{
  "devices": {
    "device_001": { "platform": "reddit" },
    "device_002": { "platform": "instagram" },
    "device_004": { "platform": "babycenter" }
  },
  "default_platform": "reddit"
}
```

---

### 4.3 平台配置示例

`Config/PlatformsConfig.json`:

```json
{
  "platforms": {
    "babycenter": {
      "name": "BabyCenter",
      "package_name": "com.babycenter.pregnancytracker",
      "enabled": true,
      "rate_limits": {
        "max_actions_per_hour": 80,
        "max_likes_per_hour": 24,
        "max_comments_per_hour": 12
      },
      "ui_selectors": {
        "post_unit": {
          "strategy": "resource-id",
          "value": "post_container",
          "fallback_strategy": "class",
          "fallback_value": "android.widget.LinearLayout"
        }
      }
    }
  }
}
```

---

### 4.4 添加新平台（完整步骤）

以添加 **Facebook** 为例：

#### Step 1: 平台配置 — `Config/PlatformsConfig.json`

在 `platforms` 下新增：

```json
"facebook": {
    "name": "Facebook",
    "package_name": "com.facebook.katana",
    "enabled": true,
    "rate_limits": {
        "max_actions_per_hour": 100,
        "max_likes_per_hour": 50,
        "max_comments_per_hour": 15,
        "min_action_delay_ms": 2000,
        "max_action_delay_ms": 8000
    },
    "ui_selectors": {
        "post_unit": { "strategy": "resource-id", "value": "stories_tray" },
        "upvote_button": { "strategy": "content-desc", "value": "Like" }
    },
    "page_signatures": {
        "feed": { "indicators": ["stories_tray", "feed_stream"] },
        "post_detail": { "indicators": ["comment_composer"] }
    }
}
```

#### Step 2: 操作定义 — `Config/Operations/facebook_operations.json`

```json
{
    "platform": "facebook",
    "version": "1.0",
    "operations": {
        "browse": { ... },
        "like": { ... },
        "open_post": { ... },
        "read_post": { ... },
        "comment": { ... },
        "back_to_feed": { ... }
    }
}
```

#### Step 3: 意图映射 — `Config/IntentMappings/facebook_intents.json`

```json
{
    "platform": "facebook",
    "version": "1.0",
    "intents": {
        "browse_feed": { "operations": ["browse"], "fallback_intents": [] },
        "like_content": { "operations": ["like"], "fallback_intents": ["browse_feed"] },
        "read_post": { "operations": ["open_post", "read_post", "back_to_feed"], "fallback_intents": ["browse_feed"] },
        "reply_post": { "operations": ["open_post", "comment", "back_to_feed"], "fallback_intents": ["read_post", "browse_feed"] }
    },
    "action_to_intent": {
        "browse": "browse_feed",
        "read_post": "read_post",
        "like": "like_content",
        "comment": "reply_post",
        "post": "reply_post"
    }
}
```

#### Step 4: 设备映射 — `Config/device_app_mapping.json`

```json
"device_005": {
    "platform": "facebook",
    "device_name": "Facebook Test Device"
}
```

#### Step 5: 验证

在 ZennoDroid 中设置 `device_id = device_005`，运行 SessionRunner，检查日志：

```
[SessionRunner] 选择平台: facebook
[SessionRunner] 已加载操作配置: Config\Operations\facebook_operations.json
[SessionRunner] 已加载意图映射: facebook
```

#### 自动化方式（推荐，v4.5.10+）

使用 App Onboarder 自动生成配置、操作和 E2E 测试：

```bash
python Tools/app_onboarder/main.py --package com.facebook.katana --key facebook
```

参见 `Tools/app_onboarder/README.md`。

---

### 4.5 运行会话

SessionRunner 自动完成以下流程：
1. 从项目变量读取 `device_id`
2. 在 `device_app_mapping.json` 中查找平台
3. 加载平台配置 + 意图映射 + 操作定义
4. 带速率限制和人性化执行操作

```csharp
// In MainProject.droid - Own Code block
// SessionRunner.cs handles everything automatically
string result = RunSession(project, instance);
```

---

### 4.6 实机验证记录

#### Reddit

**测试脚本**：`reddit_adb_test.ps1`
**测试日期**：2026-03-04
**测试结果**：6/6 PASSED

| 测试项 | 结果 | 说明 |
|--------|------|------|
| 底部导航定位 | PASSED | `bottom_nav` 存在且可点击 |
| Feed 帖子元素 | PASSED | `post_unit`, `post_footer` 等元素可定位 |
| 帖子打开/返回 | PASSED | 正常打开帖子详情，返回 feed |
| 点赞按钮交互 | PASSED | `upvote_button` 可定位并点击 |
| 评论输入流程 | PASSED | 完整评论流程可执行 |
| APP 启动/关闭 | PASSED | monkey launcher 方式可靠启动 |

**关键发现**：
- **KEYCODE_BACK 回退**：视频帖子（全屏播放器）无法通过常规返回按钮退出，需要使用 `KEYCODE_BACK` 作为回退策略
- **monkey launcher 启动**：相比 `am start`，使用 `monkey -p {package} 1` 启动 APP 更可靠
- **bottom_nav 可选**：部分版本的 Reddit 在特定页面隐藏底部导航栏，`bottom_nav` 的 find 步骤应设置 `on_fail: skip`

#### BabyCenter

**测试脚本**：`babycenter_adb_test.ps1`
**测试日期**：2026-03-04
**测试结果**：23/23 PASSED

| 测试项 | 结果 | 说明 |
|--------|------|------|
| 底部导航栏（5 个 tab） | PASSED | `bottom_navigator`, `menu_home` ~ `menu_more` 全部存在 |
| 首页元素 | PASSED | `recycler`, `salutation`, `weekRange`, `toolbar`, `appBar` |
| 社区页面导航 | PASSED | 点击 `menu_birthclub` 成功切换 |
| 社区 feed 元素 | PASSED | `postContainer`, `posts`, `title`, `text` 等 |
| 社区 tab 栏 | PASSED | `community_home`, `community_my_activity`, `community_my_bookmark` |
| 帖子详情页（WebView） | PASSED | `web_view`, `webViewLayout`, `toolbar`, `share`, `refresh` |
| ViewPager 水平滑动 | PASSED | 水平滑动正确触发帖子切换 |

**关键发现**：
- **WebView 帖子详情**：帖子内容在 `WebViewActivity` 中通过 WebView 渲染
- **ViewPager 水平 feed**：社区 feed 使用 ViewPager 而非 RecyclerView，browse 操作必须配置为水平滑动
- **like/comment 可操作**：WebView 暴露了 accessibility nodes（详见 `Docs/Platforms/BabyCenter_APP_Guide_平台指南.md`）

---

### 4.7 平台验证状态总览

| 平台 | 状态 | 验证日期 | 测试结果 | 备注 |
|------|------|---------|---------|------|
| Reddit | ✅ 已验证 | 2026-03-04 | 6/6 PASSED | 全部操作可靠 |
| BabyCenter | ✅ 已验证 | 2026-03-04 | 23/23 PASSED | WebView accessibility 可操作 |
| Instagram | ⚠️ 未验证 | - | - | 选择器基于估算，未经实机确认 |
| TikTok | ❌ 已禁用 | - | - | 未验证，enabled: false |
| Facebook | ❌ 已禁用 | - | - | 未验证，enabled: false |

---

### 4.8 故障排查

#### Rate Limit Exceeded
**症状**: Actions return "Rate limit exceeded"
**解决**: 检查 `{platform}_actions_this_hour` 变量，等待每小时重置，或调整 `PlatformsConfig.json` 中的 `max_actions_per_hour`。

#### UI Element Not Found
**症状**: Operations fail with "not found"
**解决**: 验证 `ui_selectors` 中的 resource-id，检查 APP UI 是否变更，添加 XPath 或 image fallback。

#### Platform Config Not Loading
**症状**: SessionRunner cannot resolve platform execution chain
**解决**: 验证 `device_app_mapping.json` 中的 device_id，检查 `Config/Operations/` 和 `Config/IntentMappings/` 中对应平台的 JSON 文件是否存在，确认平台在 `PlatformsConfig.json` 中已启用。

---

### 4.9 SessionRunner 版本历史

| 版本 | 日期 | 变更 |
|------|------|------|
| v4.5.0 | 2026-02-07 | 初始版本，加权随机动作选择 |
| v4.5.1 | 2026-02-26 | 接入 MemoryManager，交互去重 |
| v4.5.2 | 2026-02-27 | 统一引擎 (ActionExecutor + operations.json)，视觉验证层 |
| v4.5.8 | 2026-02-27 | 成功门控升级 (>=95%)，语义字段回填，SKIP 状态统一 |
| v4.5.9 | 2026-02-28 | like 映射修复 (like_content 替代错误的 open_post) + I/O 容错增强 |
| v4.5.10 | 2026-03-04 | App Onboarder 上线（不改 SessionRunner 主执行语义，文档同步） |

### MultiPlatformFramework 版本历史

| Version | Date | Changes |
|---------|------|---------|
| 1.5 | 2026-03-05 | Added v4.5.10 App Onboarder, corrected BabyCenter status |
| 1.4 | 2026-02-27 | Added v4.5.8 stability updates |
| 1.3 | 2026-02-27 | Added BabyCenter platform support |
| 1.2 | 2026-02-14 | Fixed file structure, added MemoryManager/RuleEngine |
| 1.1 | 2026-02-13 | Fixed ui_selectors to nested object format |
| 1.0 | 2026-02-07 | Initial multi-platform release with Reddit and Instagram |

---

## Part 5: Persona Schema — Multi-Platform Extension

**Version**: 4.5.2
**Purpose**: 扩展 persona schema 以支持多平台社交媒体

### 5.1 Schema Extensions

在现有 `digital_behavior` section 中添加 `platform_preferences` 对象：

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
        "action_weights": { "browse": 40, "like": 30, "comment": 15, "follow": 10, "share": 5 },
        "peak_hours": ["07:00-08:00", "20:00-22:00"],
        "session_duration_minutes": 15,
        "sessions_per_day": 4,
        "preferred_communities": ["BabyBumps", "Mommit", "Parenting"],
        "content_focus": ["pregnancy_tips", "toddler_behavior", "product_recommendations"]
      },
      "instagram": {
        "enabled": true,
        "humanization_profile": "deep_reader",
        "action_weights": { "browse": 50, "like": 35, "comment": 5, "follow": 8, "share": 2 },
        "peak_hours": ["12:00-13:00", "21:00-22:30"],
        "session_duration_minutes": 20,
        "sessions_per_day": 3,
        "preferred_hashtags": ["#pregnancy", "#momlife", "#toddlermom", "#secondpregnancy"],
        "content_focus": ["visual_inspiration", "lifestyle", "product_discovery"]
      },
      "tiktok": {
        "enabled": false,
        "humanization_profile": "speed_demon",
        "action_weights": { "browse": 60, "like": 25, "comment": 5, "follow": 8, "share": 2 },
        "peak_hours": ["19:00-20:00", "22:00-23:00"],
        "session_duration_minutes": 25,
        "sessions_per_day": 2,
        "preferred_hashtags": ["#momtok", "#pregnancytiktok", "toddlerlife"],
        "content_focus": ["entertainment", "quick_tips", "relatable_content"]
      },
      "facebook": {
        "enabled": false,
        "humanization_profile": "casual",
        "action_weights": { "browse": 45, "like": 30, "comment": 10, "follow": 10, "share": 5 },
        "peak_hours": ["08:00-09:00", "20:00-21:00"],
        "session_duration_minutes": 18,
        "sessions_per_day": 3,
        "preferred_groups": ["Local Moms Group", "Pregnancy Support"],
        "content_focus": ["community_support", "local_events", "marketplace"]
      },
      "babycenter": {
        "enabled": true,
        "humanization_profile": "casual",
        "action_weights": { "browse": 40, "like": 20, "comment": 10, "follow": 5, "share": 5, "read_post": 20 },
        "peak_hours": ["08:00-09:00", "12:00-13:00", "20:00-21:30"],
        "session_duration_minutes": 15,
        "sessions_per_day": 3,
        "preferred_communities": ["Birth Club", "Pregnancy", "Baby"],
        "content_focus": ["pregnancy_progress", "baby_milestones", "parenting_advice"]
      }
    }
  }
}
```

---

### 5.2 Field Definitions

#### Platform-Level Fields

| Field | Type | Description | Example |
|-------|------|-------------|---------|
| `enabled` | boolean | Whether this platform is active for the persona | `true` |
| `humanization_profile` | string | Behavior profile from HumanizationEngine | `"speed_demon"`, `"casual"`, `"deep_reader"`, `"distracted"` |
| `action_weights` | object | Probability distribution for actions (must sum to 100) | See below |
| `peak_hours` | array[string] | Time ranges when persona is most active | `["07:00-08:00", "20:00-22:00"]` |
| `session_duration_minutes` | integer | Average session length | `15` |
| `sessions_per_day` | integer | Number of sessions per day | `4` |
| `preferred_communities` | array[string] | Platform-specific communities/groups | `["BabyBumps", "Mommit"]` |
| `preferred_hashtags` | array[string] | Hashtags to follow (Instagram/TikTok) | `["#pregnancy", "#momlife"]` |
| `preferred_groups` | array[string] | Groups to participate in (Facebook) | `["Local Moms Group"]` |
| `content_focus` | array[string] | Content themes persona seeks | `["pregnancy_tips", "product_recommendations"]` |

#### Action Weights

Action weights 定义动作的概率分布，**必须总和为 100**：

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

### 5.3 Humanization Profiles

每个平台可以使用不同的 humanization profile：

| Profile | Speed | Variance | Tap Offset | Swipe Bending | Accidental Actions | Best For |
|---------|-------|----------|------------|---------------|-------------------|----------|
| `speed_demon` | Fast (0.6x) | Low (15%) | 5px | 10 | Very rare (1-2%) | TikTok, quick browsing |
| `casual` | Normal (1.0x) | Medium (25%) | 15px | 30 | Occasional (2-3%) | Reddit, general use |
| `deep_reader` | Slow (1.5x) | High (30%) | 10px | 20 | Common (3-8%) | Instagram, reading posts |
| `distracted` | Variable (1.2x) | Very High (40%) | 20px | 40 | Frequent (4-10%) | Interruption-prone behavior |

#### Profile Selection Guidelines
- **Reddit**: `casual` or `deep_reader` (discussion requires reading)
- **Instagram**: `deep_reader` (visual content, longer viewing)
- **TikTok**: `speed_demon` (fast-paced, quick scrolling)
- **Facebook**: `casual` (mixed content types)
- **BabyCenter**: `casual` (community discussion + WebView reading)

---

### 5.4 Cross-Platform Behavior Consistency

#### Consistency Rules

1. **Peak Hours Alignment**
   - Peak hours should align with persona's `usage_patterns.peak_hours`
   - Platform-specific variations allowed (±1 hour)

2. **Session Duration Correlation**
   - Total daily session time across platforms should match persona's lifestyle
   - Formula: `sum(sessions_per_day * session_duration_minutes) ≈ usage_patterns.average_session_minutes * usage_patterns.sessions_per_day`

3. **Action Weight Consistency**
   - Action weights should reflect persona's `community_engagement.lurker_vs_poster`
   - Lurkers: Higher browse, lower comment/share
   - Posters: More balanced distribution

4. **Content Focus Alignment**
   - `content_focus` should align with persona's `interests_and_hobbies` and `current_concerns`

#### Example Consistency Check

For persona Sarah (from R58M4816G8Y.json):
- **Personality**: Organized, empathetic, cautious
- **Community Engagement**: Occasional poster

```json
{
  "reddit": {
    "humanization_profile": "casual",
    "action_weights": { "browse": 40, "like": 30, "comment": 15, "follow": 10, "share": 5 },
    "peak_hours": ["07:00-08:00", "20:00-22:00"]
  },
  "instagram": {
    "humanization_profile": "deep_reader",
    "action_weights": { "browse": 50, "like": 35, "comment": 5, "follow": 8, "share": 2 },
    "peak_hours": ["12:00-13:00", "21:00-22:30"]
  }
}
```

**Why This Works**:
- Reddit gets more comments (discussion-focused, matches "occasional poster")
- Instagram gets more browse/like (visual platform, less commenting)
- Both use moderate-to-slow profiles (matches "cautious" personality)

---

### 5.5 Rate Limit Compliance

| Platform | Max Actions/Hour | Max Likes/Hour | Max Comments/Hour | Max Follows/Hour |
|----------|------------------|----------------|-------------------|------------------|
| Reddit | 120 | 40 | 20 | 15 |
| Instagram | 60 | 30 | 10 | 20 |
| BabyCenter | 80 | 24 | 12 | 8 |
| TikTok | 100 | 50 | 15 | 25 |
| Facebook | 80 | 35 | 12 | 15 |

**Action Weight Calculation**:

```
actions_per_session = session_duration_minutes * (max_actions_per_hour / 60)
action_probability = action_weight / 100
expected_action_count = actions_per_session * action_probability
```

**Example** (Instagram, 20-minute session):
```
actions_per_session = 20 * (60 / 60) = 20 actions
like_probability = 35 / 100 = 0.35
expected_likes = 20 * 0.35 = 7 likes  ← well within 30/hour limit
```

---

### 5.6 Migration Guide

To add multi-platform support to an existing persona:

1. Read existing persona JSON
2. Add `platform_preferences` to `digital_behavior`
3. Configure enabled platforms (start with 1-2)
4. Set humanization profiles based on personality
5. Define action weights based on engagement style
6. Align peak hours with existing usage patterns
7. Validate consistency using rules above

```csharp
// Example migration script
string personaJson = CoreHelper.ReadFile(personaPath);
var persona = JsonHelper.Parse(personaJson);

string engagementStyle = JsonHelper.Get(persona, "digital_behavior.community_engagement.lurker_vs_poster");
string peakHours = JsonHelper.Get(persona, "digital_behavior.usage_patterns.peak_hours");

var actionWeights = engagementStyle == "lurker" 
    ? new { browse = 60, like = 25, comment = 5, follow = 8, share = 2 }
    : new { browse = 40, like = 30, comment = 15, follow = 10, share = 5 };

var platformPrefs = new {
    reddit = new {
        enabled = true,
        humanization_profile = "casual",
        action_weights = actionWeights,
        peak_hours = peakHours
    }
};

JsonHelper.AddField(persona, "digital_behavior.platform_preferences", platformPrefs);
CoreHelper.WriteFile(personaPath, JsonHelper.Stringify(persona));
```

---

### 5.7 Validation Rules

#### Required Fields
- `enabled` (boolean)
- `humanization_profile` (must be one of: speed_demon, casual, deep_reader, distracted)
- `action_weights` (must sum to 100)
- `peak_hours` (array, at least 1 time range)
- `session_duration_minutes` (integer, 5-60)
- `sessions_per_day` (integer, 1-10)

#### Validation Checks
1. Action weights sum to 100
2. Peak hours format: "HH:MM-HH:MM"
3. Humanization profile exists in HumanizationEngine
4. Session duration realistic (5-60 minutes)
5. Total daily time reasonable (< 4 hours across all platforms)
6. At least one platform enabled

---

## Part 6: ActionExecutor 原语参考

### 6.1 可用 step action 类型

| action | 说明 | 关键参数 |
|--------|------|----------|
| `log` | 输出日志 | `message` |
| `find` | 查找 UI 元素 | `selector`, `save_as`, `on_fail` |
| `tap` | 点击元素 | `context_ref`, `on_fail` |
| `type` | 输入文本 | `var`（ZD 变量名） |
| `scroll` | 滚动 | `direction`, `distance` |
| `delay` | 随机等待 | `min_ms`, `max_ms` |
| `back` | 返回上一页 | - |
| `refresh_layout` | 刷新 UI 层级 | - |
| `verify` | 验证元素存在 | `selector`, `on_fail`, `max_retries`, `retry_delay_ms` |
| `require` | 要求特定页面 | `page`, `on_fail` |
| `set_var` | 设置变量 | `name`, `value` |
| `if_exists` | 条件分支 | `condition` (see 6.2) |

### 6.2 if_exists 原语

`if_exists` 是 ActionExecutor 中的条件分支原语，根据 UI 元素是否存在执行不同的步骤序列。

#### 语法

```json
{
  "action": "if_exists",
  "condition": {
    "selector": "element_key",
    "then": [
      { "action": "...", ... }
    ],
    "else": [
      { "action": "...", ... }
    ]
  }
}
```

#### 参数说明

| 字段 | 类型 | 必需 | 说明 |
|------|------|------|------|
| `condition` | Object | 是 | 条件对象 |
| `condition.selector` | String | 是 | 元素选择器（引用 ui_selectors 或内联） |
| `condition.then` | Array | 是 | 元素存在时执行的步骤数组 |
| `condition.else` | Array | 否 | 元素不存在时执行的步骤数组（可选） |

#### 示例 1：检查登录状态

```json
{
  "action": "if_exists",
  "condition": {
    "selector": "login_button",
    "then": [
      { "action": "log", "message": "未登录，开始登录流程" },
      { "action": "tap", "selector": "login_button" },
      { "action": "delay", "min_ms": 2000, "max_ms": 3000 }
    ],
    "else": [
      { "action": "log", "message": "已登录，跳过登录" }
    ]
  }
}
```

#### 示例 2：处理弹窗

```json
{
  "action": "if_exists",
  "condition": {
    "selector": "popup_close_button",
    "then": [
      { "action": "log", "message": "检测到弹窗，关闭" },
      { "action": "tap", "selector": "popup_close_button" },
      { "action": "delay", "min_ms": 500, "max_ms": 1000 }
    ]
  }
}
```

注意：`else` 分支可选，如果元素不存在且没有 `else` 分支，则直接跳过。

#### 示例 3：嵌套条件

```json
{
  "action": "if_exists",
  "condition": {
    "selector": "post_unit",
    "then": [
      { "action": "log", "message": "找到帖子" },
      { "action": "if_exists", "condition": {
        "selector": "like_button",
        "then": [
          { "action": "tap", "selector": "like_button" }
        ]
      }}
    ],
    "else": [
      { "action": "log", "message": "未找到帖子，刷新页面" },
      { "action": "scroll", "direction": "down", "distance": 500 }
    ]
  }
}
```

#### 执行逻辑

1. 提取 `condition` 对象
2. 解析 `selector`（支持引用 ui_selectors 或内联选择器）
3. 调用 `SelectorEngine.Exists()` 检查元素是否存在
4. 根据结果选择分支：
   - 元素存在 → 执行 `then` 分支
   - 元素不存在 → 执行 `else` 分支（如果存在）
5. 递归执行分支中的所有步骤
6. 如果任何步骤返回 `ABORT`，则中止整个操作

#### 错误处理

- 缺少 `condition` 对象 → 根据 `on_fail` 策略处理
- 选择器解析失败 → 根据 `on_fail` 策略处理
- 元素存在但缺少 `then` 分支 → 根据 `on_fail` 策略处理
- 元素不存在且无 `else` 分支 → 返回 `OK`（正常跳过）

#### 与其他原语的配合

**与 foreach 配合**:

```json
{
  "action": "foreach",
  "loop": {
    "selector": "post_unit",
    "max_items": 5,
    "body": [
      { "action": "if_exists", "condition": {
        "selector": "like_button",
        "then": [
          { "action": "tap", "selector": "like_button" }
        ]
      }}
    ]
  }
}
```

**与 call_operation 配合**:

```json
{
  "action": "if_exists",
  "condition": {
    "selector": "feed_page",
    "then": [
      { "action": "call_operation", "operation": "browse" }
    ],
    "else": [
      { "action": "call_operation", "operation": "navigate_to_feed" }
    ]
  }
}
```

#### 注意事项

1. **性能考虑**：每次检查都会调用 `GetLayout()` 获取 UI 布局，避免在循环中频繁使用
2. **选择器准确性**：确保选择器定义准确，避免误判
3. **分支复杂度**：避免过深的嵌套，建议不超过 3 层
4. **错误传播**：分支中的 `ABORT` 会中止整个操作，谨慎使用

#### 实现细节

- 文件：`Modules/Core/ActionExecutor.cs`
- 方法：`ExecuteIfExists()`
- 辅助方法：`ExecuteStepsArray()`
- 依赖：`SelectorEngine.Exists()`, `JsonHelper.ExtractObject()`, `JsonHelper.ExtractArray()`
- C# 版本：5.0（无现代语法）

---

### 6.3 on_fail 策略

| 值 | 行为 |
|----|------|
| `abort` | 终止当前操作，返回 ERROR |
| `skip` | 跳过当前步骤，继续下一步 |
| `retry` | 重试（需配合 `max_retries`） |

---

## Part 7: 测试方案

### 7.1 E2E 集成测试

| Test Script | Description |
|-------------|-------------|
| `Reddit_E2E_Test.cs` | Reddit E2E test with refresh_layout and intent verification |
| `Instagram_E2E_Test.cs` | Instagram E2E test with like fallback chain |
| `BabyCenter_E2E_Test.cs` | BabyCenter E2E test for new platform |
| `playwright_dps_test.js` | Playwright Android test via ADB/WebView |

**Usage**:
```csharp
// Set device ID and run test in ZennoDroid
// Test scripts automatically verify platform configuration
```

---

### 7.2 Playwright Android 可行性分析

Playwright 提供**实验性的 Android 自动化支持**，可以用于控制 Android 模拟器或真机，为 DPS v4.5 项目提供一种**替代 ZennoDroid 的测试方案**。

#### 支持的操作

| 操作 | API | 说明 |
|------|-----|------|
| **设备连接** | `android.devices()` | 连接到 Android 设备/模拟器 |
| **截图** | `device.screenshot()` | 截取整个设备屏幕 |
| **点击** | `device.tap(selector)` | 点击 UI 元素 |
| **拖拽** | `device.drag(selector, dest)` | 拖拽 UI 元素 |
| **输入** | `device.fill(selector, text)` | 填充输入框 |
| **按键** | `device.press(selector, key)` | 按键操作 |
| **滑动** | `device.fling(selector, direction)` | 快速滑动 |
| **Shell 命令** | `device.shell(cmd)` | 执行 ADB shell 命令 |
| **WebView** | `device.webView(pkg)` | 控制 WebView 内容 |

#### 选择器支持

```javascript
// Resource ID
{ res: 'com.example.app:id/button' }
// Text
{ text: 'Submit' }
// Content Description
{ desc: 'Navigate up' }
// Class
{ cls: 'android.widget.Button' }
```

#### 与 ZennoDroid 的对比

| 特性 | ZennoDroid | Playwright Android |
|------|------------|-------------------|
| **原生 APP 支持** | ✅ 完整支持 | ⚠️ 实验性，主要支持 WebView |
| **UI 元素定位** | ✅ XML 解析 + 多策略 | ✅ 多种选择器 |
| **截图** | ✅ 支持 | ✅ 支持 |
| **输入/滑动** | ✅ 完整支持 | ✅ 支持 |
| **Python/C# 支持** | ✅ C# 脚本 | ⚠️ 主要 Node.js，但有 Python 绑定 |
| **部署复杂度** | ⚠️ 需要 ZennoDroid 软件 | ✅ 只需 ADB + npm |
| **成本** | ❌ 商业软件 | ✅ 开源免费 |

#### DPS v4.5 项目中的可行性

| 测试场景 | 可行性 | 说明 |
|----------|--------|------|
| **配置文件验证** | ✅ 完全可行 | 读取 JSON 配置，验证格式 |
| **UI 元素定位测试** | ⚠️ 部分可行 | 可以点击、滑动，但元素定位不如 ZennoDroid 稳定 |
| **WebView APP 测试** | ✅ 完全可行 | 如果 APP 使用 WebView，Playwright 可以直接控制 |
| **原生 APP 测试** | ⚠️ 有限支持 | Reddit/Instagram/BabyCenter 是原生 APP，支持有限 |
| **截图验证** | ✅ 完全可行 | 可以截图并使用 AI 分析 |

#### 推荐的混合方案

```
┌─────────────────────────────────────────────────────────────┐
│                    DPS v4.5 测试架构                          │
├─────────────────────────────────────────────────────────────┤
│                                                               │
│  ┌──────────────────┐         ┌──────────────────┐         │
│  │  Playwright      │         │   ZennoDroid     │         │
│  │  (快速验证)      │         │   (完整测试)     │         │
│  ├──────────────────┤         ├──────────────────┤         │
│  │ - 配置文件验证   │         │ - 原生 APP 控制  │         │
│  │ - JSON 解析      │         │ - 复杂交互流程   │         │
│  │ - 截图 + AI 分析 │         │ - 生产环境测试   │         │
│  │ - WebView 测试   │         │                  │         │
│  └──────────────────┘         └──────────────────┘         │
│                                                               │
└─────────────────────────────────────────────────────────────┘
```

#### 代码示例

**基本测试**:

```javascript
const { _android: android } = require('playwright');

(async () => {
  const [device] = await android.devices();
  console.log(`Model: ${device.model()}`);

  // 启动 APP
  await device.shell('am start -n com.reddit.frontpage/.MainActivity');
  await new Promise(resolve => setTimeout(resolve, 3000));

  // 截图
  await device.screenshot({ path: 'reddit_home.png' });

  // 点击元素
  await device.tap({ res: 'com.reddit.frontpage:id/post_footer' });

  // 滑动
  await device.fling({ res: 'com.reddit.frontpage:id/feed_container' }, 'down');
})();
```

**与 DPS 集成的测试**:

```javascript
const { _android: android } = require('playwright');
const fs = require('fs');

async function testDPSConfiguration() {
  const path = require('path');
  const projectRoot = path.resolve(__dirname, '..');
  
  const redditIntents = JSON.parse(fs.readFileSync(
    projectRoot + '/Config/IntentMappings/reddit_intents.json'
  ));
  
  console.log('✓ Reddit like_content 存在:', redditIntents.intents.like_content);
  
  const [device] = await android.devices();
  await device.shell('am start -n com.reddit.frontpage/.MainActivity');
  await new Promise(resolve => setTimeout(resolve, 3000));
  await device.screenshot({ path: 'test_reddit_home.png' });
  
  console.log('✓ 测试完成');
}

testDPSConfiguration().catch(console.error);
```

#### 与 DPS v4.5 项目的兼容性

| 方面 | 兼容性 | 说明 |
|------|--------|------|
| **C# 代码** | ❌ 不兼容 | Playwright 主要支持 Node.js/Python，C# 支持有限 |
| **Modules/ 架构** | ❌ 不兼容 | 需要重写为 JavaScript/Python |
| **JSON 配置** | ✅ 完全兼容 | 可以直接读取现有配置文件 |
| **ZDProjects 脚本** | ⚠️ 需要转换 | 需要转换为 Playwright API 调用 |

#### Playwright Android 的限制

1. **实验性功能**：官方标注为实验性，API 可能变更
2. **原生 APP 支持有限**：主要针对 Chrome for Android 和 WebView
3. **需要 ADB**：必须通过 ADB 连接设备
4. **设备需要唤醒**：设备需要保持唤醒状态
5. **无根设备限制**：某些功能需要在开发者选项中启用

#### 方案结论

| 问题 | 答案 |
|------|------|
| **Playwright 可以替代 ZennoDroid 吗？** | ⚠️ 部分可以，但有重要限制 |
| **可以用于 DPS v4.5 测试吗？** | ✅ 可以用于配置验证和简单交互测试 |
| **原生 APP 支持如何？** | ⚠️ 实验性，不如 ZennoDroid 稳定 |
| **推荐使用吗？** | ✅ 作为补充工具，用于快速验证；❌ 不推荐完全替代 |

**推荐方案**: 混合方案 — CI/CD 用 Playwright 快速配置验证，本地开发用 ZennoDroid 完整测试，截图对比用 Playwright + AI 分析。

---

### 7.2 运行时测试计划

> 来源: TESTING_PLAN.md (v1.0, 2026-02-27)
> 目标: 验证 DPS v4.5 所有核心模块在 ZennoDroid 环境中的运行时行为

#### 测试环境要求

**必需的 ZD 变量**:

| 变量名 | 类型 | 示例值 | 说明 |
|--------|------|--------|------|
| `project_root` | String | `C:\DPS_v4.5\` | 项目根目录（必须以 `\` 结尾） |
| `device_id` | String | `default` | 设备标识符（可选） |

**必需的文件**: `Modules/Initializer.cs`, `Modules/Core/CoreHelper.cs`, `Modules/Core/JsonHelper.cs`, `Modules/Core/SelectorEngine.cs`, `Modules/Core/PageDetector.cs`, `Modules/Core/ActionExecutor.cs`, `Modules/Core/NavigationResolver.cs`, `Modules/Core/VisionCorrector.cs`, `Modules/Core/ManifestLoader.cs`, `Modules/Core/RateLimiter.cs`, `ZDProjects/ModuleLoader.cs`, `Configs/Manifests/instagram.json`, `Config/AIConfig.json`

#### 测试场景

##### 测试 1: 项目初始化验证

**目标**: 验证 Initializer 模块能否正确创建目录结构并检查配置文件

| 检查项 | 预期结果 | 验证方法 |
|--------|----------|----------|
| 返回值 | `"SUCCESS"` 或 `"WARNING: 有缺失的配置或模块"` | 字符串匹配 |
| ZD 变量 `initializer_result` | `"SUCCESS"` 或 `"WARNING"` | 读取变量 |
| 目录创建 | `Config/`, `Modules/`, `Logs/`, `Screenshots/` 等存在 | 文件系统检查 |
| VisionCorrector 初始化 | 日志中包含 `[VisionCorrector] 初始化成功` | 日志检查 |

##### 测试 2: 动态编译验证

**目标**: 验证 ModuleLoader 能否正确编译包含新模块的代码

| 检查项 | 预期结果 | 验证方法 |
|--------|----------|----------|
| 编译成功 | 无编译错误日志 | 日志检查 |
| RateLimiter.cs 包含 | 日志中无 CS0246 错误 | 日志检查 |
| 缓存机制 | 第二次调用显示 `[ModuleLoader] 缓存命中` | 日志检查 |
| 方法调用成功 | 返回 `"SUCCESS"` | 返回值检查 |

##### 测试 3: Instagram 导航路径测试

**目标**: 验证 NavigationResolver 能否正确加载 Manifest 并计算路径

| 检查项 | 预期结果 | 验证方法 |
|--------|----------|----------|
| Manifest 加载 | `LoadFromManifest` 返回 `true` | 返回值检查 |
| 导航边数量 | 日志显示 `加载了 12 条导航边` | 日志检查 |
| home → notifications 路径 | 返回非 null，步数 = 1 | 路径对象检查 |
| home → post_detail 路径 | 返回非 null，步数 = 2 | 路径对象检查 |

##### 测试 4: 速率限制验证

**目标**: 验证 RateLimiter 能否正确解析和应用速率限制配置

| 检查项 | 预期结果 | 验证方法 |
|--------|----------|----------|
| `per_hour` 值 | `30` | JSON 解析 |
| `cooldown_seconds` 值 | `120` | JSON 解析 |
| RateLimiter 初始化 | 无异常 | 异常捕获 |

##### 测试 5: VisionCorrector 集成测试

**目标**: 验证 VisionCorrector 能否正确初始化并调用 Gemini API

| 检查项 | 预期结果 | 验证方法 |
|--------|----------|----------|
| 初始化成功 | `IsInitialized()` 返回 `true` | 方法调用 |
| 截图捕获 | 返回非空文件路径 | 字符串检查 |
| Gemini API 调用 | 返回 JSON 格式结果 | JSON 解析 |
| JSON 包含必需字段 | `on_expected_page`, `current_page`, `recovery_action` | JSON 字段检查 |

#### 测试执行顺序与依赖

推荐: 测试 1 → 2 → 3 → 4 → 5（测试 2-5 依赖测试 1 通过）

#### 测试完成标准

- ✅ 测试 1-4 全部返回 PASS
- ✅ 测试 5 返回 PASS 或 SKIP（如果无网络）
- ✅ 无编译错误、无运行时异常

---

### 7.3 快速测试执行指南

> 来源: QUICK_START_TESTING.md (2026-02-27)

#### 前置条件

- [ ] Android 模拟器已启动
- [ ] Instagram APP 已安装
- [ ] ZennoDroid 已连接到模拟器
- [ ] 项目路径已设置

#### 方式1: 一键自动测试（推荐）

设置 ZennoDroid 变量后，复制以下代码到 Own Code 动作块：

```csharp
string testRunnerPath = project_root + "ZDProjects/RuntimeTestRunner.cs";
string testRunnerCode = System.IO.File.ReadAllText(testRunnerPath);

var provider = new Microsoft.CSharp.CSharpCodeProvider();
var parameters = new System.CodeDom.Compiler.CompilerParameters();
parameters.GenerateInMemory = true;
parameters.ReferencedAssemblies.Add("System.dll");

var results = provider.CompileAssemblyFromSource(parameters, testRunnerCode);
if (results.Errors.Count > 0) { project.SendErrorToLog("编译失败"); return "ERROR"; }

var assembly = results.CompiledAssembly;
var type = assembly.GetType("RuntimeTestRunner");
var method = type.GetMethod("Run");
string result = (string)method.Invoke(null, new object[] { project, instance });
project.SendInfoToLog("测试结果: " + result);
return result;
```

测试完成后检查: ZennoDroid 日志输出、`Reports/runtime_test_report.txt`、ZD 变量 `test_result`

#### 方式2: 手动逐个测试

##### 测试1: 模拟器连接

```csharp
dynamic droid = instance.DroidInstance;
project.SendInfoToLog("设备信息: " + droid.GetDeviceInfo());
project.SendInfoToLog("分辨率: " + droid.Screen.Width + "x" + droid.Screen.Height);
```

##### 测试2: 项目初始化

```csharp
string result = Initializer.Run(project);
project.SendInfoToLog("初始化结果: " + result);
bool dirExists = System.IO.Directory.Exists(project_root + "Screenshots");
project.SendInfoToLog("Screenshots 目录存在: " + dirExists);
```

##### 测试3: AppExplorer 探索 Instagram

```csharp
dynamic droid = instance.DroidInstance;
droid.StartApp("com.instagram.android");
System.Threading.Thread.Sleep(5000);
string outputPath = project_root + "Configs/Manifests/instagram_explored.json";
string result = AppExplorer.Explore(project, instance, "com.instagram.android", outputPath);
project.SendInfoToLog("探索结果: " + result);
```

##### 测试4: 导航路径验证

```csharp
string manifestPath = project_root + "Configs/Manifests/instagram.json";
string manifestJson = System.IO.File.ReadAllText(manifestPath);
bool loaded = NavigationResolver.LoadManifest(manifestJson);
string[] path1 = NavigationResolver.FindPath("home", "notifications");
string[] path2 = NavigationResolver.FindPath("home", "direct_messages");
project.SendInfoToLog("home → notifications: " + (path1 != null ? string.Join(" → ", path1) : "不存在"));
```

##### 测试5: 速率限制验证

```csharp
string manifestJson = System.IO.File.ReadAllText(project_root + "Configs/Manifests/instagram.json");
string operationsJson = JsonHelper.ExtractObject(manifestJson, "operations");
string likePostsJson = JsonHelper.ExtractObject(operationsJson, "like_feed_posts");
string rateLimitJson = JsonHelper.ExtractObject(likePostsJson, "rate_limit");
int perHour = JsonHelper.GetInt(rateLimitJson, "per_hour", 0);
int cooldownSeconds = JsonHelper.GetInt(rateLimitJson, "cooldown_seconds", 0);
project.SendInfoToLog("速率限制: " + perHour + "/hour, 冷却 " + cooldownSeconds + "秒");
```

##### 测试6: VisionCorrector 视觉修正

```csharp
dynamic droid = instance.DroidInstance;
byte[] screenshot = droid.Screen.ScreenshotAsArray();
string screenshotPath = project_root + "Screenshots/test_screenshot.png";
System.IO.File.WriteAllBytes(screenshotPath, screenshot);
string prompt = "分析这个 Instagram 屏幕截图，识别当前页面类型";
string result = VisionCorrector.AnalyzeAndRecover(project, instance, prompt);
project.SendInfoToLog("VisionCorrector 结果: " + result);
```

#### 测试成功标准

| 测试 | 成功标准 |
|------|---------|
| 测试1 | 设备信息有效，分辨率 > 0 |
| 测试2 | 返回 "SUCCESS"，Screenshots 目录存在 |
| 测试3 | Manifest 文件生成，包含必需字段 |
| 测试4 | 两条导航路径都存在 |
| 测试5 | 速率限制为 30/hour, 120秒 |
| 测试6 | VisionCorrector 返回有效结果 |

总体成功标准: 至少 5/6 测试通过

#### 常见问题排查

| 问题 | 原因 | 解决方案 |
|------|------|----------|
| DroidInstance 为 null | 模拟器未连接 | 检查 `adb devices`，重新连接 |
| AppExplorer 探索失败 | APP 未完全启动 | 增加等待时间到 10s |
| VisionCorrector 返回 ERROR | API Key 无效或网络问题 | 检查 `Config/AIConfig.json` |
| NavigationResolver 路径不存在 | `instagram.json` 缺少导航边 | 检查 `navigation.edges` 数组 |

---

## Part 8: 术语表 (Glossary)

> 版本: v1.4 | 更新日期: 2026-03-05

本节定义了 DPS v4.5 项目中使用的所有专业术语和缩写。

---

### A

**Action (动作)** — 用户在社交平台上执行的单个操作，如浏览、点赞、评论等。每个动作都有对应的权重和持续时间配置。

**ActionExecutor (操作执行器)** — v4.5 新增的统一操作执行引擎，位于 `Modules/Core/ActionExecutor.cs`。通过 JSON 步骤定义驱动执行流程，将平台操作抽象为可配置的步骤序列。

**Action Weight (动作权重)** — 决定某个动作被选中概率的数值。在 `BehaviorConfig.json` 中配置，用于加权随机选择算法。

**AI Service (AI 服务)** — 封装了多个 AI 模型调用的服务层，支持 Gemini、OpenAI 等提供商，具有自动重试和备选机制。

**App Onboarder (新平台接入工具)** — v4.5.10 新增。位于 `Tools/app_onboarder/` 的独立 Python CLI 工具，自动探索 APP UI 结构并生成 PlatformsConfig.json 条目、operations.json 和 E2E 测试脚本，用于快速接入新平台。

### B

**Backup Model (备选模型)** — AI 服务的第三层模型，当 Primary 和 Fallback 都失败时使用。

**Behavior Config (行为配置)** — 定义用户行为参数的配置文件 (`BehaviorConfig.json`)，包含动作权重、打字速度、持续时间等。

**Behavior Profile (行为配置文件)** — v4.5 新增。定义用户行为模式的预设配置：casual（默认）、speed_demon（快速）、deep_reader（慢速深读）、distracted（节奏波动大）。

**Big Five Personality (大五人格)** — 心理学人格模型：Openness（开放性）、Conscientiousness（尽责性）、Extraversion（外向性）、Agreeableness（宜人性）、Neuroticism（神经质）。

### C

**Compilation Cache (编译缓存)** — v4.5 新增。缓存已编译的模块，将后续运行时间从 ~500ms 降至 <10ms。

**Conception Date (受孕日期)** — 画像中记录的怀孕开始日期，用于计算当前孕周和阶段流转。

**Core Helper (核心辅助)** — 提供 ZD 变量读写、日志输出、文件操作等基础功能的核心模块。

**CSharpCodeProvider** — .NET Framework 提供的动态编译器，DPS 使用它在运行时编译 .cs 文件。仅支持 C# 5.0 语法。

### D

**Daily Update (每日更新)** — 每天运行一次的模块，更新画像中的时间相关字段（年龄、孕周、季节等）。

**DecisionConfig (决策配置)** — v4.5.1 新增。配置文件 (`Config/DecisionConfig.json`)，定义 RuleEngine 规则、疲劳模型参数、MemoryManager 去重窗口期和记忆限额等。

**Device ID (设备标识)** — 每个模拟设备的唯一标识符。格式如 `Device_001` 或 `R58M4816G8Y`。

**DPS (Dynamic Persona System)** — 动态人物画像系统，本项目的核心框架名称。

**.droid 文件** — ZennoDroid 项目文件格式，包含流程控制和 UI 操作逻辑。

### E

**Error Recovery (错误恢复)** — v4.5 新增。自动处理操作失败的机制，支持最多 3 次重试，使用指数退避策略（2s, 4s, 8s）。

**Evolution (进化)** — 画像属性随时间和行为模式变化的过程，由 `WeeklyEvolve` 模块执行。

**Exponential Backoff (指数退避)** — 重试策略，每次重试的等待时间呈指数增长。DPS 使用 2s → 4s → 8s 的退避序列。

**Extension (扩展)** — 通过 `IExtension` 接口实现的可插拔功能模块，由 `ExtensionManager` 管理生命周期。用于 IP 检查、地理位置模拟、天气同步等功能。

**ExtensionManager (扩展管理器)** — 位于 `Modules/Core/ExtensionManager.cs`。负责发现、加载、初始化和卸载扩展模块。

### F

**Fallback Model (备选模型)** — AI 服务的第二层模型，当 Primary 模型失败时自动切换。

**Fatigue Model (疲劳模型)** — v4.5.1 新增。SessionRunner 中的能量衰减系统，模拟用户疲劳。配置在 `DecisionConfig.json` 的 `fatigue_model` 节。

**FileHelper (文件辅助)** — 提供文件读写、原子写入、目录管理等功能的核心模块。

**Fuzzy Memory (模糊记忆)** — 超过 180 天的事件会被转换为模糊描述，如 "2025-06-15 腿骨折" → "去年 6 月中旬腿摔断了"。

### H

**Humanization Engine / HumanizationEngine (人性化引擎)** — v4.5 新增。位于 `Core/HumanizationEngine.cs`，模拟真实用户行为的核心引擎。提供 4 种行为配置文件，控制点击延迟、滚动速度、随机偏移等参数。

### I

**IExtension (扩展接口)** — 位于 `Modules/Core/IExtension.cs`。所有扩展模块必须实现的接口，定义了 `Name`、`Initialize()`、`Execute()` 和 `Cleanup()` 方法。

**Intent (意图类)** — v4.5.6 新增，位于 `Modules/Core/Intent.cs`。高层操作意图的抽象类，表示"想做什么"（如 like_content、browse_feed），由 IntentTranslator 翻译为具体的 ZDCommand 物理命令。

**IntentTranslator (意图翻译器)** — v4.5.6 新增，位于 `Modules/Core/IntentTranslator.cs`。负责将 Intent 对象翻译为 ZDCommand 命令序列。

**Initializer (初始化器)** — 系统启动时运行的模块，负责创建目录结构、验证配置完整性。

### J

**JsonHelper (JSON 辅助)** — 手动实现的 JSON 解析器，不依赖外部库，支持字段读取、设置和转义处理。

### L

**Life Stage (生命阶段)** — 画像的当前生命周期阶段，共 7 个阶段：TTC, T1, T2, T3, PP0, PP1, NP。

**Long-term Memory (长期记忆)** — 保留 180 天以上的重要事件摘要，存储在 `Memory/{device_id}/long_term.json`。

### M

**Main (主入口)** — 系统的核心调度模块，检查画像状态并决定下一步操作。

**Maintenance (维护)** — 清理过期日志、备份和记忆文件的模块，根据 `MaintenanceConfig.json` 配置执行。

**Memory (记忆)** — 记录用户会话行为的数据，分为短期记忆、长期记忆、行为模式和社交记忆四层。

**MemoryManager (交互记忆管理器)** — v4.5.1 新增，位于 `Modules/MemoryManager.cs`。结构化交互记录系统，提供 `RecordInteraction()`、`IsDuplicate()`、`CleanupOldInteractions()`、`EnforceMemoryLimits()`。数据存储在 `Memory/{device_id}/{platform}/interactions.json`。

**MethodInfo** — .NET 反射 API 中表示方法信息的类，编译缓存中存储的就是 MethodInfo 对象。

**Module (模块)** — DPS 中的功能单元，每个模块是一个 .cs 文件，包含 `Run(object projectObj)` 入口方法。

**ModuleLoader (模块加载器)** — 负责动态编译和加载 .cs 模块的核心组件。

### N

**NP (Nursing Period)** — 育儿期，孩子 13 个月以上的阶段。

**NavigationResolver (导航解析器)** — v4.5.4 新增，位于 `Modules/Core/NavigationResolver.cs`。解析 APP 页面之间的导航路径。

### O

**OperationContext (操作上下文)** — 位于 `Modules/Core/OperationContext.cs`。ActionExecutor 执行步骤时的上下文对象，支持嵌套操作时的上下文传递。

**Own Code (自有代码)** — ZennoDroid 中的 C# 代码块，用于执行自定义逻辑。DPS 的入口点都是 Own Code 块。

### P

**PageDetector (页面检测器)** — v4.5 新增，位于 `Modules/Core/PageDetector.cs`。通过分析当前 UI 布局 XML 判断当前页面状态。

**Persona (人物画像)** — 包含 100+ 字段的用户模拟数据，包括基础信息、性格特征、兴趣偏好、行为参数等。

**PersonaCreate (画像创建)** — 调用 AI 生成初始画像的模块，使用 `PersonaPrompt.txt` 作为提示词。

**Platform (平台)** — v4.5 新增。当前已接入 Reddit、Instagram、BabyCenter（TikTok/Facebook 配置预留，默认未启用）。

**Platform Base (平台基类)** — v4.5 新增。定义平台模块标准接口的抽象类。

**Platform Module (平台模块)** — v4.5 新增。特定平台的操作实现，如 `RedditModule.cs`、`InstagramModule.cs`。

**PP0 (Postpartum 0)** — 产后早期，孩子 0-3 个月的阶段。

**PP1 (Postpartum 1)** — 产后中期，孩子 4-12 个月的阶段。

**Primary Model (主模型)** — AI 服务的首选模型，通常是 Gemini。

**project_root (项目根目录)** — DPS 项目的根目录路径，所有相对路径都基于此目录。

### R

**Rate Limit (速率限制)** — v4.5 新增。平台对操作频率的限制，如 Instagram 限制 60 actions/hour。

**RateLimiter (速率限制器)** — v4.5.4 新增，位于 `Modules/Core/RateLimiter.cs`。统一的速率限制实现。

**ReportGen (报告生成)** — 生成每日/每周运行报告的模块，在 17:00 后自动触发。

**Retry (重试)** — 操作失败后的自动重试机制，最多 3 次，使用指数退避策略。

**RuleEngine (规则引擎)** — v4.5.1 新增，位于 `Modules/RuleEngine.cs`。帖子评估门控系统。

**Run() 方法** — 每个模块的入口方法，签名为 `public static string Run(object projectObj)`。

### S

**Session (会话)** — 用户在平台上的一次连续活动，包含多个动作，有开始和结束时间。

**Session Plan (会话计划)** — 由 Main 模块生成的会话执行计划。

**SelectorEngine (选择器引擎)** — 位于 `Modules/Core/SelectorEngine.cs`。负责解析 `PlatformsConfig.json` 中的嵌套 `ui_selectors` 对象。

**SessionRunner (会话执行器)** — 执行会话计划的模块，负责选择动作、调用平台模块、记录行为。

**Short-term Memory (短期记忆)** — 保留 7-30 天的会话记录，存储在 `Memory/{device_id}/{date}.json`。

**Social Memory (社交记忆)** — 记录与其他用户互动历史的数据，存储在 `Memory/{device_id}/social.json`。

**Stage Code (阶段码)** — 表示生命阶段的缩写代码：TTC, T1, T2, T3, PP0, PP1, NP。

**Stage Config (阶段配置)** — 定义不同生命阶段参数的配置文件 (`StageConfig.json`)。

**StateSaver (状态保存器)** — 保存画像和运行统计的模块，在每次会话结束后执行。

**Stats (统计)** — 运行统计数据，存储在 `Stats/{device_id}_stats.json`。

### T

**T1 (Trimester 1)** — 孕早期，怀孕 1-13 周。

**T2 (Trimester 2)** — 孕中期，怀孕 14-27 周。

**T3 (Trimester 3)** — 孕晚期，怀孕 28-40 周。

**TTC (Trying To Conceive)** — 备孕期，未怀孕且无孩子的阶段。

**Typing Skill Level (打字技能等级)** — 画像中的打字速度参数，分为 slow、regular、fast 三个等级。

### U

**UI Locator (UI 定位器)** — v4.5 新增。多策略 UI 元素定位模块，支持 resource-id、XPath、图像识别等方式。

**VisionCorrector (视觉纠错器)** — v4.5.4 新增，位于 `Modules/Core/VisionCorrector.cs`。按需视觉验证模块，仅在操作失败时触发 Gemini Flash 截图分析。

### W

**Weekly Evolve (每周进化)** — 每周运行一次的模块，分析 7 天行为模式，由 AI 提出画像调整建议。

**Weight (权重)** — 用于加权随机选择的数值，决定某个选项被选中的概率。

### Z

**ZD (ZennoDroid)** — Android 模拟器自动化软件，DPS 的运行平台。

**ZDCommand (ZennoDroid 命令)** — v4.5.6 新增，位于 `Modules/Core/ZDCommand.cs`。物理执行命令的抽象类。

**ZDResult (执行结果)** — v4.5.6 新增，位于 `Modules/Core/ZDResult.cs`。统一执行结果类。

**ZD Variable (ZD 变量)** — ZennoDroid 项目中的变量，用于模块间数据传递。

**ZennoDroidAdapter (ZennoDroid 适配器)** — v4.5.6 新增，位于 `Modules/Core/ZennoDroidAdapter.cs`。ZennoDroid API 适配层。

---

### 缩写对照表

| 缩写 | 全称 | 中文 |
|------|------|------|
| DPS | Dynamic Persona System | 动态人物画像系统 |
| ZD | ZennoDroid | ZennoDroid 软件 |
| TTC | Trying To Conceive | 备孕期 |
| T1 | Trimester 1 | 孕早期 |
| T2 | Trimester 2 | 孕中期 |
| T3 | Trimester 3 | 孕晚期 |
| PP0 | Postpartum 0 | 产后早期 |
| PP1 | Postpartum 1 | 产后中期 |
| NP | Nursing Period | 育儿期 |
| AI | Artificial Intelligence | 人工智能 |
| API | Application Programming Interface | 应用程序接口 |
| JSON | JavaScript Object Notation | JSON 数据格式 |
| WPM | Words Per Minute | 每分钟字数 |

---

*文档版本: v1.0 | 最后更新: 2026-03-05 | 合并自 7 个源文档*

