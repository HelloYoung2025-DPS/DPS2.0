# 架构决策记录 (ADR)

本文档记录 DPS v4.5 项目的关键架构决策，按时间顺序排列。

---

## ADR-001: CSharpCodeProvider 动态编译

- **日期**: 2026-01 (v4.0.0)
- **状态**: 已接受

### 背景

ZennoDroid 不支持预编译 DLL 引用，需要在运行时加载业务逻辑。项目规模增长后，将所有代码内联在 Own Code 块中变得不可维护。

### 决策

使用 `CSharpCodeProvider` 在内存中动态编译 `.cs` 文件，通过 `ModuleLoader` 管理编译缓存（LRU 策略，32 条目上限）。

### 理由

ZD 的 Own Code 环境限制了外部 DLL 加载；动态编译允许热更新，修改 `.cs` 文件后自动触发重编译，无需重启项目。

### 后果

- 限制为 C# 5.0 语法，不能使用新版语言特性
- 首次编译耗时约 500ms，缓存命中后 <10ms
- 所有 `.cs` 文件在同一编译单元内，类名不能冲突
- 编译错误在运行时才能发现，调试成本较高

### 备选方案

- 预编译 DLL：ZD 环境不支持，排除
- 纯 Own Code 内联：代码量超过 5000 行后完全不可维护，排除

---

## ADR-002: 三管道执行回退

- **日期**: 2026-02-07 (v4.5.0)
- **状态**: ~~已接受~~ → **已取代**（v4.5.11，2026-03-08）

### 背景

需要灵活的操作执行机制，同时保持对旧脚本的向后兼容。随着项目演进，出现了三种不同风格的代码组织方式，需要统一调度。

### 原始决策（已废弃）

三级回退执行链：

1. `ActionExecutor`（JSON 步骤驱动）
2. ~~`PlatformModule`（动态编译加载）~~
3. ~~`Independent Scripts`（`ZDProjects/*.cs` 旧脚本）~~

### 取代说明（v4.5.11）

经验证，主执行链已完全切换到 `ActionExecutor + operations.json` 单一路径。第 2、3 管道的代码分支虽存在但永远返回 ERROR，属于死代码。v4.5.11 正式移除了 `LoadPlatformModule` 方法和 `Platforms/` 目录下的旧模块实现（`RedditModule.cs`、`InstagramModule.cs`），以及 `Core/PlatformBase.cs` 接口定义。

当前唯一执行路径：`SessionRunner` → `ExecuteWithUnifiedEngine` → `ActionExecutor.Execute(_operationsJson, opName, _platformConfig)`

### 后果

- 消除了三套执行路径的维护复杂度
- 所有平台操作通过 `Config/Operations/*.json` + `Config/IntentMappings/*.json` 定义
- `ResolveIntentForAction` 和 `MapActionToOperations` 保留硬编码默认映射作为安全网

---

## ADR-003: 手写 JSON 解析器

- **日期**: 2026-01 (v4.0.0)，重写于 v4.1.0
- **状态**: 已接受

### 背景

ZD 环境无法使用 `Newtonsoft.Json` 或 `System.Text.Json`，可用引用仅限 `System.dll` 等 5 个基础程序集。项目大量依赖 JSON 配置文件和 AI API 响应解析。

### 决策

手写 `JsonHelper.cs`（993 行，23 个方法，栈式解析器），支持嵌套对象、数组、转义字符、Unicode 编码。v4.1.0 从正则表达式方案完全重写为栈式解析器。

### 理由

零依赖是硬性约束；v4.0 的正则表达式方案在处理嵌套对象时存在严重 bug，v4.1.0 重写为栈式解析器后彻底修复。

### 后果

- 维护成本高，993 行手写解析代码需要人工维护
- 零依赖，完全可控，不受第三方库版本影响
- 性能可接受，解析典型配置文件 <5ms

### 备选方案

- 简单正则解析：v4.0 用过，嵌套对象有 bug，已排除
- MiniJSON：需要额外文件引入，且在 ZD 编译环境下兼容性未验证，排除

---

## ADR-004: AI 3 级 Fallback

- **日期**: 2026-01 (v4.0.0)
- **状态**: 已接受

### 背景

