# DPS v4.5 项目上下文

## 1. 项目概述

DPS v4.5 (Dynamic Persona Simulation) 是基于 ZennoDroid 的**通用移动自动化框架**。系统采用 **DPS (大脑) + ZennoDroid (手)** 分层架构，通过 **Intent Translator** 将高层意图翻译为物理命令，实现跨平台 APP 自动化。

**核心理念**：每个账号拥有独立的"人格画像"（Persona），通过 **Intent-Based Execution** 模型驱动行为决策。DPS 负责感知、记忆、决策，ZennoDroid 负责元素定位、拟人化执行、异常处理。

---

## 2. 技术栈

| 层级 | 技术栈 |
|------|------------|
| 语言 | C# 5.0 (Modules/) + C# 7.0+ (ZDProjects/) |
| 运行时 | .NET Framework，ZennoDroid 7.x+ 嵌入式 |
| 平台 | ZennoDroid on Windows + Playwright (测试/验证) |
| 编译 | CSharpCodeProvider 动态编译 + LRU 缓存（32 条目） |
| 可用引用 | System.dll, System.Core.dll, System.Data.dll, System.Xml.dll, Microsoft.CSharp.dll |
| JSON | 手写 JsonHelper.cs（993 行，23 方法），无第三方依赖 |
| AI | 3 级 fallback：Gemini → Claude → GPT |
| 存储 | 纯文件系统（JSON 文件），无数据库 |
| 测试 | ZennoDroid E2E + Playwright Android (ADB/WebView) |

---

## 3. 完整目录结构

