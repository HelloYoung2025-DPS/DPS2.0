# DPS v4.5 术语表 (Glossary)

> 版本: v1.2
> 更新日期: 2026-02-14

本文档定义了 DPS v4.5 项目中使用的所有专业术语和缩写。

---

## A

### Action (动作)
用户在社交平台上执行的单个操作，如浏览、点赞、评论等。每个动作都有对应的权重和持续时间配置。

### ActionExecutor (操作执行器)
v4.5 新增的统一操作执行引擎，位于 `Modules/Core/ActionExecutor.cs`。通过 JSON 步骤定义驱动执行流程，将平台操作抽象为可配置的步骤序列。

### Action Weight (动作权重)
决定某个动作被选中概率的数值。在 `BehaviorConfig.json` 中配置，用于加权随机选择算法。

### AI Service (AI 服务)
封装了多个 AI 模型调用的服务层，支持 Gemini、OpenAI 等提供商，具有自动重试和备选机制。

---

## B

### Backup Model (备选模型)
AI 服务的第三层模型，当 Primary 和 Fallback 都失败时使用。

### Behavior Config (行为配置)
定义用户行为参数的配置文件 (`BehaviorConfig.json`)，包含动作权重、打字速度、持续时间等。

### Behavior Profile (行为配置文件)
v4.5 新增。定义用户行为模式的预设配置，包括：
- **casual**: 默认模式，中等速度
- **active**: 快速浏览，短阅读时间
- **lurker**: 慢速浏览，长阅读时间，只看不互动
- **new_user**: 行为谨慎，适合新账号

### Big Five Personality (大五人格)
心理学人格模型，包含五个维度：
- **Openness** (开放性)
- **Conscientiousness** (尽责性)
- **Extraversion** (外向性)
- **Agreeableness** (宜人性)
- **Neuroticism** (神经质)

---

## C

### Compilation Cache (编译缓存)
v4.5 新增。缓存已编译的模块，避免重复编译，将后续运行时间从 ~500ms 降至 <10ms。

### Conception Date (受孕日期)
画像中记录的怀孕开始日期，用于计算当前孕周和阶段流转。

### Core Helper (核心辅助)
提供 ZD 变量读写、日志输出、文件操作等基础功能的核心模块。

### CSharpCodeProvider
.NET Framework 提供的动态编译器，DPS 使用它在运行时编译 .cs 文件。仅支持 C# 5.0 语法。

---

## D

### Daily Update (每日更新)
每天运行一次的模块，更新画像中的时间相关字段（年龄、孕周、季节等）。

### DecisionConfig (决策配置)
v4.5.1 新增。配置文件 (`Config/DecisionConfig.json`)，定义 RuleEngine 规则、疲劳模型参数、MemoryManager 去重窗口期和记忆限额等。

### Device ID (设备标识)
每个模拟设备的唯一标识符，用于区分不同的画像和记忆数据。格式如 `Device_001` 或 `R58M4816G8Y`。

### DPS (Dynamic Persona System)
动态人物画像系统，本项目的核心框架名称。

### .droid 文件
ZennoDroid 项目文件格式，包含流程控制和 UI 操作逻辑。

---

## E

### Error Recovery (错误恢复)
v4.5 新增。自动处理操作失败的机制，支持最多 3 次重试，使用指数退避策略（2s, 4s, 8s）。

### Evolution (进化)
画像属性随时间和行为模式变化的过程，由 `WeeklyEvolve` 模块执行。

### Exponential Backoff (指数退避)
重试策略，每次重试的等待时间呈指数增长。DPS 使用 2s → 4s → 8s 的退避序列。

### Extension (扩展)
通过 `IExtension` 接口实现的可插拔功能模块，由 `ExtensionManager` 管理生命周期。用于 IP 检查、地理位置模拟、天气同步等功能。

### ExtensionManager (扩展管理器)
位于 `Modules/Core/ExtensionManager.cs`。负责发现、加载、初始化和卸载扩展模块。提供 `LoadExtensions()`、`RunAll()` 等方法，支持通过 `ExtensionsConfig.json` 配置扩展参数。