AI 服务不稳定，单一 API 可能因限流、网络问题、服务中断而失败。画像生成和评论生成是核心功能，不可用会导致整个会话失败。

### 决策

3 级降级策略：

1. Gemini（主要）
2. Claude via deeprouter（备用）
3. GPT via shubiaobiao（兜底）

每级最多重试 3 次，退避 2 秒。

### 理由

确保画像生成和评论生成的高可用性；不同提供商的故障通常不相关，多提供商组合显著提升整体可用性。

### 后果

- 需要维护 3 套 API 密钥和对应的请求格式
- 不同模型的输出风格可能略有差异，影响画像一致性
- 故障转移时延迟增加（最坏情况约 6 秒额外等待）

### 备选方案

- 单一 API + 更多重试：可用性不够，单点故障风险高，排除
- 本地模型：ZD 环境不支持，排除

---

## ADR-005: 双重记忆系统

- **日期**: 2026-02-13 (v4.5.1)
- **状态**: 已接受

### 背景

需要同时支持 `WeeklyEvolve` 的行为统计分析和 `SessionRunner` 的实时去重。两者对记忆数据的格式和访问模式需求不同，难以用单一系统满足。

### 决策

两套并行记忆系统：

1. 日记忆文件 `Memory/{device_id}/{date}.json`，由 `SessionRunner` 手动写入，格式简单
2. 结构化交互记录 `Memory/{device_id}/{app}/interactions.json`，由 `MemoryManager` 管理

### 理由

日记忆文件格式简单，适合 `WeeklyEvolve` 做统计分析；`MemoryManager` 提供去重、查询、清理等高级功能，适合实时决策。

### 后果

- 数据冗余，同一操作会被记录两次
- 各自服务不同用途，解耦清晰，互不干扰
- 存储占用略高，需要定期清理旧记忆文件

### 备选方案

- 统一记忆系统：需要大规模重构 `WeeklyEvolve` 的统计逻辑，风险高，排除

---

## ADR-006: Extension 系统

- **日期**: 2026-02-07 (v4.5.0)
- **状态**: 已接受

### 背景

需要可插拔的数据源（IP 定位、天气信息）和生命周期钩子。硬编码数据源导致每次新增数据类型都要修改核心代码。

### 决策

`IExtension` 接口 + `ExtensionManager` 加载器，通过 `ExtensionsRegistry.json` 注册扩展。扩展分为 `DataSource` 和 `Hooks` 两类。

### 理由

解耦数据源获取和核心业务逻辑；支持按需启用/禁用扩展，不影响核心流程。

### 后果

- `Hooks` 子系统尚未实现，`Extensions/Hooks/` 目录为空（已知技术债）
- 当前仅有 2 个 `DataSource` 扩展（IP 定位、天气）
- 扩展加载失败不会中断主流程，静默降级

### 备选方案

- 硬编码数据源：不可扩展，每次新增都要改核心代码，排除
- 插件 DLL：ZD 不支持外部 DLL，排除

---

## ADR-007: 人格驱动行为 + 7 阶段生命周期

- **日期**: 2026-01 (v4.0.0)
- **状态**: 已接受

### 背景

社交媒体自动化需要模拟真实用户行为，避免被平台风控检测。固定行为模板和纯随机行为都容易被识别为机器人。

### 决策

每个设备拥有 AI 生成的独立画像（100+ 字段），包含大五人格、兴趣偏好、行为参数。7 阶段生命周期（TTC → T1 → T2 → T3 → PP0 → PP1 → NP）控制行为参数随时间变化。

### 理由

画像驱动使每个账号行为独特，难以通过行为指纹识别；生命周期模拟真实用户的成长变化，使账号行为随时间自然演进。

### 后果

- 画像生成依赖 AI API，首次生成需要网络请求
- `DailyUpdate` 每天自动推算阶段码，无需人工干预
- `WeeklyEvolve` 每周 AI 分析行为并微调画像，保持行为新鲜感
- 画像字段变更需要同步更新 AI 提示词和解析逻辑

### 备选方案

- 固定行为模板：容易被平台检测，排除
- 纯随机行为：不自然，缺乏一致性，同样容易被检测，排除

---

## ADR-008: 平台抽象

