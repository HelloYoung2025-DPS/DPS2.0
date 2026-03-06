# DPS v4.5 ConfigGuide_配置指南

> **更新**: 2026-03-05 (v4.5.10)  
> **来源**: GETTING_STARTED.md + CopyPaste_Setup.md + QuickSetup_Flowchart.md  
> **预计阅读时间**: 30 分钟  
> **目标**: 从零开始理解、配置并运行 DPS v4.5

---

## 一、项目简介

### 1.1 这是什么项目？

**DPS (Dynamic Persona System)** 是一个**通用移动自动化框架**，运行在 ZennoDroid 上。

#### 核心功能

| 功能 | 说明 |
|------|------|
| **AI 画像生成** | 自动生成 100+ 字段的虚拟人物画像 |
| **多平台支持** | Reddit、Instagram、BabyCenter（TikTok、Facebook 开发中） |
| **人性化行为** | 4 种行为模式，模拟真人操作 |
| **自动错误恢复** | 遇到问题自动重试，最多 3 次 |
| **记忆系统** | MemoryManager 结构化交互记录，去重 + 限额 |
| **规则引擎** | RuleEngine 帖子评估门控 |
| **统一执行引擎** | ActionExecutor JSON 步骤配置驱动 |
| **Intent-Based Execution** | v4.5.6+ DPS(大脑) + ZennoDroid(手) 分层架构 |

#### 工作流程简图

```
┌─────────────┐    ┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│  初始化     │ → │  生成画像   │ → │  执行会话   │ → │  保存状态   │
│ Initializer │    │ PersonaCreate│   │ SessionRunner│   │ StateSaver  │
└─────────────┘    └─────────────┘    └─────────────┘    └─────────────┘
```

#### Universal Framework 架构 (v4.5.6+)

```
DPS (大脑层)                    ZennoDroid (手层)
├─ Intent ("我想点赞")           ├─ ZennoDroidAdapter (API 封装)
├─ IntentTranslator (翻译)       ├─ SelectorEngine (元素定位)
└─ VisionCorrector (视觉验证)    └─ ScriptHelpers (人性化执行)
```

### 1.2 你需要准备什么？

#### 必备软件

| 软件 | 版本要求 | 用途 |
|------|----------|------|
| ZennoDroid | 7.x 或更高 | 运行自动化脚本 |
| 文本编辑器 | 任意 | 编辑配置文件 |
| (可选) Node.js | 18+ | Playwright Android 测试 |

#### 必备知识

- 基本的 ZennoDroid 操作（创建项目、添加变量）
- 基本的 JSON 格式理解（知道 `{}` 和 `""` 是什么）

#### 必备资源

- **AI API Key**（至少一个）：
  - Gemini API Key（推荐，免费额度高）
  - 或 OpenAI 兼容 API Key

#### 配置检查清单

在开始之前，请确认：

- [ ] 已安装 ZennoDroid 7.x 或更高版本
- [ ] 已下载 DPS_v4.5 项目文件夹
- [ ] 已获取至少一个 AI API Key

---

## 二、核心概念

### 2.1 项目结构

```
DPS_v4.5/
├── Config/              ← 配置文件（你需要编辑这里）
│   ├── Operations/      ← 平台操作步骤 JSON
│   └── IntentMappings/  ← Intent 映射配置
├── Modules/             ← 业务逻辑模块
│   └── Core/            ← 核心库（CoreHelper, JsonHelper, ActionExecutor 等）
│                       ← Intent, ZDCommand, ZDResult, IntentTranslator (v4.5.6+)
├── Core/                ← ZD 运行时引擎（ScriptHelpers, UILocator 等）
├── Extensions/          ← 扩展插件
│   ├── DataSources/
│   └── Hooks/
├── ZDProjects/          ← ZennoDroid 入口代码（复制到 ZD）
│   └── Tests/           ← E2E 测试脚本
├── Tools/
│   └── app_onboarder/   ← 新平台自动接入工具（v4.5.10）
├── Persons/             ← 生成的画像存储
├── Memory/              ← 交互记录（MemoryManager）
├── Data/                ← 运行数据
├── Reports/             ← 统计报告
├── Logs/                ← 日志
└── Docs/                ← 文档（你在这里）
```

### 2.2 核心概念

| 概念 | 解释 | 类比 |
|------|------|------|
| **画像 (Persona)** | AI 生成的虚拟人物资料 | 就像创建一个游戏角色 |
| **阶段 (Stage)** | 人物的生命周期阶段 | TTC→T1→T2→T3→PP0→PP1→NP |
| **会话 (Session)** | 一次自动化操作周期 | 打开 APP → 操作 → 关闭 |
| **人性化 (Humanization)** | 模拟真人的随机行为 | 随机延迟、偶尔误触 |
| **Intent (意图)** | v4.5.6+ 高层操作抽象 | "like_content" 而非 "Tap(540, 1800)" |

### 2.3 执行流程