---

## F

### Fallback Model (备选模型)
AI 服务的第二层模型，当 Primary 模型失败时自动切换。

### Fatigue Model (疲劳模型)
v4.5.1 新增。SessionRunner 中的能量衰减系统，模拟用户疲劳。能量不足时自动禁用高消耗动作（如 comment、like），等待时恢复能量。配置在 `DecisionConfig.json` 的 `fatigue_model` 节。

### FileHelper (文件辅助)
提供文件读写、原子写入、目录管理等功能的核心模块。

### Fuzzy Memory (模糊记忆)
超过 180 天的事件会被转换为模糊描述，如 "2025-06-15 腿骨折" → "去年 6 月中旬腿摔断了"。

---

## H

### Humanization Engine (人性化引擎)
v4.5 新增。生成拟人化行为参数的核心模块，包括滚动速度、暂停时间、打字节奏等。

### HumanizationEngine (人性化引擎类)
位于 `Core/HumanizationEngine.cs`，模拟真实用户行为的核心引擎。提供 4 种行为配置文件（`casual`、`active`、`lurker`、`new_user`），控制点击延迟、滚动速度、随机偏移等参数。

---

## I

### IExtension (扩展接口)
位于 `Modules/Core/IExtension.cs`。所有扩展模块必须实现的接口，定义了 `Name`、`Initialize()`、`Execute()` 和 `Cleanup()` 方法。由 `ExtensionManager` 统一调用。

### Initializer (初始化器)
系统启动时运行的模块，负责创建目录结构、验证配置完整性。

---

## J

### JsonHelper (JSON 辅助)
手动实现的 JSON 解析器，不依赖外部库，支持字段读取、设置和转义处理。

---

## L

### Life Stage (生命阶段)
画像的当前生命周期阶段，共 7 个阶段：TTC, T1, T2, T3, PP0, PP1, NP。

### Long-term Memory (长期记忆)
保留 180 天以上的重要事件摘要，存储在 `Memory/{device_id}/long_term.json`。

---

## M

### Main (主入口)
系统的核心调度模块，检查画像状态并决定下一步操作。

### Maintenance (维护)
清理过期日志、备份和记忆文件的模块，根据 `MaintenanceConfig.json` 配置执行。

### Memory (记忆)
记录用户会话行为的数据，分为短期记忆、长期记忆、行为模式和社交记忆四层。

### MemoryManager (交互记忆管理器)
v4.5.1 新增，位于 `Modules/MemoryManager.cs`。结构化交互记录系统，提供：
- `RecordInteraction()` — 记录每次操作
- `IsDuplicate()` — 窗口期内去重检测
- `CleanupOldInteractions()` — 过期数据清理
- `EnforceMemoryLimits()` — 硬上限管控
数据存储在 `Memory/{device_id}/{platform}/interactions.json`。

### MethodInfo
.NET 反射 API 中表示方法信息的类，编译缓存中存储的就是 MethodInfo 对象。

### Module (模块)
DPS 中的功能单元，每个模块是一个 .cs 文件，包含 `Run(object projectObj)` 入口方法。

### ModuleLoader (模块加载器)
负责动态编译和加载 .cs 模块的核心组件。

---

## N

### NP (Nursing Period)
育儿期，孩子 12 个月以上的阶段。

---

## O

### Own Code (自有代码)
ZennoDroid 中的 C# 代码块，用于执行自定义逻辑。DPS 的入口点都是 Own Code 块。

---

## P

### PageDetector (页面检测器)
v4.5 新增，位于 `Modules/Core/PageDetector.cs`。通过分析当前 UI 布局 XML 判断当前页面状态（如主页、帖子详情、评论区、错误弹窗等），为 SessionRunner 提供上下文感知的决策支持。

### Persona (人物画像)
包含 100+ 字段的用户模拟数据，包括基础信息、性格特征、兴趣偏好、行为参数等。

### PersonaCreate (画像创建)
调用 AI 生成初始画像的模块，使用 `PersonaPrompt.txt` 作为提示词。