```
DPS_v4.5/
├── .omo.conf                    # 项目约束配置（轻量级）
├── .omo/                        # .omo 2.0 完整状态管理
├── Config/                      # 14 个 JSON 配置文件
│   ├── AIConfig.json            # AI 模型配置 — 3 级 fallback，含真实 API Key
│   ├── Apps.json                # 应用包名注册表
│   ├── BehaviorConfig.json      # 行为参数 — 动作权重、打字速度
│   ├── DecisionConfig.json      # 决策引擎规则 — 帖子评估阈值
│   ├── EvolutionRules.json      # 画像演化规则
│   ├── ExtensionsConfig.json    # 扩展全局配置
│   ├── ExtensionsRegistry.json  # 扩展注册表
│   ├── MaintenanceConfig.json   # 维护任务配置
│   ├── Operations/              # ActionExecutor 操作步骤定义
│   │   ├── reddit_operations.json
│   │   ├── instagram_operations.json
│   │   └── babycenter_operations.json  # v4.5.7 新增
│   ├── IntentMappings/          # Intent 映射配置
│   │   ├── reddit_intents.json
│   │   ├── instagram_intents.json
│   │   └── babycenter_intents.json     # v4.5.7 新增
│   ├── PersonaPrompt.txt        # AI 画像生成 Prompt 模板
│   ├── PlatformsConfig.json     # 核心！平台配置 — ui_selectors, rate_limits
│   ├── StageConfig.json         # 7 阶段生命周期配置
│   ├── ValidationRules.json     # 数据验证规则
│   └── device_app_mapping.json  # 设备→平台映射
├── Core/                        # ZD 脚本层引擎（4 个文件）
│   ├── ScriptHelpers.cs         # ZD 公共函数库（GetVar/SetVar/Humanization）
│   ├── HumanizationEngine.cs    # 人性化行为引擎 — 4 种配置
│   ├── UILocator.cs             # 多策略 UI 元素定位
│   └── ErrorRecovery.cs         # 错误恢复 — 指数退避重试
├── Modules/                     # 业务逻辑模块（14 个 .cs）
│   ├── Main.cs                  # 主入口 — 检查画像状态，分发流程
│   ├── Initializer.cs           # 初始化 — 创建目录、验证配置
│   ├── SessionRunner.cs         # 核心！会话执行引擎（853 行）
│   ├── PersonaCreate.cs         # AI 画像生成
│   ├── DailyUpdate.cs           # 每日更新 — 年龄/孕周/季节
│   ├── WeeklyEvolve.cs          # 每周 AI 进化 — 分析行为调整画像
│   ├── MemoryManager.cs         # 交互记忆管理（738 行，24 方法）
│   ├── RuleEngine.cs            # 规则引擎 — 帖子评估决策
│   ├── StateSaver.cs            # 状态持久化
│   ├── ReportGen.cs             # 报告生成
│   ├── Maintenance.cs           # 维护任务 — 清理过期数据
│   ├── Extension.cs             # 扩展入口
│   ├── UIHelper.cs              # UI 辅助 — Android XML 解析
│   └── Core/                    # 核心工具（14 个文件）
│       ├── JsonHelper.cs        # 核心！JSON 解析器（993 行）
│       ├── AIService.cs         # AI API 调用 — 3 级 fallback
│       ├── ActionExecutor.cs    # 统一操作执行器
│       ├── CoreHelper.cs        # 日志/变量/文件工具
│       ├── FileHelper.cs        # 文件操作 — 原子写入
│       ├── ExtensionManager.cs  # 扩展管理器
│       ├── IExtension.cs        # 扩展接口定义
│       ├── PageDetector.cs      # 页面状态检测
│       ├── SelectorEngine.cs    # UI 选择器引擎
│       ├── Intent.cs            # v4.5.6 新增：高层操作意图抽象（168 行）
│       ├── ZDCommand.cs         # v4.5.6 新增：ZennoDroid 物理命令（333 行）
│       ├── ZDResult.cs          # v4.5.6 新增：统一执行结果（311 行）
│       ├── ZennoDroidAdapter.cs # v4.5.6 新增：API 适配器（408 行）
│       └── IntentTranslator.cs  # v4.5.6 新增：意图翻译器（472 行）
├── Extensions/                  # 可插拔扩展
│   ├── DataSources/
│   │   ├── IPLocationExtension.cs
│   │   └── WeatherExtension.cs
│   └── Hooks/                   # 待实现（目录为空）
├── ZDProjects/                  # ZD Own Code 入口脚本
│   ├── ModuleLoader.cs          # 通用模块加载器（273 行）
│   ├── *_OwnCode.cs             # 10 个模块入口脚本
│   ├── Reddit_*.cs              # 4 个 Reddit 操作脚本
│   └── Tests/                   # E2E 测试脚本
│       ├── Reddit_E2E_Test.cs   # Reddit 完整测试 (v4.5.7)
│       ├── Instagram_E2E_Test.cs # Instagram 完整测试 (v4.5.7)
│       ├── BabyCenter_E2E_Test.cs # BabyCenter 完整测试 (v4.5.7)
│       └── playwright_dps_test.js # Playwright Android 测试 (v4.5.7)
├── Tools/                       # 独立工具 (v4.5.10 新增)
│   └── app_onboarder/           # 新平台自动接入工具 (Python CLI)
│       ├── main.py              # CLI 入口（交互/命令行模式）
│       ├── adb_controller.py    # ADB 命令封装
│       ├── ui_analyzer.py       # UI Dump XML 解析引擎
│       ├── app_explorer.py      # 5 阶段自主探索引擎
│       ├── config_generator.py  # 配置/操作/测试脚本生成器
│       └── test_runner.py       # E2E 测试运行 + 自动修复
├── Persons/                     # 画像存储（运行时生成）
├── Memory/                      # 行为记忆（运行时生成）
├── Logs/                        # 日志（运行时生成）
├── Reports/                     # 报告（运行时生成）
├── Docs/                        # 项目文档
│   ├── README.md
│   ├── GETTING_STARTED.md
│   ├── MultiPlatformFramework.md
│   ├── UnifiedIntentArchitecture.md # Intent 系统说明
│   ├── PLAYWRIGHT_ANDROID_ANALYSIS.md # Playwright Android 测试分析 (v4.5.7)
│   └── FIX_REPORT_2026-02-27.md # CODEX 修复报告 (v4.5.7)
├── CHANGELOG.md                 # 版本历史
└── run.droid                    # ZennoDroid 项目文件
```