```
1. Initializer    → 检查环境，创建目录
2. Main           → 检查画像状态，决定下一步
   ├─ 无画像      → PersonaCreate（生成画像）
   ├─ 需要更新    → DailyUpdate（更新画像）
   └─ 准备就绪    → 返回 READY
3. SessionRunner  → 执行社交媒体操作
   ├─ Intent 映射到操作序列
   ├─ ActionExecutor 执行 JSON 步骤
   ├─ MemoryManager 去重检测 + 交互记录
   ├─ RuleEngine 帖子评估门控
   └─ 成功门槛：有效动作成功率 >=95% 且至少 6 个成功动作
4. StateSaver     → 保存状态和统计
```

SessionRunner 常用输出变量（v4.5.8）：
- `session_success_rate`
- `session_successful_actions`
- `session_failed_actions`
- `session_skipped_actions`
- `action_attempt_count`
- `action_count`（仅成功动作数）

---

## 三、配置步骤

> **预计时间**: 15-20 分钟  
> **难度**: ⭐ 入门级

### 第一步：设置项目路径

#### 1.1 确定项目位置

将 `DPS_v4.5` 文件夹放到你想要的位置，例如：

```
C:\DPS_v4.5\
```

> ⚠️ **重要**: 路径中不要有中文或空格！

#### 1.2 记录你的项目路径

我的项目路径是：`________________________`（填写后继续）

---

### 第二步：配置 AI 服务

#### 2.1 打开配置文件

用文本编辑器打开：
```
你的项目路径\Config\AIConfig.json
```

#### 2.2 修改 API Key

找到以下内容并替换为你的 API Key：

```json
{
    "version": "1.0",
    "description": "DPS v4.5 AI服务配置",
    "models": {
        "primary": {
            "provider": "gemini",
            "model": "gemini-3-flash-preview",
            "api_key": "在这里填入你的Gemini API Key",
            "base_url": "https://generativelanguage.googleapis.com/v1beta",
            "timeout_ms": 60000,
            "max_tokens": 8192,
            "temperature": 0.7
        },
        "fallback": {
            "provider": "openai",
            "model": "claude-sonnet-4-5-thinking",
            "api_key": "在这里填入你的备用API Key（可选）",
            "base_url": "https://deeprouter.top/v1",
            "timeout_ms": 60000,
            "max_tokens": 4096,
            "temperature": 0.7
        },
        "backup": {
            "provider": "openai",
            "model": "gpt-5.2-2025-12-11",
            "api_key": "在这里填入第三备用API Key（可选）",
            "base_url": "https://api.shubiaobiao.com/v1",
            "timeout_ms": 30000,
            "max_tokens": 2048,
            "temperature": 0.7
        }
    },
    "retry_config": {
        "max_retries": 3,
        "retry_delay_ms": 1000,
        "backoff_multiplier": 2.0
    },
    "usage_limits": {
        "daily_request_limit": 1000,
        "hourly_request_limit": 100,
        "cost_alert_threshold_usd": 10.0
    }
}
```

> 💡 **提示**: 只需要配置 `primary`，`fallback` 和 `backup` 是可选的备用方案。

#### 2.3 保存文件

按 `Ctrl + S` 保存。

---

### 第三步：配置设备映射

#### 3.1 打开配置文件

用文本编辑器打开：
```
你的项目路径\Config\device_app_mapping.json
```

#### 3.2 添加你的设备

复制以下模板，修改设备 ID 和平台：

```json
{
    "version": "1.0",
    "description": "Maps devices to social media platforms for multi-platform automation",
    "devices": {
        "你的设备ID": {
            "platform": "reddit",
            "device_name": "你的设备名称",
            "notes": "备注信息"
        }
    },
    "default_platform": "reddit"
}
```

**平台选项**:
| 平台值 | 说明 |
|--------|------|
| `reddit` | Reddit 自动化 |
| `instagram` | Instagram 自动化 |
| `babycenter` | BabyCenter 自动化 |

**示例**（多设备）:
```json
{
    "version": "1.0",
    "description": "Maps devices to social media platforms for multi-platform automation",
    "devices": {
        "R58M4816G8Y": {
            "platform": "reddit",
            "device_name": "Samsung S21",
            "notes": "主力 Reddit 设备"
        },
        "PIXEL6PRO001": {
            "platform": "instagram",
            "device_name": "Pixel 6 Pro",
            "notes": "Instagram 设备"
        }
    },
    "default_platform": "reddit"
}
```

#### 3.3 保存文件

按 `Ctrl + S` 保存。

---

### 第四步：在 ZennoDroid 中创建变量

#### 4.1 打开 ZennoDroid

启动 ZennoDroid，创建一个新项目或打开现有项目。

#### 4.2 创建必填变量（3个）

在 ZennoDroid 的「项目变量」面板中，右键 → 添加变量：