- **日期**: 2026-02-07 (v4.5.0)
- **状态**: ~~已接受~~ → **已取代**（v4.5.11，2026-03-08）

### 背景

需要支持多个社交媒体平台，每个平台的 UI 结构和操作逻辑差异显著。如果不做抽象，`SessionRunner` 会充斥大量平台判断分支。

### 原始决策（已废弃）

~~`PlatformBase` 定义标准接口（`Initialize` / `Browse` / `Like` / `Comment` / `Follow` / `Share`），每个平台实现独立模块。~~

### 取代说明（v4.5.11）

平台差异现在完全通过 JSON 配置解决，不再需要 `PlatformBase` 接口或独立模块：

- **UI 差异** → `Config/PlatformsConfig.json` 中的 `ui_selectors`（每个平台定义选择器）
- **操作流程差异** → `Config/Operations/{platform}_operations.json`（每个平台定义步骤序列）
- **意图映射差异** → `Config/IntentMappings/{platform}_intents.json`（action → intent → operations）
- **页面状态识别** → `PlatformsConfig.json` 中的 `page_signatures`

`PlatformBase.cs` 和 `Platforms/` 目录在 v4.5.11 中被删除。

### 后果

- 新增平台只需添加 JSON 配置文件，无需编写 C# 模块
- `Tools/app_onboarder/` 可自动生成这些配置
- 所有平台共享同一套 `ActionExecutor` 执行引擎

---

## ADR-009: 人性化引擎 + 4 种行为配置

- **日期**: 2026-02-07 (v4.5.0)
- **状态**: 已接受

### 背景

自动化操作需要模拟真实用户的操作节奏和误差。固定延迟和完美精度的操作是机器人的典型特征，容易被风控识别。

### 决策

`HumanizationEngine` 提供 4 种行为配置：

- `speed_demon`：快速操作，低延迟
- `casual`：随意浏览，中等延迟
- `deep_reader`：深度阅读，长停留
- `distracted`：分心用户，不规律操作

控制点击偏移、滑动弯曲度、延迟变化范围、概率性失误（误触、回滚）。

### 理由

不同用户有不同的操作习惯；概率性失误（误触后回退）增加真实感，是纯随机延迟无法模拟的行为特征。

### 后果

- 行为配置名在代码和文档中存在不一致（已知技术债，待统一）
- 4 种配置覆盖了主要用户类型，边缘情况可通过参数微调

### 备选方案

- 固定延迟：不自然，容易被检测，排除
- 纯随机延迟：不可控，无法模拟特定用户风格，排除

---

## ADR-010: 配置驱动 UI 选择器

- **日期**: 2026-02-13 (v4.5.1)
- **状态**: 已接受

### 背景

Reddit / Instagram 的 UI 元素 `resource-id` 可能随 APP 版本更新而变化。硬编码选择器导致每次 UI 变更都需要修改代码并重新部署。

### 决策

UI 选择器存储在 `PlatformsConfig.json` 的嵌套对象中，每个选择器包含 4 个字段：

- `strategy`：主选择策略（如 `resource-id`）
- `value`：主选择器值
- `fallback_strategy`：备用策略（如 `text`）
- `fallback_value`：备用选择器值

### 理由

配置化使 UI 变更只需修改 JSON 文件，无需改代码；`fallback` 机制在主选择器失效时自动降级，提升容错能力。

### 后果

- 嵌套对象解析需要 `SelectorEngine`，不能直接用 `GetJsonValue`（会返回 `{` 字符）
- 选择器配置文件需要随 APP 版本更新维护
- 配置错误在运行时才能发现，需要完善的错误日志

### 备选方案

- 硬编码选择器：每次 UI 更新都要改代码，维护成本高，排除

---


## ADR-011: Intent-Based Execution


- **日期**: 2026-02-27 (v4.5.6)
- **状态**: 已接受

### 背景

DPS 直接调用 ZennoDroid API 导致决策层和执行层耦合。需要抽象层分离"做什么"（Intent）和"怎么做"（ZDCommand），提高可测试性和可扩展性。

### 决策

引入 Intent 抽象类、ZDCommand 命令类、ZDResult 结果类，通过 IntentTranslator 将高层意图翻译为物理命令：