---

## 4. 执行流程

```
ZD 启动
  │
  ▼
Initializer
  创建运行时目录（Persons/Memory/Logs/Reports）
  验证 Config/ 下所有 JSON 配置文件
  │
  ▼
Main
  读取画像状态，分发到对应子流程
  ├─ NEED_CREATE_PERSONA → PersonaCreate
  │     调用 AIService（Gemini → Claude → GPT fallback）
  │     生成画像 JSON，写入 Persons/{id}.json
  │
  ├─ NEED_DAILY_UPDATE → DailyUpdate
  │     更新年龄、孕周、季节字段
  │     触发阶段切换检测
  │
  └─ READY → 继续主流程
  │
  ▼
Extension
  IPLocationExtension（获取设备 IP 地理位置）
  WeatherExtension（获取当地天气，影响行为权重）
  │
  ▼
SessionRunner（核心，853 行）
  加权随机选择动作序列
  疲劳模型（连续操作后降低活跃度）
  调用 RuleEngine 评估每个帖子
  调用 MemoryManager 记录交互
  通过 ActionExecutor 执行具体 UI 操作
  │
  ▼
StateSaver
  持久化画像状态（Persons/{id}.json）
  更新统计数据（今日操作数、互动率等）
  │
  ▼
Maintenance
  清理 Memory/ 中超过保留期的记忆条目
  压缩过期日志
  │
  ▼
ReportGen
  仅在 17:00 后执行
  生成当日行为报告，写入 Reports/
```

---

## 5. 7 阶段生命周期

| 阶段码 | 名称 | 触发条件 | sessions/天 | 分钟/次 |
|--------|------|----------|:-----------:|:-------:|
| TTC | 备孕期 | 未怀孕，无孩子 | 6 | 12 |
| T1 | 孕早期 | 孕 1-13 周 | 7 | 10 |
| T2 | 孕中期 | 孕 14-27 周 | 7 | 12 |
| T3 | 孕晚期 | 孕 28+ 周 | 6 | 11 |
| PP0 | 产后早期 | 孩子 0-3 月 | 10 | 7 |
| PP1 | 产后中期 | 孩子 4-12 月 | 8 | 9 |
| NP | 育儿期 | 孩子 13+ 月 | 6 | 12 |

阶段配置存储在 `Config/StageConfig.json`，由 `DailyUpdate.cs` 在每日更新时检测并切换。

---

## 6. 关键模块说明

### SessionRunner.cs（~1490 行）
会话执行的核心。负责：
- 通过 `device_app_mapping.json` 选择平台，加载 `PlatformsConfig.json` 和 `operations.json`
- 加载 `IntentMappings` 意图映射（可选，缺失时回退到硬编码默认映射）
- 从 BehaviorConfig 读取动作权重，加权随机选择下一个动作
- 通过 `ExecuteWithUnifiedEngine` → `ActionExecutor.Execute` 执行具体 UI 操作
- 疲劳模型：连续执行同类动作后，该动作权重临时降低
- 时间感知：根据当前时间（早/午/晚）调整行为模式
- 调用 HumanizationEngine 在每次操作前后插入随机延迟

### JsonHelper.cs（993 行，23 方法）
项目唯一的 JSON 处理工具，无第三方依赖。关键方法：
- `GetJsonValue(json, key)` — 读取顶层字段
- `GetNestedValue(json, path)` — 读取嵌套路径（用点分隔）
- `SetJsonValue(json, key, value)` — 修改字段并返回新 JSON 字符串
- `ParseJsonArray(json, key)` — 解析数组为 `List<string>`