| 变量名 | 类型 | 初始值 | 说明 |
|--------|------|--------|------|
| `project_root` | String | `C:\DPS_v4.5\` | ⚠️ 必须以 `\` 结尾 |
| `device_id` | String | `你的设备ID` | 与 device_app_mapping.json 中一致 |
| `current_app` | String | `reddit` | 当前运行的平台 |

#### 4.3 创建模块结果变量（8个）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `initializer_result` | String | （留空） |
| `main_result` | String | （留空） |
| `persona_result` | String | （留空） |
| `daily_result` | String | （留空） |
| `session_result` | String | （留空） |
| `report_result` | String | （留空） |
| `evolve_result` | String | （留空） |
| `extension_result` | String | （留空） |

#### 4.4 创建配置缓存变量（5个）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `ai_config_json` | String | （留空） |
| `stage_config_json` | String | （留空） |
| `behavior_config_json` | String | （留空） |
| `persona_json` | String | （留空） |
| `session_plan_json` | String | （留空） |

#### 4.5 创建控制变量（1个）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `force_regenerate` | String | `false` |

#### 4.6 创建状态变量（10个）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `last_error` | String | （留空） |
| `run_result` | String | （留空） |
| `action_count` | String | `0`（仅成功动作数） |
| `effective_date` | String | （留空） |
| `current_action` | String | （留空） |
| `current_post_id` | String | （留空） |
| `current_platform` | String | `reddit` |
| `_init_status` | String | （留空） |
| `_config_ok` | String | （留空） |
| `_module_ok` | String | （留空） |

#### 4.7 (v4.5.8) 创建会话统计变量（5个）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `action_attempt_count` | String | `0` |
| `session_success_rate` | String | `0` |
| `session_successful_actions` | String | `0` |
| `session_failed_actions` | String | `0` |
| `session_skipped_actions` | String | `0` |

#### 4.8 创建画像变量（3个）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `pregnancy_status` | String | （留空） |
| `special_requirements` | String | （留空） |
| `_config_missing` | String | （留空） |

> ✅ **检查点**: 你现在应该有 **35 个核心变量**（不含 Selector 变量）

#### 4.9 创建 Selector 变量（v4.5.1 新增，7个）

以下变量由 `RedditModule.cs` 初始化时自动设置，也可手动创建：

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `reddit_sel_post_unit` | String | `post_unit` |
| `reddit_sel_post_footer` | String | `post_footer` |
| `reddit_sel_upvote_button` | String | `post_footer_first_child` |
| `reddit_sel_comment_button` | String | `comment_button` |
| `reddit_sel_submit_button` | String | `submit_comment` |
| `reddit_sel_follow_button` | String | `follow_button` |
| `reddit_sel_share_button` | String | `share_button` |

> 💡 这些变量通常由平台模块自动从 `PlatformsConfig.json` 读取并设置，无需手动填写。

---

### 第五步：创建 Own Code 动作块

#### 5.1 创建 Initializer 动作块

1. 在 ZennoDroid 项目中，添加一个 **Own Code** 动作块
2. 命名为 `Initializer`
3. 复制 `ZDProjects\Initializer_OwnCode.cs` 文件的**全部内容**到动作块中

> ⚠️ **重要**: 直接打开并复制整个文件内容，不要手动输入！文件路径：
> ```
> 你的项目路径\ZDProjects\Initializer_OwnCode.cs
> ```

#### 5.2 创建 Main 动作块

1. 添加另一个 **Own Code** 动作块
2. 命名为 `Main`
3. 复制 `ZDProjects\Main_OwnCode.cs` 文件的**全部内容**到动作块中

> ⚠️ 文件路径：`你的项目路径\ZDProjects\Main_OwnCode.cs`

#### 5.3 创建 SessionRunner 动作块

1. 添加另一个 **Own Code** 动作块
2. 命名为 `SessionRunner`
3. 复制 `ZDProjects\SessionRunner_OwnCode.cs` 文件的**全部内容**到动作块中

> ⚠️ 文件路径：`你的项目路径\ZDProjects\SessionRunner_OwnCode.cs`

> 💡 **提示**: 其他模块的 OwnCode 同理，每个都对应 `ZDProjects/` 下的 `*_OwnCode.cs` 文件。
> 需要创建的动作块总共 **10 个**：Initializer, Main, SessionRunner, DailyUpdate, Extension, Maintenance, PersonaCreate, ReportGen, StateSaver, WeeklyEvolve

---

### 第六步：设置执行流程

#### 6.1 创建主流程

在 ZennoDroid 中按以下顺序连接动作块：

```
┌─────────────────┐
│   开始          │
└────────┬────────┘
         ▼
┌─────────────────┐
│  Initializer    │  ← 检查环境
└────────┬────────┘
         ▼
┌─────────────────┐
│  检查结果       │  ← 判断 initializer_result
└────────┬────────┘
         │
    ┌────┴────┐
    ▼         ▼
 SUCCESS    ERROR → 结束
    │
    ▼
┌─────────────────┐
│     Main        │  ← 检查画像状态
└────────┬────────┘
         ▼
┌─────────────────┐
│  检查结果       │  ← 判断 main_result
└────────┬────────┘
         │
    ┌────┼────┐
    ▼    ▼    ▼
 READY  NEED  ERROR → 结束
    │   CREATE
    │    │
    │    ▼
    │  PersonaCreate（自动触发）
    │    │
    ▼    ▼
┌─────────────────┐
│  SessionRunner  │  ← 执行社交媒体操作
└────────┬────────┘
         ▼