### Platform (平台)
v4.5 新增。支持的社交媒体平台，目前包括 Reddit 和 Instagram。

### Platform Base (平台基类)
v4.5 新增。定义平台模块标准接口的抽象类，所有平台模块都必须实现这些接口。

### Platform Module (平台模块)
v4.5 新增。特定平台的操作实现，如 `RedditModule.cs`、`InstagramModule.cs`。

### PP0 (Postpartum 0)
产后早期，孩子 0-3 个月的阶段。

### PP1 (Postpartum 1)
产后中期，孩子 4-12 个月的阶段。

### Primary Model (主模型)
AI 服务的首选模型，通常是 Gemini。

### project_root (项目根目录)
DPS 项目的根目录路径，所有相对路径都基于此目录。

---

## R

### Rate Limit (速率限制)
v4.5 新增。平台对操作频率的限制，如 Instagram 限制 60 actions/hour。

### ReportGen (报告生成)
生成每日/每周运行报告的模块，在 17:00 后自动触发。

### Retry (重试)
操作失败后的自动重试机制，最多 3 次，使用指数退避策略。

### RuleEngine (规则引擎)
v4.5.1 新增，位于 `Modules/RuleEngine.cs`。帖子评估门控系统，根据 `DecisionConfig.json` 中的规则对帖子进行评分，决定是否执行指定动作。SessionRunner 使用 `EvaluatePostForAction()` 调用。

### Run() 方法
每个模块的入口方法，签名为 `public static string Run(object projectObj)`。

---

## S

### Session (会话)
用户在平台上的一次连续活动，包含多个动作，有开始和结束时间。

### Session Plan (会话计划)
由 Main 模块生成的会话执行计划，包含动作分布、持续时间、行为参数等。

### SelectorEngine (选择器引擎)
位于 `Modules/Core/SelectorEngine.cs`。负责解析 `PlatformsConfig.json` 中的嵌套 `ui_selectors` 对象，提取 `strategy`、`value`、`fallback_strategy`、`fallback_value` 等字段，为 UI 元素查找提供统一的选择器解析服务。

### SessionRunner (会话执行器)
执行会话计划的模块，负责选择动作、调用平台模块、记录行为。

### Short-term Memory (短期记忆)
保留 7-30 天的会话记录，存储在 `Memory/{device_id}/{date}.json`。

### Social Memory (社交记忆)
记录与其他用户互动历史的数据，存储在 `Memory/{device_id}/social.json`。

### Stage Code (阶段码)
表示生命阶段的缩写代码：TTC, T1, T2, T3, PP0, PP1, NP。

### Stage Config (阶段配置)
定义不同生命阶段参数的配置文件 (`StageConfig.json`)，包含会话频率、时长等。

### StateSaver (状态保存器)
保存画像和运行统计的模块，在每次会话结束后执行。

### Stats (统计)
运行统计数据，包括总运行次数、总动作数等，存储在 `Stats/{device_id}_stats.json`。

---

## T

### T1 (Trimester 1)
孕早期，怀孕 1-13 周。

### T2 (Trimester 2)
孕中期，怀孕 14-27 周。

### T3 (Trimester 3)
孕晚期，怀孕 28-40 周。

### TTC (Trying To Conceive)
备孕期，未怀孕且无孩子的阶段。

### Typing Skill Level (打字技能等级)
画像中的打字速度参数，分为 slow、regular、fast 三个等级。

---

## U

### UI Locator (UI 定位器)
v4.5 新增。多策略 UI 元素定位模块，支持 resource-id、XPath、图像识别等方式。

---

## W

### Weekly Evolve (每周进化)
每周运行一次的模块，分析 7 天行为模式，由 AI 提出画像调整建议。

### Weight (权重)
用于加权随机选择的数值，决定某个选项被选中的概率。

---

## Z

### ZD (ZennoDroid)
Android 模拟器自动化软件，DPS 的运行平台。

### ZD Variable (ZD 变量)
ZennoDroid 项目中的变量，用于模块间数据传递。

---

## 缩写对照表

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

*文档版本: v1.2 | 最后更新: 2026-02-14*