### SelectorEngine.cs
专门解析 `PlatformsConfig.json` 中的 `ui_selectors` 嵌套对象。不能用 `JsonHelper.GetJsonValue` 直接读取，必须通过 `SelectorEngine.GetSelector(platform, action, element)` 获取选择器字符串。

### MemoryManager.cs（738 行，24 方法）
管理账号的交互记忆：
- 记录已互动过的帖子/用户，避免重复操作
- 按时间衰减记忆权重
- 提供"相似内容偏好"计算，影响 RuleEngine 评分

### AIService.cs
封装 3 级 AI fallback：
1. Gemini（主力）
2. Claude（备用）
3. GPT（最终兜底）

每级失败后自动切换，全部失败则返回预设默认值，不中断流程。

---

## 7. 已知技术债

- `Modules/Decision/`、`Modules/Persona/`、`Modules/Memory/`、`Modules/Report/` 子目录仅有 README 占位，功能尚未迁移
- `Extensions/Hooks/` 目录为空，钩子机制未实现
- 行为配置名不一致：代码中使用 `speed_demon / casual / deep_reader / distracted`，部分文档写的是 `active / lurker / new_user` (待统一)
- TikTok 和 Facebook 的平台配置已存在于 PlatformsConfig.json，但对应 Module 未实现，未启用
- **Playwright Android 支持属于实验性质**，部分高级功能（原生 APP 完整控制）有限制

---

## 8. 重要注意事项

**API Key 安全**
`Config/AIConfig.json` 包含真实的 Gemini/Claude/GPT API Key，已在 `.gitignore` 中排除，不得提交到版本控制。

**ZD Own Code 同步**
`ZDProjects/*_OwnCode.cs` 修改后必须手动复制到 ZennoDroid 项目对应的 Own Code 动作块中，ZD 不会自动读取文件系统上的变更。

**其他模块自动重编译**
`Modules/` 下的 `.cs` 文件由 `ModuleLoader.cs` 在运行时动态编译。ModuleLoader 检测文件时间戳，有变更时自动重新编译，无需手动操作。

**ui_selectors 解析规则**
`PlatformsConfig.json` 中的 `ui_selectors` 是多层嵌套对象，必须通过 `SelectorEngine.GetSelector()` 读取，直接调用 `JsonHelper.GetJsonValue` 只能拿到原始 JSON 字符串，无法正确解析。

**C# 版本限制**
`Modules/` 下的代码必须兼容 C# 5.0（CSharpCodeProvider 默认版本）。禁止使用 C# 6.0+ 语法（字符串插值 `$""`、null 条件运算符 `?.`、nameof 等）。`ZDProjects/` 层可以使用 C# 7.0+。

---

## 9. Universal Framework 架构详解 (v4.5.6+)

### Intent-Based Execution 模型

DPS 不再直接调用 ZennoDroid API，而是通过 Intent 抽象层：

```
高层意图 (Intent)          翻译层 (IntentTranslator)     物理命令 (ZDCommand)
"like_content"       →     解析选择器 + 回退链     →     Tap(x, y) + Swipe(...)
"follow_entity"      →     查找用户元素               →     Tap(follow_button)
"share_content"      →     分享菜单导航               →     菜单操作序列
```

### 新增核心模块

- **Intent.cs**: 高层操作意图抽象类（168 行）
- **ZDCommand.cs**: ZennoDroid 物理命令类（333 行）
- **ZDResult.cs**: 统一执行结果类（311 行）
- **ZennoDroidAdapter.cs**: ZennoDroid API 适配器（408 行）
- **IntentTranslator.cs**: 意图翻译器（472 行）

### Manifest v2.0 格式