┌─────────────────┐
│     结束        │
└─────────────────┘
```

#### 6.2 添加条件判断

在 `Initializer` 后添加条件判断：
- 条件：`{-Variable.initializer_result-}` 包含 `SUCCESS`
- 是 → 继续到 Main
- 否 → 结束

在 `Main` 后添加条件判断：
- 条件：`{-Variable.main_result-}` 包含 `READY`
- 是 → 继续到 SessionRunner
- 否 → 结束（或处理其他状态）

---

### 第七步：首次运行测试

#### 7.1 运行 Initializer

1. 在 ZennoDroid 中运行 `Initializer` 动作块
2. 检查日志输出

**预期结果**:
```
[Initializer] 加载模块: C:\DPS_v4.5\Modules\Initializer.cs
[Initializer] 目录检查完成
[Initializer] 配置文件检查完成
SUCCESS: 初始化完成
```

#### 7.2 运行 Main

1. 运行 `Main` 动作块
2. 检查日志输出

**首次运行预期结果**:
```
[Main] 加载模块: C:\DPS_v4.5\Modules\Main.cs
[Main] 画像不存在，需要创建
NEED_CREATE_PERSONA
```

#### 7.3 检查生成的画像

运行后检查 `Persons/` 目录，应该有一个以你的 `device_id` 命名的 JSON 文件。

> ✅ 配置完成！恭喜！你已经完成了 DPS v4.5 的基本配置。

---

## 四、系统流程图

> 以下流程图帮助你可视化理解系统架构和各模块的工作方式。

### 4.1 系统总体架构

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           DPS v4.5 系统架构                              │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────┐                 │
│  │   Config/   │    │   Modules/  │    │    Core/    │                 │
│  │  配置文件    │───▶│   业务逻辑   │───▶│  运行时引擎  │                 │
│  └─────────────┘    └─────────────┘    └─────────────┘                 │
│         │                  │                  │                         │
│         ▼                  ▼                  ▼                         │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────┐                 │
│  │  Persons/   │    │   Memory/   │    │    Logs/    │                 │
│  │  画像存储    │    │   记忆存储   │    │   日志存储   │                 │
│  └─────────────┘    └─────────────┘    └─────────────┘                 │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### 4.2 首次运行流程

```
┌──────────────────────────────────────────────────────────────────────────┐
│                          首次运行流程                                     │
└──────────────────────────────────────────────────────────────────────────┘

开始
  │
  ▼
┌─────────────────────┐
│ 1. 检查 project_root │
│    变量是否设置      │
└──────────┬──────────┘
           │
     ┌─────┴─────┐
     │           │
    未设置      已设置
     │           │
     ▼           ▼
  ❌ 错误    ┌─────────────────────┐
  退出       │ 2. 运行 Initializer  │
             │    检查目录和配置     │
             └──────────┬──────────┘
                        │
                  ┌─────┴─────┐
                  │           │
               失败         成功
                  │           │
                  ▼           ▼
               ❌ 错误    ┌─────────────────────┐
               退出       │ 3. 运行 Main         │
                          │    检查画像状态       │
                          └──────────┬──────────┘
                                     │
                        ┌────────────┼────────────┐
                        │            │            │
                   无画像       需要更新        准备就绪
                        │            │            │
                        ▼            ▼            ▼
              ┌──────────────┐ ┌──────────────┐ ┌──────────────┐
              │ PersonaCreate│ │ DailyUpdate  │ │ SessionRunner│
              │ AI生成画像    │ │ 更新画像数据  │ │ 执行社交操作  │
              └──────┬───────┘ └──────┬───────┘ └──────┬───────┘
                     │                │                │
                     └────────────────┴────────────────┘
                                      │
                                      ▼
                              ┌──────────────┐
                              │  StateSaver  │
                              │  保存状态     │
                              └──────────────┘
                                      │
                                      ▼
                                   结束 ✅
```

### 4.3 模块执行流程详解

#### 4.3.1 Initializer 模块

```
┌─────────────────────────────────────────────────────────────┐
│                    Initializer 流程                          │
└─────────────────────────────────────────────────────────────┘

开始
  │
  ▼
┌─────────────────────┐
│ 检查目录结构         │
│ Config/, Persons/,  │
│ Memory/, Logs/...   │
└──────────┬──────────┘
           │
     ┌─────┴─────┐
     │           │
   不存在       存在
     │           │
     ▼           │
  创建目录       │
     │           │
     └─────┬─────┘
           ▼
┌─────────────────────┐
│ 检查配置文件         │
│ AIConfig.json       │
│ StageConfig.json    │
│ BehaviorConfig.json │
└──────────┬──────────┘
           │
     ┌─────┴─────┐
     │           │
   缺失         完整
     │           │
     ▼           ▼
  ⚠️ WARNING   ✅ SUCCESS
  返回缺失列表   初始化完成
```

**返回值说明**:
| 返回值 | 含义 | 下一步 |
|--------|------|--------|
| `SUCCESS` | 初始化成功 | 继续运行 Main |
| `WARNING: 缺少配置...` | 部分配置缺失 | 检查配置文件 |
| `ERROR: ...` | 严重错误 | 检查路径和权限 |

#### 4.3.2 Main 模块

```
┌─────────────────────────────────────────────────────────────┐
│                       Main 流程                              │
└─────────────────────────────────────────────────────────────┘