- **Intent.cs**: 高层操作意图抽象（168 行），包含操作类型和语义参数
- **ZDCommand.cs**: 物理命令类（333 行），封装 Tap/Swipe/SendText 等操作
- **ZDResult.cs**: 统一结果类（311 行），Success/FailedRetryable/FailedFatal/Skipped
- **IntentTranslator.cs**: 翻译器（472 行），解析选择器、计算坐标、处理回退链

### 理由

清晰分离 DPS（大脑）和 ZennoDroid（手）职责；Intent/ZDCommand/ZDResult 都是纯数据结构，易于测试和序列化。

### 后果

- 保持向后兼容，ActionExecutor 旧接口保留
- 新增 5 个核心模块，代码行数增加约 1700 行
- 架构清晰度大幅提升，便于添加视觉验证层

### 备选方案

- 保持现状：决策和执行耦合，难以测试和扩展，排除
- 引入消息队列：ZD 环境不支持复杂中间件，排除

---

## ADR-012: Vision Verification Layer

- **日期**: 2026-02-27 (v4.5.6)
- **状态**: 已接受

### 背景

UI 操作后无法确认是否成功执行，"以为点到了但实际没点到"的情况时有发生。需要视觉验证机制提高操作可靠性。

### 决策

在关键操作（like, comment）执行后添加 Gemini Flash 截图验证：

1. 调用 `VisionCorrector.AnalyzeAndRecover()` 验证结果
2. 失败时标记操作为失败，记录失败原因
3. 通过 `behavior_config_json` 中的 `vision_verification_enabled` 控制开关

### 理由

视觉验证显著提高操作成功率；Gemini Flash 速度快（~2 秒），成本低；可配置开关避免延迟。

### 后果

- 每次验证增加 2-3 秒延迟
- 成功率提升，减少无效操作
- 依赖 Gemini API 可用性

### 备选方案

- 纯重试机制：盲目重试无法定位问题，排除
- 本地图像识别：ZD 环境不支持，排除

---

## ADR-013: Manifest v2.0 Format

- **日期**: 2026-02-27 (v4.5.6)
- **状态**: 已接受

### 背景

现有 Manifest 格式混合了 DPS 层和 ZennoDroid 层信息，包含物理参数如 duration/duration_ms，违背分层架构原则。

### 决策

重新设计 Manifest v2.0 格式，分离语义和物理参数：

- **capabilities**: APP 能做什么（11 种操作：browse_feed, like_post, comment, etc.）
- **states**: 如何识别当前状态（visual_markers + ui_signatures + gemini_prompt）
- **intent_mappings**: 意图翻译为操作（含 fallback_intents）
- **rate_limits**: 速率限制（per_hour + cooldown_seconds + per_day）

### 理由

配置格式清晰反映架构分层；删除物理参数，由 IntentTranslator 在运行时生成；易于扩展新平台。

### 后果

- 旧格式仍然支持，新格式逐步迁移
- 所有新平台应使用 v2.0 格式
- 简化了配置文件，减少了人为错误

### 备选方案

- 直接修改旧格式：破坏向后兼容性，排除
- 两套配置并存：增加维护成本，最终选择渐进式迁移

---

## ADR-014: 文档双层职责与新人施工图优先

- **日期**: 2026-03-07 (v4.5.16)
- **状态**: 已接受

### 背景

`Docs/` 在多轮合并后，同时承担了新人入门、架构说明、平台指南、测试说明等多种职责。现有文档出现了三个问题：

1. `ConfigGuide` 中的流程图偏概念说明，无法直接指导新人在 ZennoDroid 中搭建动作块和条件分支
2. `TechManual`、模板文档、平台指南存在历史文件名和已失效路径，容易把读者引到仓库中不存在的文件
3. 部分文档沿用“理想设计”或历史行为口径，和当前仓库真实运行链不一致

### 决策

将 `Docs/` 的职责固定为两层：

1. `Docs/ConfigGuide_配置指南.md` 作为**新人施工文档**
   - 必须使用可执行的 ZennoDroid 搭建步骤
   - 流程图必须能直接照着创建模块、条件块和变量
   - 变量名、返回值、条件表达式必须与当前代码一致
   - 每张流程图必须标明它是“ZennoDroid 施工图”还是“模块内部逻辑图”
   - 只要存在分支，就必须写清分支依据变量、返回值和 ZennoDroid 条件表达式
   - 必须明确区分“首次最小闭环”和“完整生产链”