新的配置格式，分离语义和物理参数：
- **capabilities**: APP 能做什么（11 种操作）
- **states**: 如何识别当前状态（visual_markers + ui_signatures + gemini_prompt）
- **intent_mappings**: 意图翻译为操作（含 fallback_intents）
- **rate_limits**: 速率限制（per_hour + cooldown_seconds + per_day）

---

## 10. 测试策略 (v4.5.8+)

**混合测试方案**：
- **ZennoDroid E2E**: 完整工作流测试，覆盖 browse → read → like → comment 链路
- **Playwright Android**: 通过 ADB/WebView 进行配置验证和基础交互测试
- **优势互补**: Playwright 快速验证，ZennoDroid 深度 E2E

---

## 11. 版本历史速查

| 版本 | 日期 | 核心变更 |
|------|------|-----------|
| v4.5.10 | 2026-03-04 | App Onboarder 工具：Python CLI 自动探索 APP、生成配置/操作/E2E 测试，支持 WebView 和双 Feed 模式 |
| v4.5.9 | 2026-02-28 | 代码重构优化：SessionRunner 逻辑封装、.omo 2.0 模块追踪、I/O 错误处理增强 |
| v4.5.8 | 2026-02-27 | 复扫修复：会话成功门控 >=95% + 最小成功动作数、语义链路补齐、I/O 容错增强、SKIP 状态统一 |
| v4.5.7 | 2026-02-27 | CODEX 修复：ActionExecutor 稳定性、SessionRunner 评估门控、意图映射修复、BabyCenter 接入 |
| v4.5.6 | 2026-02-27 | Universal Framework：Intent/ZDCommand/ZDResult/IntentTranslator 架构完整重构 |
| v4.5.5 | 2026-02-27 | 编译错误修复：ActionExecutor 重复方法删除、API 调用修正 |
| v4.5.4 | 2026-02-27 | Universal APP Automation Framework：NavigationResolver、VisionCorrector、AppExplorer、RateLimiter |
| v4.5.3 | 2026-02-26 | ActionExecutor call_operation 原语实现 |
| v4.5.2 | 2026-02-17 | P0 级 Bug 修复：ModuleLoader 缓存优化、SessionRunner 并发安全、MemoryManager 文件锁 |

---

## 12. 新会话快速恢复指南

**目的**: 让新 AI 会话快速了解项目状态，无需从头阅读所有文档。

### 快速入口

1. **项目上下文**: 本文件 `.omo/context.md` - 详细技术架构和目录结构
2. **AI 会话指南**: `.omo/AI_SESSION_GUIDE_AI会话指南.md` - AI 会话恢复指引
3. **技术手册**: `Docs/TechManual_技术手册.md` - 完整技术文档
4. **代码规范**: `.omo/conventions_代码规范.md` - C# 语法约束和编码规范

### 恢复工作流程

```bash
# 1. 打开项目
cd /Users/hofishvn/openCode_Projects/DPS_v4.5

# 2. 向 AI 说明恢复意图
# "我想继续优化 BabyCenter 模块"

# 3. AI 将自动读取:
#    - .omo/context.md (完整上下文)
#    - .omo/conventions_代码规范.md (代码规范)
#    - Docs/TechManual_技术手册.md (如需技术细节)
```

### 关键约束提醒

- **C# 5.0 语法限制**: `Modules/` 下代码禁止使用 `$""`、`?.`、nameof
- **配置同步**: `ZDProjects/*_OwnCode.cs` 修改需手动复制到 ZennoDroid
- **ui_selectors**: 必须通过 `SelectorEngine.GetSelector()` 读取

### 当前版本状态

- **版本**: v4.5.10 (2026-03-04)
- **最新变更**: App Onboarder 新平台自动接入工具（Python CLI，6 模块，自动探索 + 配置生成 + E2E 测试 + 自动修复）
- **新增目录**: `Tools/app_onboarder/` — 独立 Python 工具（不影响 C# 主项目）
- **待测试**: App Onboarder 真机集成测试（需 ADB 连接）