开始
  │
  ▼
┌─────────────────────┐
│ 加载配置文件         │
│ AIConfig.json       │
│ StageConfig.json    │
└──────────┬──────────┘
           │
           ▼
┌─────────────────────┐
│ 检查画像文件         │
│ Persons/{device_id} │
│ .json               │
└──────────┬──────────┘
           │
     ┌─────┴─────────────┐
     │                   │
   不存在               存在
     │                   │
     ▼                   ▼
  返回              ┌─────────────────────┐
  NEED_CREATE       │ 检查是否需要每日更新  │
  _PERSONA          │ 比较 effective_date  │
                    └──────────┬──────────┘
                               │
                         ┌─────┴─────┐
                         │           │
                      需要更新     不需要
                         │           │
                         ▼           ▼
                      返回         返回
                      NEED_DAILY   READY
                      _UPDATE
```

**返回值说明**:
| 返回值 | 含义 | 下一步 |
|--------|------|--------|
| `READY` | 准备就绪 | 运行 SessionRunner |
| `NEED_CREATE_PERSONA` | 需要创建画像 | 运行 PersonaCreate |
| `NEED_DAILY_UPDATE` | 需要每日更新 | 运行 DailyUpdate |
| `ERROR: ...` | 错误 | 检查日志 |

#### 4.3.3 SessionRunner 模块

```
┌─────────────────────────────────────────────────────────────┐
│                   SessionRunner 流程                         │
└─────────────────────────────────────────────────────────────┘

开始
  │
  ▼
┌─────────────────────┐
│ 确定平台             │
│ 读取 device_app_    │
│ mapping.json        │
└──────────┬──────────┘
           │
           ▼