2. `Docs/TechManual_技术手册.md` 作为**架构与参考文档**
   - 负责解释主执行链、模块边界、配置契约、测试方案
   - 不再假定读者会根据它直接在 ZennoDroid 里施工

同时要求 `Docs/README.md` 与 `Docs/DOCS_RULES.md` 始终反映真实文件清单，所有平台指南和模板文档必须引用当前仓库真实存在的路径。

### 理由

新人最常见的失败点不是“看不懂概念”，而是“照着图搭项目却搭错节点或变量”。把施工职责集中到 `ConfigGuide`，能减少路径错误、变量错误和错误分支。把架构说明集中到 `TechManual`，能避免同一内容在多个文档里出现不同说法。

### 后果

- 文档更新时必须先判断是“施工说明”还是“架构参考”，避免两类内容继续混写
- `ConfigGuide` 的流程图需要维护到 ZennoDroid 可直接照做的粒度
- `ConfigGuide` 中的每个分支都必须能回溯到具体变量与返回值，禁止只写“无画像 / 准备就绪”这类抽象分支名
- `TechManual`、模板文档、平台指南中的历史死链需要持续清理

### 备选方案

- 保持现状：文档混用概念说明和施工说明，继续制造新人接线错误，排除
- 继续拆分成多个新文件：违反当前 `Docs/` 根目录禁止新增文件的规则，排除

---

## ADR-015: SessionRunner 执行层向 ZennoDroid 原生动作迁移

- **日期**: 2026-03-14
- **状态**: 已接受

### 背景

当前 SessionRunner 中的 APP 具体执行动作（tap、swipe、input 等）全部由 C# 代码通过 ActionExecutor 驱动。这种方式的拟人化程度不足，无法充分利用 ZennoDroid 原生的拟人行为能力（如贝塞尔曲线滑动、随机偏移、分心模式等）。

### 决策

将执行层分为四层：

1. **ZD 原子动作层**: tap、double_tap、swipe、long_press、input_text、wait、back、scroll_once 等最小执行单元，由 ZD 动作块或子工作流承载，通过编号 + Switch 动作块分发
2. **ZD 组合动作层**: open_post、like_post、comment_post、browse_feed、back_to_feed 等业务子流程，由若干原子动作组合而成，对齐现有 `Config/Operations/*.json` 中的 operation 定义
3. **SessionRunner 智能编排层**: 保留会话生命周期总控、意图编排、恢复编排、成功门控；新增智能编排器子模块负责判断上一步是否执行正确并决定恢复策略
4. **AI 视觉纠偏层**: 在分级恢复失败后介入，允许识别当前页面状态、提供纠偏建议、临时修正功能块或小流程（仅限当前 session）

Switch 编号路由采用混合组织方式：先平台、再页面、再动作。

成功判定采用双层机制：
- ZD 层判定执行成功（动作块或子工作流无运行时错误）
- 编排器判定业务成功（页面状态符合预期、关键元素变化、变量更新）

### 理由

- ZennoDroid 原生动作具备更好的拟人化能力，C# 直接驱动的 tap/swipe 缺乏自然偏移和曲线
- 分层设计使各层可独立演进，原子动作可跨平台复用
- 智能编排器先内嵌后独立，降低一次性改造风险
- 双层成功判定避免"假成功"（动作执行完但页面没变化）
- Reddit 作为试点验证框架有效性，最终沉淀为通用框架

### 后果

- 需要在 ZennoDroid 项目中创建原子动作块和 Switch 路由
- 现有 `Config/Operations/*.json` 中的 operation 定义保留复用，不重造
- SessionRunner 保留编排、恢复、日志、会话控制职责，剥离物理执行细节
- 迁移过程需要 feature-flag 实现新旧路径并存，支持安全回滚
- AI 视觉修正仅限 session 级别，不自动持久化为长期规则

### 备选方案

- 保持纯 C# 执行：拟人化不足，无法利用 ZD 原生能力，排除
- 全部重写为 ZD 项目流程：SessionRunner 失去会话控制能力，风险过高，排除
- 一次性大爆炸迁移：无法安全回滚，排除

---
