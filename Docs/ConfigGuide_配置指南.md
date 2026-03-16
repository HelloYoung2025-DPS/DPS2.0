# DPS v4.5 ConfigGuide_配置指南

> **更新**: 2026-03-07 (v4.5.16)  
> **来源**: GETTING_STARTED.md + CopyPaste_Setup.md + QuickSetup_Flowchart.md  
> **预计阅读时间**: 35 分钟  
> **目标**: 从零开始理解、配置并运行 DPS v4.5  
> **定位**: 本文档是 **ZennoDroid 新人施工手册**。如果你是第一次搭建项目，请优先照着本文的变量表、复制清单和流程图执行。

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
   ├─ 从 device_app_mapping.json 确定平台
   ├─ Intent 映射到 operation 序列
   ├─ ActionExecutor 执行 Config/Operations/*.json 步骤
   ├─ MemoryManager 去重检测 + 交互记录
   ├─ RuleEngine 帖子评估门控
   └─ 当前成功门槛：action_count > 0 且 failedActions <= action_count / 2
4. StateSaver     → 保存状态和统计
```

SessionRunner 当前主链常用输出变量：
- `session_result`
- `run_result`
- `action_count`（动作循环次数，含成功/失败/SKIP）
- `current_platform`
- `current_action`
- `current_intent`

> ⚠️ **重要**: 当前代码会把 `current_platform` 作为**运行时输出变量**写回，而不是要求你手工输入 `current_app`。

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

#### 4.2 先创建最小必填变量（推荐先做）

先只创建下面 2 个变量，就可以开始跑最小闭环：

| 变量名 | 类型 | 初始值 | 说明 |
|--------|------|--------|------|
| `project_root` | String | `C:\DPS_v4.5\` | ⚠️ 必须以 `\` 结尾 |
| `device_id` | String | `你的设备ID` | 必须与 `Config\device_app_mapping.json` 中一致 |

> ⚠️ **不要再创建 `current_app` 作为输入变量。** 当前主链由 `SessionRunner` 根据 `device_id` 自动确定平台，并把结果写回 `current_platform`。

#### 4.3 创建控制变量（推荐）

| 变量名 | 类型 | 初始值 | 说明 |
|--------|------|--------|------|
| `force_regenerate` | String | `false` | 需要强制重建画像时再设为 `true` |

#### 4.4 创建主链结果变量（推荐预创建）

这些变量不是启动主链的输入，但建议预先创建，便于在 ZD 条件块中判断返回值：

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
| `run_result` | String | （留空） |
| `last_error` | String | （留空） |

#### 4.5 创建配置缓存变量（推荐）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `ai_config_json` | String | （留空） |
| `stage_config_json` | String | （留空） |
| `behavior_config_json` | String | （留空） |
| `persona_json` | String | （留空） |
| `session_plan_json` | String | （留空） |

#### 4.6 创建会话调试变量（推荐）

| 变量名 | 类型 | 初始值 | 说明 |
|--------|------|--------|------|
| `action_count` | String | `0` | 当前代码里表示动作循环次数 |
| `current_action` | String | （留空） | 当前动作名 |
| `current_intent` | String | （留空） | 当前意图名 |
| `current_page` | String | （留空） | 当前页面状态 |
| `current_platform` | String | （留空） | 运行时自动写入 |
| `current_post_id` | String | （留空） | 当前帖子标识 |
| `current_post_json` | String | （留空） | 当前帖子语义 JSON |
| `comment_text` | String | （留空） | 评论文本 |
| `ai_comment_text` | String | （留空） | AI 生成评论文本 |
| `effective_intent` | String | （留空） | 实际执行的意图 |

#### 4.7 创建初始化/诊断变量（可选）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `init_status` | String | （留空） |
| `config_ok` | String | （留空） |
| `module_ok` | String | （留空） |
| `config_missing` | String | （留空） |
| `vision_verified` | String | （留空） |
| `pending_action` | String | （留空） |
| `pending_action_type` | String | （留空） |
| `pending_platform` | String | （留空） |

#### 4.8 变量创建建议

- **最小闭环**：至少创建 `project_root`、`device_id`、`initializer_result`、`main_result`、`persona_result`、`session_result`
- **建议调试**：把 4.4 ～ 4.7 全部预创建，后续排查最省事
- **兼容行为**：当前 `CoreHelper.SetVar()` 对不存在的输出变量会静默忽略，所以少建调试变量不会阻止主链运行，但会丢失可观察性

#### 4.9 Selector 变量说明

`reddit_sel_*` 这类选择器变量属于历史 Reddit 脚本链路，**不是新人搭建 DPS 主链的必需变量**。当前统一主链优先使用：

- `Config\PlatformsConfig.json`
- `Config\Operations\*.json`
- `Config\IntentMappings\*.json`

---

### 第五步：创建 Own Code 动作块

> **推荐策略**: 第一次搭建时，先只创建 4 个动作块：`Initializer`、`Main`、`PersonaCreate`、`SessionRunner`。  
> 跑通最小闭环后，再补 `DailyUpdate`、`StateSaver`、`ReportGen` 等模块。

> **所有 Own Code 块的创建步骤都一样：**
> 1. 在工作区空白处**右键** → **Add action** → **Custom code** → **C# code**
> 2. **双击**新块，打开代码编辑器
> 3. 用文本编辑器打开对应 .cs 文件 → **Ctrl+A** 全选 → **Ctrl+C** 复制
> 4. 回到 ZD 代码编辑器 → **Ctrl+A** → **Ctrl+V** 粘贴覆盖 → 点 **Save**
> 5. **右键**块 → **Comment** → 输入块名（如 `Initializer`）
>
> 详细图文说明可回看 [4.2.3 节"C. 第二步：创建 C# code 方块"](#c-第二步创建-c-code-方块)。

#### 5.1 创建 Initializer 动作块

按上方通用步骤创建 C# code 块。

- **块名（Comment）**: `Initializer`
- **代码来源**: `你的项目路径\ZDProjects\Initializer_OwnCode.cs`

> ⚠️ **重要**: 直接打开并复制整个文件内容，不要手动输入！

#### 5.2 创建 Main 动作块

按上方通用步骤创建 C# code 块。

- **块名（Comment）**: `Main`
- **代码来源**: `你的项目路径\ZDProjects\Main_OwnCode.cs`

#### 5.3 创建 SessionRunner 动作块

按上方通用步骤创建 C# code 块。

- **块名（Comment）**: `SessionRunner`
- **代码来源**: `你的项目路径\ZDProjects\SessionRunner_OwnCode.cs`

#### 5.4 创建 PersonaCreate 动作块（首次运行必须）

按上方通用步骤创建 C# code 块。

- **块名（Comment）**: `PersonaCreate`
- **代码来源**: `你的项目路径\ZDProjects\PersonaCreate_OwnCode.cs`

#### 5.5 推荐的首批动作块清单

| ZennoDroid 动作块名 | 复制来源文件 | 用途 | 首次搭建是否必需 |
|---------------------|--------------|------|------------------|
| `Initializer` | `ZDProjects\Initializer_OwnCode.cs` | 检查目录、配置、运行环境 | 是 |
| `Main` | `ZDProjects\Main_OwnCode.cs` | 判断是否需要创建/更新画像 | 是 |
| `PersonaCreate` | `ZDProjects\PersonaCreate_OwnCode.cs` | 首次生成画像 | 是 |
| `SessionRunner` | `ZDProjects\SessionRunner_OwnCode.cs` | 执行社交动作主链 | 是 |
| `DailyUpdate` | `ZDProjects\DailyUpdate_OwnCode.cs` | 每日画像更新 | 否，第二阶段再补 |
| `StateSaver` | `ZDProjects\StateSaver_OwnCode.cs` | 保存状态 | 否，第二阶段再补 |
| `Extension` | `ZDProjects\Extension_OwnCode.cs` | 扩展数据源 | 否 |
| `ReportGen` | `ZDProjects\ReportGen_OwnCode.cs` | 生成报告 | 否 |
| `WeeklyEvolve` | `ZDProjects\WeeklyEvolve_OwnCode.cs` | 每周画像演化 | 否 |
| `Maintenance` | `ZDProjects\Maintenance_OwnCode.cs` | 清理维护 | 否 |

---

### 第六步：设置执行流程

#### 6.1 首次最小可运行施工图（照着搭）

第一次在 ZennoDroid 中搭建时，请先连出下面这条**最小闭环**：

```
开始
  ↓
Initializer
  ↓ 判断 `{-Variable.initializer_result-}` 是否包含 `SUCCESS`
  ├─ 否 → 结束并看日志
  └─ 是 → Main
           ↓ 判断 `{-Variable.main_result-}` 是否包含 `NEED_CREATE_PERSONA`
           ├─ 是 → PersonaCreate → 回到 Main
           └─ 否 → 判断 `{-Variable.main_result-}` 是否包含 `READY`
                    ├─ 是 → SessionRunner → 结束
                    └─ 否 → 结束并看日志
```

#### 6.2 详细施工图（动作块 / 返回值 / 条件）

| 步骤 | ZennoDroid 动作块 | 复制来源 | 预期返回值 | 条件表达式 | 下一步 |
|------|-------------------|----------|------------|------------|--------|
| 1 | `Initializer` | `ZDProjects\Initializer_OwnCode.cs` | `SUCCESS` / `WARNING` / `ERROR: ...` | `{-Variable.initializer_result-}` 包含 `SUCCESS` | 成功进 `Main` |
| 2 | `Main` | `ZDProjects\Main_OwnCode.cs` | `READY` / `NEED_CREATE_PERSONA` / `NEED_DAILY_UPDATE` / `ERROR: ...` | `{-Variable.main_result-}` | 按不同返回值分支 |
| 3 | `PersonaCreate` | `ZDProjects\PersonaCreate_OwnCode.cs` | `SUCCESS` / `ERROR: ...` | `{-Variable.persona_result-}` 包含 `SUCCESS` | 成功后回到 `Main` |
| 4 | `SessionRunner` | `ZDProjects\SessionRunner_OwnCode.cs` | `SUCCESS` / `ERROR: ...` | `{-Variable.session_result-}` 包含 `SUCCESS` | 成功结束 |

#### 6.3 条件判断怎么写（直接照抄）

在 `Initializer` 后添加条件判断：
- 条件：`{-Variable.initializer_result-}` 包含 `SUCCESS`
- 是 → 继续到 Main
- 否 → 结束

在 `Main` 后添加条件判断：
- 条件 1：`{-Variable.main_result-}` 包含 `NEED_CREATE_PERSONA`
- 是 → 运行 `PersonaCreate`
- 否 → 继续判断条件 2

- 条件 2：`{-Variable.main_result-}` 包含 `READY`
- 是 → 继续到 `SessionRunner`
- 否 → 结束（或记录日志后人工处理）

在 `PersonaCreate` 后添加条件判断：
- 条件：`{-Variable.persona_result-}` 包含 `SUCCESS`
- 是 → 回到 `Main`
- 否 → 结束

#### 6.4 第二阶段再补的完整流程

最小闭环稳定后，再补完整生产链：

```
Initializer
  → Main
     ├─ NEED_CREATE_PERSONA → PersonaCreate → Main
     ├─ NEED_DAILY_UPDATE   → DailyUpdate   → Main
     └─ READY               → Extension（可选）
                            → SessionRunner
                            → StateSaver（推荐）
                            → ReportGen（按需）
                            → Maintenance（按需）
```

> 💡 **施工建议**: 不要第一次就把 10 个模块全接上。先跑通最小闭环，再逐个补模块，定位问题最快。

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

#### 7.3 运行 PersonaCreate

1. 运行 `PersonaCreate`
2. 成功后检查 `persona_result`

**预期结果**:
```
[PersonaCreate] 开始生成画像
...
SUCCESS
```

#### 7.4 再次运行 Main

如果画像创建成功，再运行一次 `Main`。

**预期结果**:
```
READY
```

#### 7.5 运行 SessionRunner

1. 运行 `SessionRunner`
2. 检查 `session_result`、`run_result`、`action_count`、`current_platform`

**预期结果**:
```
SUCCESS
```

#### 7.6 检查生成的画像和运行数据

运行后检查以下目录：

- `Persons\`：应有以你的 `device_id` 命名的画像 JSON
- `Memory\{device_id}\`：应出现会话记忆文件
- `Logs\`：应出现本次运行日志

> ✅ 当你已经能跑通 `Initializer -> Main -> PersonaCreate -> Main -> SessionRunner`，就说明新人最小闭环已经搭成。

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

> **这张图是 ZennoDroid 施工图。** 你要在项目里真正创建的是 `Initializer`、`Main`、`PersonaCreate`、`SessionRunner` 这几个 Own Code 动作块，以及对应的条件判断块。  
> `4.3` 和 `4.4` 里的图是**模块内部逻辑说明**，不是让你继续在 ZennoDroid 里拆更多动作块。

#### 4.2.1 首次最小闭环（直接照着搭）

```
开始
  │
  ▼
[Own Code] Initializer
来源: ZDProjects\Initializer_OwnCode.cs
  │
  ▼
条件 A: `{-Variable.initializer_result-}` 包含 `SUCCESS` ?
  ├─ 否 → 结束，查看 `initializer_result` 和日志
  └─ 是 → [Own Code] Main
           来源: ZDProjects\Main_OwnCode.cs
             │
             ▼
           条件 B: `{-Variable.main_result-}` 包含 `NEED_CREATE_PERSONA` ?
             ├─ 是 → [Own Code] PersonaCreate
             │        来源: ZDProjects\PersonaCreate_OwnCode.cs
             │          │
             │          ▼
             │        条件 C: `{-Variable.persona_result-}` 包含 `SUCCESS` ?
             │          ├─ 是 → 回到 Main
             │          └─ 否 → 结束，查看 `persona_result`
             │
             └─ 否 → 条件 D: `{-Variable.main_result-}` 包含 `READY` ?
                      ├─ 是 → [Own Code] SessionRunner
                      │        来源: ZDProjects\SessionRunner_OwnCode.cs
                      │          │
                      │          ▼
                      │        条件 E: `{-Variable.session_result-}` 包含 `SUCCESS` ?
                      │          ├─ 是 → 首次闭环完成
                      │          └─ 否 → 结束，查看 `session_result`
                      │
                      └─ 否 → 条件 F: `{-Variable.main_result-}` 包含 `NEED_DAILY_UPDATE` ?
                               ├─ 是 → 首次闭环先不要接 `DailyUpdate`，第二阶段再补
                               └─ 否 → 结束，查看 `main_result`
```

#### 4.2.2 每个分支的依据是什么

| 分支 | 判断变量 | 直接照抄的条件表达式 | 含义 | 第一次是否必须接 |
|------|----------|----------------------|------|------------------|
| 条件 A | `initializer_result` | `{-Variable.initializer_result-}` 包含 `SUCCESS` | 初始化通过，可以继续 | 是 |
| 条件 B | `main_result` | `{-Variable.main_result-}` 包含 `NEED_CREATE_PERSONA` | 当前没有画像，需要先创建 | 是 |
| 条件 C | `persona_result` | `{-Variable.persona_result-}` 包含 `SUCCESS` | 画像创建成功，回到 `Main` 重新判断 | 是 |
| 条件 D | `main_result` | `{-Variable.main_result-}` 包含 `READY` | 可以直接进入 `SessionRunner` | 是 |
| 条件 E | `session_result` | `{-Variable.session_result-}` 包含 `SUCCESS` | 首次会话执行成功 | 是 |
| 条件 F | `main_result` | `{-Variable.main_result-}` 包含 `NEED_DAILY_UPDATE` | 画像已存在，但需要先做每日更新 | 否，第二阶段再补 |

#### 4.2.3 在 ZennoDroid 里如何实现

1. 每个模块对应 **一个 Own Code 动作块**，不要把模块内部步骤拆成更多动作块。
2. 每个 `条件 A/B/C/D/E/F` 对应 **一个条件判断块**，条件文本直接复制上表里的表达式。
3. `PersonaCreate` 成功后，连线返回到 `Main`；这不是新建第二个 `Main`，而是让流程线回到已有的 `Main` 动作块。
4. 首次跑通时只需要 `Initializer -> Main -> PersonaCreate -> Main -> SessionRunner` 这一条最小闭环。
5. `DailyUpdate`、`StateSaver`、`Extension`、`ReportGen` 不要第一次就接进主流程，否则排错会变慢。

#### 4.2.4 这张图不包含什么

- 不包含 `SessionRunner` 内部的动作循环；那是模块内部逻辑，见 `4.3.3`
- 不包含 Reddit / Instagram 的具体 `operation steps`；那是统一引擎内部执行链，见 `4.4`
- 不包含 `StateSaver` 等完整生产链节点；那是第二阶段再补，不属于首次最小闭环

### 4.3 模块执行流程详解

> **以下图都是模块内部逻辑说明图。** 这些图帮助你理解模块内部做了什么，但**不要**把图里的每个小步骤继续拆成 ZennoDroid 动作块。  
> 在 ZennoDroid 外层流程里，你只需要按 `4.2` / `6.1` / `6.2` 创建少量 Own Code 动作块和条件判断块。

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

> **版本**: v4.6.1 (2026-03-14)
> **阅读时间**: 10 分钟
> **前提**: 你已经完成了第四步（创建变量）和第五步（创建 C# code 方块）

##### A. 概述

SessionRunner 是**一个 C# code 方块（C# code cube）**。你在 ZennoDroid 工作区里只需要放这一个方块，它内部会自动完成平台选择、动作循环、错误恢复和统计。所有逻辑都封装在 `ZDProjects\SessionRunner_OwnCode.cs` 这个文件的代码里，运行时由这个方块一次性执行完毕。

> **再说一遍**: SessionRunner 不是多个方块组成的流程图，别把下面的内部逻辑拆成新的方块。

---

##### B. 第一步：创建变量

打开项目变量窗口（Project Variables window）：

1. 菜单栏点击 **Window** > **Variables**，打开项目变量窗口
2. 点击窗口中的 **Custom** 标签
3. 点击左下方的 **Add** 按钮
4. 在弹出的输入框中，输入变量名（见下表）
5. 确认后，在变量列表中找到刚创建的变量
6. 在 **Default value** 列直接输入默认值
7. 在 **Note** 列输入备注说明
8. 重复第 3-7 步，直到所有变量创建完毕

**必须创建（2 个）**

| 变量名 | Default value（默认值） | Note（备注） |
|--------|------------------------|-------------|
| `project_root` | `C:\DPS_v4.5\` | 项目根目录，路径必须以 `\` 结尾 |
| `device_id` | `device_001` | 设备标识，必须与 Config\device_app_mapping.json 中的 key 一致 |

> `project_root` 的默认值改成你自己的实际路径。`device_id` 改成你自己的设备 ID。

**推荐创建（运行后自动写入，预创建方便在变量窗口直接观察）**

| 变量名 | Default value | Note |
|--------|--------------|------|
| `session_result` | （留空） | 会话最终结果: SUCCESS / ERROR |
| `run_result` | （留空） | 同 session_result |
| `action_count` | `0` | 动作循环次数（含成功/失败/跳过） |
| `current_platform` | （留空） | 运行时自动写入平台名称 |
| `current_page` | （留空） | 当前 APP 页面状态 |
| `current_action` | （留空） | 当前执行的动作名 |
| `action_result` | （留空） | 单次操作结果 |
| `session_successful_actions` | （留空） | 成功动作计数 |
| `session_failed_actions` | （留空） | 失败动作计数 |
| `session_skipped_actions` | （留空） | 跳过动作计数 |
| `session_success_rate` | （留空） | 成功率 (0.0000-1.0000) |
| `action_attempt_count` | （留空） | 总尝试次数 |

> **不创建推荐变量会怎样？** v4.5.12+ 有虚拟变量回退机制，不创建也不会报错。但你将无法在 ZennoDroid 的变量窗口直接看到这些值，排查问题会很麻烦。

---

##### C. 第二步：创建 C# code 方块

**操作步骤**:

1. 在 ZennoDroid 工作区的空白处**右键** > **Add action** > **Custom code** > **C# code**
2. 工作区会出现一个新的方块
3. **双击**这个方块，打开代码编辑器窗口
4. 用文本编辑器（记事本、VS Code 等）打开文件：`你的项目路径\ZDProjects\SessionRunner_OwnCode.cs`
5. 按 `Ctrl+A` 全选文件内容，然后 `Ctrl+C` 复制
6. 回到 ZennoDroid 的代码编辑器窗口，`Ctrl+A` 全选已有内容，然后 `Ctrl+V` 粘贴覆盖
7. 点击代码编辑器右下角的 **Save** 按钮保存

**给方块添加注释**（方便识别）:

1. 在工作区中**右键**刚创建的方块
2. 选择 **Comment**
3. 输入 `SessionRunner`
4. 确认

> **千万不要手动输入代码。** `SessionRunner_OwnCode.cs` 有 136 行，包含完整的模块加载器和编译器调用，手敲必然出错。必须完整复制粘贴。

---

##### D. 第三步：设置变量默认值

回到项目变量窗口（Window > Variables > Custom 标签）：

1. **project_root**: 找到这一行，在 Default value 列输入你的项目路径
   - 例如: `C:\DPS_v4.5\`
   - 路径**必须以 `\` 结尾**，否则模块拼接文件路径时会出错
   - 路径中**不要有中文和空格**

2. **device_id**: 找到这一行，在 Default value 列输入你的设备标识
   - 例如: `device_001`
   - 这个值必须和 `Config\device_app_mapping.json` 文件里的 key 完全一致

> 其他推荐变量的 Default value 留空就行，SessionRunner 运行时会自动写入。

---

##### E. 第四步：搭建完整流程

在 ZennoDroid 工作区中，按下面的结构搭建方块和连线。每个方块竖向排列，从上到下依次连接。

**流程施工图（照着搭，一个方块都别多、一个方块都别少）：**

```
   [C# code] Initializer
     注释: Initializer
     代码来源: ZDProjects\Initializer_OwnCode.cs
     |
     v
   [If] 检查 Initializer 结果
     条件: {-Variable.initializer_result-} 包含文本 SUCCESS
     |
     |-- 否 --> 结束（打开 Window > Log 查看日志排查问题）
     |
     |-- 是 -->
         |
         v
       [C# code] Main
         注释: Main
         代码来源: ZDProjects\Main_OwnCode.cs
         |
         v
       [If] 需要创建画像?
         条件: {-Variable.main_result-} 包含文本 NEED_CREATE_PERSONA
         |
         |-- 是 -->
         |     |
         |     v
         |   [C# code] PersonaCreate
         |     注释: PersonaCreate
         |     代码来源: ZDProjects\PersonaCreate_OwnCode.cs
         |     |
         |     v
         |   从 PersonaCreate 方块底部拖连线回到 Main 方块顶部
         |   （不是新建第二个 Main，是连回已有的那个 Main 方块）
         |
         |-- 否 -->
             |
             v
           [If] 准备好了?
             条件: {-Variable.main_result-} 包含文本 READY
             |
             |-- 否 --> 结束（打开 Window > Log 查看日志排查问题）
             |
             |-- 是 -->
                 |
                 v
               [C# code] SessionRunner
                 注释: SessionRunner
                 代码来源: ZDProjects\SessionRunner_OwnCode.cs
                 |
                 v
               [If] 会话成功?
                 条件: {-Variable.session_result-} 包含文本 SUCCESS
                 |
                 |-- 是 --> (可选) [C# code] StateSaver --> 结束
                 |
                 |-- 否 --> 结束（打开 Window > Log 查看日志排查问题）
```

**每个方块的创建方法：**

| 方块序号 | 方块类型 | 创建方式 | 注释名 | 代码来源文件 |
|---------|---------|---------|--------|-------------|
| 1 | C# code | 右键空白 > Add action > Custom code > C# code | `Initializer` | `ZDProjects\Initializer_OwnCode.cs` |
| 2 | If | 右键空白 > Add action > Logic > If | `检查Initializer` | 条件见下表 |
| 3 | C# code | 右键空白 > Add action > Custom code > C# code | `Main` | `ZDProjects\Main_OwnCode.cs` |
| 4 | If | 右键空白 > Add action > Logic > If | `需要创建画像` | 条件见下表 |
| 5 | C# code | 右键空白 > Add action > Custom code > C# code | `PersonaCreate` | `ZDProjects\PersonaCreate_OwnCode.cs` |
| 6 | If | 右键空白 > Add action > Logic > If | `准备好了` | 条件见下表 |
| 7 | C# code | 右键空白 > Add action > Custom code > C# code | `SessionRunner` | `ZDProjects\SessionRunner_OwnCode.cs` |
| 8 | If | 右键空白 > Add action > Logic > If | `会话成功` | 条件见下表 |

**条件判断方块 (If) 的条件设置：**

双击 If 方块打开属性，在条件表达式中设置：

| 方块序号 | 注释名 | 条件左侧值 | 条件类型 | 条件右侧值 |
|---------|--------|-----------|---------|-----------|
| 2 | 检查Initializer | `{-Variable.initializer_result-}` | Contains (包含) | `SUCCESS` |
| 4 | 需要创建画像 | `{-Variable.main_result-}` | Contains (包含) | `NEED_CREATE_PERSONA` |
| 6 | 准备好了 | `{-Variable.main_result-}` | Contains (包含) | `READY` |
| 8 | 会话成功 | `{-Variable.session_result-}` | Contains (包含) | `SUCCESS` |

**连线方法：**

从一个方块底部的连接点，按住鼠标拖拽到下一个方块顶部的连接点。If 方块有两个输出：左边是"是"（条件成立），右边是"否"（条件不成立）。

> **关于 PersonaCreate 回连 Main**: PersonaCreate 执行完后，要从它的底部连接点拖线回到已有的 Main 方块顶部。这样画像创建成功后会重新走 Main 的判断逻辑，这次 Main 会返回 `READY`，流程就会走到 SessionRunner。

---

##### F. 第五步：首次运行验证

**运行方法：**

1. 点击 ZennoDroid 工作区顶部的**绿色三角 Play 按钮**（或按 **F5**）
2. 等待运行完成

**查看日志：**

1. 菜单 **Window** > **Log**，打开日志窗口
2. 日志会实时显示每个模块的执行信息

**成功标志（在日志中搜索这些关键词）：**

| 阶段 | 日志中出现的文本 | 说明 |
|------|-----------------|------|
| Initializer 通过 | `[Initializer] 初始化完成` | 环境检查通过 |
| 平台确认 | `[SessionRunner] 选择平台:` 后跟平台名 | 设备到平台映射成功 |
| 动作执行中 | `执行动作:` 多次出现 | 正在循环执行社交操作 |
| 统一引擎工作 | `统一引擎执行:` | JSON 操作步骤正在执行 |
| 会话结束 | `会话结束` + 统计数据 | 整个会话完成 |
| 最终判定 | `门控判定: PASS` | 成功率达标 |

**运行完毕后检查变量窗口：**

打开 Window > Variables > Custom 标签，查看以下变量的值：

| 变量名 | 预期值 | 如果不对... |
|--------|--------|-----------|
| `session_result` | `SUCCESS` | 看日志中的 ERROR 消息 |
| `current_platform` | `reddit` / `instagram` 等 | 检查 device_app_mapping.json |
| `action_count` | 大于 0 的数字 | 看日志中是否有 SKIP/ERROR 大量出现 |

---

##### G. SessionRunner 内部做了什么（仅供理解，不要拆成新方块）

下面的图说明的是 SessionRunner 这**一个 C# code 方块内部**的执行逻辑。看完有个概念就行，你不需要也**不应该**把这些步骤拆成新的方块。

```
SessionRunner 内部 9 个阶段（全部在一个方块里自动执行）

[1] 设备连接检测 ...... 检查手机/模拟器是否连通
        |
        v
[2] 读取基础变量 ...... 读取 project_root, device_id 等
        |
        v
[3] 初始化编排器 ...... 加载恢复预算配置
        |
        v
[4] 确定平台 .......... 读 device_app_mapping.json，写入 current_platform
        |
        v
[5] 加载平台配置 ...... 加载选择器、操作步骤、意图映射
        |
        v
[6] 初始化子系统 ...... 启动 MemoryManager、VisionCorrector、RuleEngine
        |
        v
[7] 页面检测与导航 .... 检测 APP 当前页面，不在首页则自动导航回去
        |
        v
[8] 主动作循环 ........ 在会话时间窗内循环：选动作 > 执行 > 记录 > 暂停
        |
        v
[9] 会话结束 .......... 计算成功率，判定是否达标，设置输出变量
```

**第 [8] 阶段每一轮循环的细节：**

1. 疲劳调权: 能量不足时自动禁用高消耗动作
2. 加权随机选择动作: browse(50%) / read_post / like(15%) / comment(4%) / post 等
3. MemoryManager 去重: 已交互过的帖子自动降级为 browse
4. RuleEngine 评估: 不符合规则的帖子降级为 browse
5. 统一引擎执行: intent > operation > steps，含分级恢复（Retry > LocalRecovery > VisionAssist > Fallback > Abort）
6. 统计与恢复: 连续 3 次 SKIP 触发 ForceNavigateToFeed
7. 记录 Memory、疲劳衰减、随机暂停

> 以上全部在代码里自动发生，你什么都不用做。

---

##### H. 输出变量速查表

SessionRunner 运行后会自动写入以下变量（前提是你在第一步中预创建了它们）：

| 变量名 | 含义 | 示例值 |
|--------|------|--------|
| `session_result` | 会话最终结果 | `SUCCESS` 或 `ERROR: 会话成功率不达标` |
| `run_result` | 同 session_result | `SUCCESS` |
| `action_count` | 动作循环总次数 | `12` |
| `action_attempt_count` | 总尝试次数（含重试） | `15` |
| `session_successful_actions` | 成功动作数 | `10` |
| `session_failed_actions` | 失败动作数 | `1` |
| `session_skipped_actions` | 跳过动作数 | `1` |
| `session_success_rate` | 成功率 | `0.9091` |
| `current_platform` | 当前平台名 | `reddit` |
| `current_page` | 最后一次操作后的页面状态 | `feed` |
| `current_action` | 最后执行的动作 | `browse` |
| `action_result` | 最后一次操作的结果 | `SUCCESS` |

---

##### I. 配置文件速查表

| 文件路径 | 必需程度 | 说明 | 缺少会怎样 |
|----------|---------|------|-----------|
| `Config\device_app_mapping.json` | **必需** | 设备 ID 到平台的映射 | `ERROR: 平台配置不存在` |
| `Config\PlatformsConfig.json` | **必需** | 平台 UI 选择器与页面签名 | `ERROR: 平台配置不存在` |
| `Config\Operations\{platform}_operations.json` | **必需** | 平台操作步骤 JSON 定义 | `ERROR: 操作配置缺失` |
| `Config\IntentMappings\{platform}_intents.json` | **必需** | 意图到操作序列的映射 | 所有动作回退为 browse |
| `Config\BehaviorConfig.json` | 推荐 | 动作权重、会话时长、恢复预算 | 使用内置默认值，不影响运行 |
| `Config\StageConfig.json` | 推荐 | 画像阶段配置 | 使用内置默认值 |
| `Config\DecisionConfig.json` | 推荐 | RuleEngine 帖子评估规则 | 跳过规则评估，所有帖子都可交互 |
| `Config\AIConfig.json` | 推荐 | AI 服务配置 | AI 评论生成和 Vision 验证不可用 |

---

##### J. 常见问题排查表

| 错误消息 / 现象 | 原因 | 解决方法 |
|-----------------|------|---------|
| `ERROR: project_root 未设置` | 变量窗口中 project_root 为空 | Window > Variables > Custom，找到 project_root，填入项目路径（以 `\` 结尾） |
| `ERROR: 设备未连接` | ZennoDroid 没有连上手机或模拟器 | 检查 USB 连接或模拟器是否启动，确认 ZD 设备列表中能看到设备 |
| `ERROR: 平台配置不存在` | device_app_mapping.json 中的 device_id 找不到对应平台，或 PlatformsConfig.json 中缺少该平台 | 检查 device_id 变量值是否和 device_app_mapping.json 中的 key 完全一致（区分大小写） |
| `ERROR: 操作配置缺失` | 缺少 `Config\Operations\{platform}_operations.json` | 确认 Operations 目录下有对应平台的 JSON 文件，比如 `reddit_operations.json` |
| `ERROR: 会话成功率不达标` | 成功率低于 95% 或成功动作数少于 6 | 检查 APP 是否正常打开，UI 选择器是否与当前 APP 版本匹配 |
| `[SessionRunner] 模块不存在:` | `Modules\SessionRunner.cs` 文件缺失 | 确认 `Modules\` 目录下有 `SessionRunner.cs` 文件 |
| `[SessionRunner] 编译错误 行X:` | 模块代码语法错误或依赖文件缺失 | 检查 `Modules\Core\` 目录下的 .cs 文件是否完整（需要 21 个核心文件） |
| 日志中大量 `[ORCHESTRATOR] Retry` | 操作频繁失败，自动重试 | UI 选择器可能过期，打开 PlatformsConfig.json 检查 selectors 是否与当前 APP 版本匹配 |
| `[ORCHESTRATOR] Abort` | 所有恢复预算耗尽，放弃该操作 | 检查 APP 当前状态，可能需要更新 PlatformsConfig.json 的页面签名 |
| 变量窗口中 session_result 始终为空 | C# code 方块的代码没有正确粘贴 | 双击 SessionRunner 方块，确认代码编辑器里有完整内容（136行），不是空的 |

##### BehaviorConfig.json 中 SmartOrchestrator 配置（可选调优）

如果你想调整恢复预算，在 `Config\BehaviorConfig.json` 中添加或修改 `smart_orchestrator` 节：

```json
{
    "smart_orchestrator": {
        "max_retries": 2,
        "max_local_recoveries": 2,
        "max_vision_assists": 3,
        "max_fallback_attempts": 1
    },
    "session_gate": {
        "min_success_rate": 0.95,
        "min_successful_actions": 6
    }
}
```

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `max_retries` | 2 | 简单重试最大次数 |
| `max_local_recoveries` | 2 | 回退安全页后重试最大次数 |
| `max_vision_assists` | 3 | AI 视觉验证最大次数（需要 AIConfig.json） |
| `max_fallback_attempts` | 1 | 备用操作序列尝试次数 |
| `min_success_rate` | 0.95 | 会话成功率门槛 |
| `min_successful_actions` | 6 | 最少成功动作数 |

> 不配置时全部使用默认值，不影响正常运行。新手不需要改这些。

### 4.4 平台操作主链（执行代码 + operation JSON）

> **这不是“在 ZennoDroid 里再新建 Reddit/Instagram 模块动作块”的施工图。**  
> 当前标准主链是：`SessionRunner` 选择动作 → `IntentMappings` 映射意图 → `Operations` 定义步骤 → `ActionExecutor` 执行步骤。  
> 对新人来说，你在 ZennoDroid 外层仍然只需要一个 `SessionRunner` 动作块。

#### 4.4.1 Reddit 如何执行

```
SessionRunner 选中动作
  │
  ├─ browse
  ├─ read_post
  ├─ like
  └─ comment
  │
  ▼
Config/IntentMappings/reddit_intents.json
  │
  ├─ browse      -> browse_feed
  ├─ read_post   -> read_post
  ├─ like        -> like_content
  └─ comment     -> reply_post
  │
  ▼
得到 operation 序列
  │
  ├─ browse_feed -> browse
  ├─ read_post   -> open_post -> read_post -> back_to_feed
  ├─ like_content -> like
  └─ reply_post  -> open_post -> comment -> back_to_feed
  │
  ▼
ActionExecutor
  │
  ├─ 读取 Config/Operations/reddit_operations.json
  ├─ 执行 steps[]（find / tap / type / verify / back ...）
  └─ 配合 PlatformsConfig.json 中的 selectors / page_signatures
  │
  ▼
返回 SUCCESS / SKIP / ERROR
```

> 重点：Reddit 当前主链不是“ZennoDroid 外层一个 Browse 块、一个 Like 块、一个 Comment 块”这种搭法，而是**外层一个 `SessionRunner`，内部再走 JSON 步骤链**。

#### 4.4.2 Instagram 如何执行

```
SessionRunner 选中动作
  │
  ├─ browse
  ├─ read_post
  ├─ like
  └─ comment
  │
  ▼
Config/IntentMappings/instagram_intents.json
  │
  ├─ browse      -> browse_feed
  ├─ read_post   -> read_post
  ├─ like        -> like_content
  └─ comment     -> reply_post
  │
  ▼
得到 operation 序列
  │
  ├─ browse_feed -> browse
  ├─ read_post   -> open_post -> view_post -> back_to_feed
  ├─ like_content -> like
  └─ reply_post  -> open_post -> comment -> back_to_feed
  │
  ▼
ActionExecutor
  │
  ├─ 读取 Config/Operations/instagram_operations.json
  ├─ 执行 steps[]（包含 if_exists / call_operation / double_tap_like）
  └─ 配合 PlatformsConfig.json 中的 selectors / page_signatures
  │
  ▼
返回 SUCCESS / SKIP / ERROR
```

> 注意：Instagram 的 `rate_limits` 目前主要定义在 `PlatformsConfig.json` 中，但新人施工时**不要**把文档理解成“在 ZennoDroid 外层流程里额外再接一个限流动作块”。当前标准入口仍然只有 `SessionRunner`。

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
| SessionRunner | `SUCCESS` | `ERROR` |

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

1. 先查看 `Docs/TechManual_技术手册.md` Part 8 的术语表
2. 再查看上面的故障排除决策树
3. 检查 `Logs/` 目录下的错误日志
4. 查看根目录 `CHANGELOG.md` 了解近期修复

---

## 六、变量快速参考表

### 必须设置的变量（2个）

| 变量名 | 示例值 | 说明 |
|--------|--------|------|
| `project_root` | `C:\DPS_v4.5\` | 必须以 `\` 结尾 |
| `device_id` | `R58M4816G8Y` | 你的设备 ID，必须与 `Config\device_app_mapping.json` 对应 |

### 建议预创建的主链结果变量

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
| `run_result` | String | （留空） |
| `last_error` | String | （留空） |

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

### 推荐的会话状态变量

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `action_count` | String | `0` |
| `current_action` | String | （留空） |
| `current_intent` | String | （留空） |
| `current_page` | String | （留空） |
| `current_post_id` | String | （留空） |
| `current_post_json` | String | （留空） |
| `current_platform` | String | （留空，运行时写入） |
| `comment_text` | String | （留空） |
| `ai_comment_text` | String | （留空） |
| `effective_intent` | String | （留空） |

### 推荐的诊断变量

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `init_status` | String | （留空） |
| `config_ok` | String | （留空） |
| `module_ok` | String | （留空） |
| `config_missing` | String | （留空） |
| `vision_verified` | String | （留空） |
| `pending_action` | String | （留空） |
| `pending_action_type` | String | （留空） |
| `pending_platform` | String | （留空） |

### 其他变量

全部创建为 `String` 类型，初始值留空即可。系统运行时会自动填充。  
如果没有预创建某些输出变量，当前代码会静默忽略写入，不会阻止主链运行，但会减少可观察性。

> ✅ **建议**: 新人先创建“必填 + 主链结果 + 会话状态”三组变量，足够完成首次搭建与排错。

---

## 6.1 v4.7 ZD 微流程变量 (新增)

v4.7 引入 ZD 外层流程编排后，需要额外创建以下变量。全部为 String 类型。

#### Step DSL 核心变量

| 变量名 | 初始值 | 写入者 | 用途 |
|--------|--------|--------|------|
| `zd_step_plan` | (留空) | InitSession / 外部 | 完整 DSL 字符串, 例如 `L:post_unit\|W:3\|S:down_900\|W:2` |
| `zd_step_index` | `0` | InitSession / Advancer | 当前步骤索引 (0-based) |
| `zd_step_count` | `0` | DecideNextAction | 总步骤数 |
| `zd_step_type` | (留空) | DecideNextAction | Switch 路由键 |
| `zd_step_param` | (留空) | DecideNextAction | 当前步骤参数 |

#### Touch / Locate 变量

| 变量名 | 初始值 | 用途 |
|--------|--------|------|
| `zd_selector_key` | (留空) | Locate 输入 selector |
| `zd_tap_x1` | `0` | Touch X from |
| `zd_tap_x2` | `0` | Touch X to |
| `zd_tap_y1` | `0` | Touch Y from |
| `zd_tap_y2` | `0` | Touch Y to |
| `zd_found` | `false` | Locate 是否成功 |

#### Swipe / Wait 变量

| 变量名 | 初始值 | 用途 |
|--------|--------|------|
| `zd_swipe_x1` / `y1` / `x2` / `y2` | `0` | Swipe 四坐标 |
| `zd_swipe_duration` | `800` | Swipe 持续时间 (**毫秒**) |
| `zd_wait_sec` | `0` | Pause 等待 (**秒**) |

#### 安全与屏幕变量

| 变量名 | 初始值 | 用途 |
|--------|--------|------|
| `zd_safety` | `0` | 防无限循环计数器 |
| `zd_screen_width` | `0` | 屏幕宽度 (Swipe 展开用) |
| `zd_screen_height` | `0` | 屏幕高度 |

#### 回收与兼容变量

| 变量名 | 初始值 | 用途 |
|--------|--------|------|
| `zd_action_result` | (留空) | 原生块执行结果回收 |
| `zd_action_duration` | `0` | 动作耗时 |
| `zd_action_error_detail` | (留空) | 错误详情 |
| `zd_verify_rule` | (留空) | Verify 输入 |
| `sr_use_legacy_run` | (留空) | 设为 `"true"` 走旧 Run() 兼容路径 |

#### DSL 配置契约速查

**分隔符**: 唯一合法分隔符为 ASCII `|`，严禁 Unicode 箭头。

**Switch Case 值域**: `L`, `T`, `T:long`, `S`, `B`, `W`, `V`, `DONE`, `Default`

**原生块关键约束**:

| 约束项 | 规则 | 参考 |
|--------|------|------|
| Touch 坐标 | 四坐标矩形 (x1/x2/y1/y2)，不是单点 | AE-1 |
| T vs T:long | 独立块，Long Tap 是 design-time 复选框 | AE-2 |
| Keyboard Back | `{AndroidKeys.BACK}` + Delay 必须启用 | AE-3 |
| Pause / Swipe | Pause 用秒, Swipe Duration 用毫秒 | AE-4 |
| Curved Swipe | 启用 Curved 则必须填 Bending 值 | AE-5 |
| Switch Default | 必须连线，不可悬空 | AE-6 |
| 红色箭头 | 全部块的红色出口必须连线 | AE-7 |

> **详细施工步骤**: 参见 [TechManual_技术手册.md 3.17 施工路径](TechManual_技术手册.md#317-v47-zd-微流程施工路径说明)
> **OwnCode 文件**: 参见 [ZDProjects/README.md](../ZDProjects/README.md#文件列表)

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
**A**: v4.5.6 引入的新架构，DPS 不再直接在高层逻辑里写死物理点击，而是通过“意图（intent）→ 操作（operation）→ steps”的链路执行。详见 `Docs/TechManual_技术手册.md` Part 1 与 Part 3。

### Q: 如何测试配置是否正确？
**A**: 使用 `ZDProjects/Tests/` 下的 E2E 测试脚本，或使用 Playwright Android 测试（`playwright_dps_test.js`）进行快速验证。

### Q: 为什么 `action_count` 变小了？
**A**: 当前主链里，`action_count` 表示动作循环次数，不只统计成功动作。判断会话成功时，`SKIP` 不算失败，但仍会计入动作循环。

---

**下一步**: 查看 `Docs/TechManual_技术手册.md` Part 8 了解专业术语