┌─────────────────────┐
│ 加载平台执行链       │
│ Reddit / Instagram  │
│ / BabyCenter        │
│ (配置驱动或C#模块)   │
└──────────┬──────────┘
           │
           ▼
┌─────────────────────┐
│ 计算会话时长         │
│ 基于 StageConfig    │
└──────────┬──────────┘
           │
           ▼
┌─────────────────────────────────────────┐
│              动作循环                    │
│  ┌─────────────────────────────────┐   │
│  │ 1. 加权随机选择动作              │   │
│  │    browse(50%) / like(15%) /   │   │
│  │    comment(4%) / ...           │   │
│  └──────────────┬──────────────────┘   │
│                 │                       │
│                 ▼                       │
│  ┌─────────────────────────────────┐   │
│  │ 2. 执行平台特定操作              │   │
│  │    调用 RedditModule 或         │   │
│  │    InstagramModule             │   │
│  └──────────────┬──────────────────┘   │
│                 │                       │
│                 ▼                       │
│  ┌─────────────────────────────────┐   │
│  │ 3. 记录到 Memory                │   │
│  │    Memory/{device_id}/{date}   │   │
│  └──────────────┬──────────────────┘   │
│                 │                       │
│                 ▼                       │
│  ┌─────────────────────────────────┐   │
│  │ 4. 检查是否继续                  │   │
│  │    时间未到 && 操作数 < 50      │   │
│  └──────────────┬──────────────────┘   │
│                 │                       │
│           ┌─────┴─────┐                │
│           │           │                │
│         继续        结束               │
│           │           │                │
│           └───────────┘                │
└─────────────────────────────────────────┘
           │
           ▼
        返回
        SESSION_COMPLETE
```

### 4.4 平台模块流程

#### 4.4.1 Reddit 模块操作

```
┌─────────────────────────────────────────────────────────────┐
│                    Reddit 操作流程                           │
└─────────────────────────────────────────────────────────────┘

┌──────────────┐   ┌──────────────┐   ┌──────────────┐
│   Browse     │   │    Like      │   │   Comment    │
│   浏览帖子    │   │    点赞      │   │    评论      │
└──────┬───────┘   └──────┬───────┘   └──────┬───────┘
       │                  │                  │
       ▼                  ▼                  ▼
┌──────────────┐   ┌──────────────┐   ┌──────────────┐
│ 1.获取UI布局  │   │ 1.定位帖子   │   │ 1.定位帖子   │
│ 2.解析帖子列表│   │ 2.找到点赞按钮│   │ 2.点击评论框 │
│ 3.人性化滚动  │   │ 3.人性化点击  │   │ 3.AI生成评论 │
│ 4.记录浏览    │   │ 4.记录点赞    │   │ 4.人性化输入 │
└──────────────┘   └──────────────┘   │ 5.提交评论   │
                                      └──────────────┘

┌──────────────┐   ┌──────────────┐
│   Follow     │   │    Share     │
│   关注       │   │    分享      │
└──────┬───────┘   └──────┬───────┘
       │                  │
       ▼                  ▼
┌──────────────┐   ┌──────────────┐
│ 1.定位用户   │   │ 1.定位帖子   │
│ 2.找到关注按钮│   │ 2.点击分享   │
│ 3.人性化点击  │   │ 3.选择方式   │
│ 4.记录关注    │   │ 4.记录分享   │
└──────────────┘   └──────────────┘
```

#### 4.4.2 Instagram 模块操作

```
┌─────────────────────────────────────────────────────────────┐
│                   Instagram 操作流程                         │
└─────────────────────────────────────────────────────────────┘

⚠️ Instagram 有更严格的速率限制：
   - 最大 60 操作/小时
   - 最大 30 点赞/小时
   - 最大 10 评论/小时
   - 最大 20 关注/小时

┌──────────────┐   ┌──────────────┐   ┌──────────────┐
│   Browse     │   │    Like      │   │   Comment    │
│   浏览动态    │   │    点赞      │   │    评论      │
└──────┬───────┘   └──────┬───────┘   └──────┬───────┘
       │                  │                  │
       ▼                  ▼                  ▼
┌──────────────┐   ┌──────────────┐   ┌──────────────┐
│ 检查速率限制  │   │ 检查速率限制  │   │ 检查速率限制  │
│      ↓       │   │      ↓       │   │      ↓       │
│ 执行浏览操作  │   │ 双击点赞     │   │ 输入评论     │
│      ↓       │   │      ↓       │   │      ↓       │
│ 更新计数器    │   │ 更新计数器    │   │ 更新计数器    │
└──────────────┘   └──────────────┘   └──────────────┘
```

### 4.5 人性化行为流程

```
┌─────────────────────────────────────────────────────────────┐
│                    人性化行为系统                            │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│                    4 种行为配置文件                          │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐        │
│  │speed_demon  │  │   casual    │  │deep_reader  │        │
│  │   快速模式   │  │   休闲模式   │  │   深读模式   │        │
│  │ 延迟×0.6    │  │  延迟×1.0   │  │  延迟×1.5   │        │
│  │ 偏移 5px    │  │  偏移 15px  │  │  偏移 10px  │        │
│  └─────────────┘  └─────────────┘  └─────────────┘        │
│                                                             │
│  ┌─────────────┐                                           │
│  │ distracted  │                                           │
│  │   分心模式   │                                           │
│  │ 延迟×1.2    │                                           │
│  │ 偏移 20px   │                                           │
│  │ 误触率 5%   │                                           │
│  └─────────────┘                                           │
│                                                             │
└─────────────────────────────────────────────────────────────┘

人性化点击流程：
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│ 1.计算目标   │ → │ 2.添加随机   │ → │ 3.执行点击   │
│   坐标       │    │   偏移       │    │             │
└─────────────┘    └─────────────┘    └─────────────┘

人性化延迟流程：
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│ 1.基础延迟   │ → │ 2.乘以配置   │ → │ 3.添加随机   │
│   (ms)       │    │   倍数       │    │   变化       │
└─────────────┘    └─────────────┘    └─────────────┘
```

### 4.6 错误恢复流程

```
┌─────────────────────────────────────────────────────────────┐
│                    错误恢复机制                              │
└─────────────────────────────────────────────────────────────┘

操作执行
    │
    ▼
┌─────────────────────┐
│ 执行操作             │
└──────────┬──────────┘
           │
     ┌─────┴─────┐
     │           │
   成功         失败
     │           │
     ▼           ▼
  返回结果   ┌─────────────────────┐
             │ 重试次数 < 3?       │
             └──────────┬──────────┘
                        │
                  ┌─────┴─────┐
                  │           │
                 是          否
                  │           │
                  ▼           ▼
           ┌──────────┐   返回失败
           │ 等待延迟  │
           │ 2^n 秒   │
           └────┬─────┘
                │
                ▼
           重新执行操作

延迟计算：
  第1次重试: 2秒
  第2次重试: 4秒
  第3次重试: 8秒
```

### 4.7 配置文件关系图

```
┌─────────────────────────────────────────────────────────────┐
│                    配置文件关系                              │
└─────────────────────────────────────────────────────────────┘

┌─────────────────┐
│  AIConfig.json  │ ─────────────────────────────────────┐
│  AI服务配置      │                                      │
│  - API Keys     │                                      │
│  - 模型选择      │                                      │
│  - 重试配置      │                                      │
└────────┬────────┘                                      │
         │                                               │
         │ 被以下模块使用：                               │
         │ - PersonaCreate (生成画像)                    │
         │ - SessionRunner (生成评论)                    │
         ▼                                               │
┌─────────────────┐    ┌─────────────────┐              │
│ StageConfig.json│    │BehaviorConfig   │              │
│ 阶段参数配置     │    │.json            │              │
│ - 会话时长      │    │ 行为参数配置     │              │
│ - 每日会话数    │    │ - 动作权重       │              │
│ - 注意力系数    │    │ - 打字速度       │              │
└────────┬────────┘    └────────┬────────┘              │
         │                      │                       │
         │                      │                       │
         └──────────┬───────────┘                       │
                    │                                   │
                    ▼                                   │
         ┌─────────────────┐                           │
         │ SessionRunner   │ ◀─────────────────────────┘
         │ 会话执行引擎     │
         └────────┬────────┘
                  │
                  │ 读取
                  ▼
         ┌─────────────────┐
         │device_app_      │
         │mapping.json     │
         │ 设备-平台映射    │
         │ - device_id     │
         │ - platform      │
         └────────┬────────┘
                  │
                  │ 决定加载
                  ▼
         ┌─────────────────┐
         │PlatformsConfig  │
         │.json            │
         │ 平台特定配置     │
         │ - 速率限制       │
         │ - UI选择器       │
         └─────────────────┘
```

### 4.8 数据流向图

```
┌─────────────────────────────────────────────────────────────┐
│                      数据流向                                │
└─────────────────────────────────────────────────────────────┘

输入                    处理                    输出
─────                   ────                    ────

┌─────────┐        ┌─────────────┐        ┌─────────────┐
│ Config/ │  ───▶  │ Initializer │  ───▶  │ 目录结构     │
│ 配置文件 │        │             │        │ 创建完成     │
└─────────┘        └─────────────┘        └─────────────┘

┌─────────┐        ┌─────────────┐        ┌─────────────┐
│ AI API  │  ───▶  │PersonaCreate│  ───▶  │ Persons/    │
│ 响应    │        │             │        │ {id}.json   │
└─────────┘        └─────────────┘        └─────────────┘

┌─────────┐        ┌─────────────┐        ┌─────────────┐
│ Persona │  ───▶  │SessionRunner│  ───▶  │ Memory/     │
│ 画像数据 │        │             │        │ {id}/{date} │
└─────────┘        └─────────────┘        └─────────────┘

┌─────────┐        ┌─────────────┐        ┌─────────────┐
│ Memory  │  ───▶  │  ReportGen  │  ───▶  │ Reports/    │
│ 记忆数据 │        │             │        │ {id}/{date} │
└─────────┘        └─────────────┘        └─────────────┘
```

### 4.9 快速参考卡片

#### 模块执行顺序
```
1. Initializer  → 检查环境
2. Main         → 检查画像
3. SessionRunner → 执行操作
4. StateSaver   → 保存状态
```

#### 返回值速查
| 模块 | 成功返回值 | 需要处理的返回值 |
|------|-----------|-----------------|
| Initializer | `SUCCESS` | `WARNING`, `ERROR` |
| Main | `READY` | `NEED_CREATE_PERSONA`, `NEED_DAILY_UPDATE` |
| SessionRunner | `SESSION_COMPLETE` | `ERROR` |

#### 关键路径
| 用途 | 路径 |
|------|------|
| 配置文件 | `{project_root}\Config\` |
| 画像存储 | `{project_root}\Persons\` |
| 操作记录 | `{project_root}\Memory\{device_id}\` |
| 日志文件 | `{project_root}\Logs\` |

---

## 五、故障排除

### 5.1 故障排除决策树

```
┌─────────────────────────────────────────────────────────────┐
│                    故障排除决策树                            │
└─────────────────────────────────────────────────────────────┘

遇到问题
    │
    ▼
┌─────────────────────────────────────────┐
│ 错误信息是什么？                         │
└──────────────────┬──────────────────────┘
                   │
     ┌─────────────┼─────────────┬─────────────┐
     │             │             │             │
     ▼             ▼             ▼             ▼
"模块不存在"   "编译错误"    "API失败"    "变量未设置"
     │             │             │             │
     ▼             ▼             ▼             ▼
┌─────────┐  ┌─────────┐  ┌─────────┐  ┌─────────┐
│检查路径  │  │检查代码  │  │检查API  │  │检查变量  │
│project_ │  │是否完整  │  │Key是否  │  │是否创建  │
│root设置 │  │复制     │  │正确     │  │         │
└────┬────┘  └────┬────┘  └────┬────┘  └────┬────┘
     │            │            │            │
     ▼            ▼            ▼            ▼
┌─────────┐  ┌─────────┐  ┌─────────┐  ┌─────────┐
│确保以\  │  │重新复制  │  │测试API  │  │对照变量  │
│结尾     │  │代码     │  │连接     │  │清单创建  │
└─────────┘  └─────────┘  └─────────┘  └─────────┘
```

### 5.2 常见问题详解

#### 问题 1: "模块不存在"

**原因**: `project_root` 路径设置错误

**解决**:
1. 检查 `project_root` 变量值
2. 确保路径以 `\` 结尾
3. 确保路径指向正确的 DPS_v4.5 目录

#### 问题 2: "编译错误"

**原因**: 代码复制不完整或有格式问题

**解决**:
1. 重新复制完整代码
2. 确保没有多余的空格或换行
3. 检查 ZennoDroid 日志中的具体错误行号

#### 问题 3: "API 调用失败"

**原因**: API Key 无效或网络问题

**解决**:
1. 检查 `AIConfig.json` 中的 API Key
2. 测试网络连接
3. 尝试使用备用 API

#### 问题 4: "变量未设置"

**原因**: ZennoDroid 变量未创建或名称错误

**解决**:
1. 对照[第四步：在 ZennoDroid 中创建变量](#第四步在-zennodroid-中创建变量)检查所有变量
2. 变量名区分大小写
3. 确保必填变量有初始值

#### 问题 5: "画像生成失败"

**原因**: 网络连接问题或 API 配额不足

**解决**:
1. 检查网络连接
2. 确认 API 配额未超限
3. 等待一段时间后重试

#### 问题 6: "速率限制"

**原因**: 操作频率超过平台限制

**解决**:
1. 等待一小时后重试
2. 检查 `BehaviorConfig.json` 中的速率限制配置

### 5.3 常见错误速查表

```
┌────────────────────┬─────────────────────────────────────┐
│ 错误信息            │ 解决方案                            │
├────────────────────┼─────────────────────────────────────┤
│ project_root 未设置 │ 在ZD变量中设置，确保以\结尾          │
│ 模块不存在          │ 检查路径是否正确，文件是否存在        │
│ 编译错误 行XX       │ 检查代码复制是否完整                 │
│ API调用失败         │ 检查AIConfig.json中的API Key        │
│ 画像生成失败        │ 检查网络连接和API配额                │
│ 速率限制            │ 等待一小时后重试                    │
└────────────────────┴─────────────────────────────────────┘
```

### 5.4 获取帮助

如果遇到问题：

1. 先查看 [术语表](GLOSSARY.md) 确认理解正确
2. 再查看上面的故障排除决策树
3. 检查 `Logs/` 目录下的错误日志
4. 查看 `Docs/FIX_REPORT_2026-02-27.md` 了解最新修复

---

## 六、变量快速参考表

### 必须设置的变量（3个）

| 变量名 | 示例值 | 说明 |
|--------|--------|------|
| `project_root` | `C:\DPS_v4.5\` | 必须以 `\` 结尾 |
| `device_id` | `R58M4816G8Y` | 你的设备 ID |
| `current_app` | `reddit` | reddit、instagram 或 babycenter |

### 模块结果变量（8个）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `initializer_result` | String | （留空） |
| `main_result` | String | （留空） |
| `persona_result` | String | （留空） |
| `daily_result` | String | （留空） |
| `session_result` | String | （留空） |
| `report_result` | String | （留空） |
| `evolve_result` | String | （留空） |
| `extension_result` | String | （留空） |

### 配置缓存变量（5个）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `ai_config_json` | String | （留空） |
| `stage_config_json` | String | （留空） |
| `behavior_config_json` | String | （留空） |
| `persona_json` | String | （留空） |
| `session_plan_json` | String | （留空） |

### 控制变量（1个）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `force_regenerate` | String | `false` |

### 状态变量（10个）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `last_error` | String | （留空） |
| `run_result` | String | （留空） |
| `action_count` | String | `0`（仅成功动作数） |
| `effective_date` | String | （留空） |
| `current_action` | String | （留空） |
| `current_post_id` | String | （留空） |
| `current_platform` | String | `reddit` |
| `_init_status` | String | （留空） |
| `_config_ok` | String | （留空） |
| `_module_ok` | String | （留空） |

### 会话统计变量（v4.5.8，5个）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `action_attempt_count` | String | `0` |
| `session_success_rate` | String | `0` |
| `session_successful_actions` | String | `0` |
| `session_failed_actions` | String | `0` |
| `session_skipped_actions` | String | `0` |

### 画像变量（3个）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `pregnancy_status` | String | （留空） |
| `special_requirements` | String | （留空） |
| `_config_missing` | String | （留空） |

### Selector 变量（v4.5.1，7个）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `reddit_sel_post_unit` | String | `post_unit` |
| `reddit_sel_post_footer` | String | `post_footer` |
| `reddit_sel_upvote_button` | String | `post_footer_first_child` |
| `reddit_sel_comment_button` | String | `comment_button` |
| `reddit_sel_submit_button` | String | `submit_comment` |
| `reddit_sel_follow_button` | String | `follow_button` |
| `reddit_sel_share_button` | String | `share_button` |

### 其他变量

全部创建为 String 类型，初始值留空即可。系统运行时会自动填充。

> ✅ **总计**: 35 个核心变量 + 7 个 Selector 变量 = **42 个变量**

---

## 七、常见问题

### Q: 我需要会编程吗？
**A**: 不需要。所有代码都已写好，你只需要复制粘贴和修改配置文件。

### Q: 支持哪些平台？
**A**: 目前支持 **Reddit**、**Instagram** 和 **BabyCenter**。TikTok 和 Facebook 在开发中。

### Q: API Key 从哪里获取？
**A**:
- Gemini: https://makersuite.google.com/app/apikey
- OpenAI: https://platform.openai.com/api-keys

### Q: 运行出错怎么办？
**A**: 查看 `Logs/` 目录下的日志文件，或参考[第五节：故障排除](#五故障排除)。

### Q: 什么是 Intent-Based Execution？
**A**: v4.5.6 引入的新架构，DPS 不再直接调用 ZennoDroid API，而是通过"意图"（如 like_content）翻译为物理命令。详见 `UnifiedIntentArchitecture.md`。

### Q: 如何测试配置是否正确？
**A**: 使用 `ZDProjects/Tests/` 下的 E2E 测试脚本，或使用 Playwright Android 测试（`playwright_dps_test.js`）进行快速验证。

### Q: 为什么 `action_count` 变小了？
**A**: 从 v4.5.8 开始，`action_count` 只统计成功动作，不再把 `SKIP` 计入；如需完整统计请看 `action_attempt_count`。

---

**下一步**: 查看 [术语表](GLOSSARY.md) 了解专业术语
