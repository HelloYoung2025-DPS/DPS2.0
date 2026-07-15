# DPS - ZennoDroid Device Agent Platform

> **当前状态**: Legacy modernization in progress
>
> **代码基线**: 现有 v4.x 研究原型
>
> **目标平台**: DPS Control Plane + GBrain Company + ZennoDroid Thin Executor
>
> **当前正式签发证据级别**: `NONE`（未提交工作区不能通过 clean-checkout 正式 Phase 0；workspace diagnostic 不签发等级）
>
> **尚未取得**: `REPOSITORY_STATIC_VERIFIED`, `CONTRACT_VERIFIED`, `INTEGRATION_VERIFIED`, `WINDOWS_VERIFIED`, `DEVICE_VERIFIED`, `CANARY_VERIFIED`, `SCALE_VERIFIED`

重要: 目前已有定向静态、合同和单元诊断通过；需要 PostgreSQL 的必需套件在本机未提供受信测试实例时按设计失败关闭，不得被记为集成通过。未提交工作区也不能形成可归属到精确提交的正式证据；workspace diagnostic 即使通过也不签发仓库级验证称号。即使未来静态门通过，它也不证明 Windows ZennoDroid 可装载、手机动作成功、灰度安全或 200 台规模能力. 现有 v4.x 运行代码是待迁移的 legacy baseline; 新模块除非其 `module.yaml`, 源码, 自动测试和证据共同证明, 一律视为 `proposed`, 不能因目录或文档存在而视为已实现. 目标架构见 [TargetArchitecture_目标架构.md](Docs/Architecture/TargetArchitecture_目标架构.md), 工程验收规则见 [EngineeringStandards_工程标准.md](Docs/EngineeringStandards_工程标准.md).

---

## 🎯 项目简介

DPS 当前是一个正在重构的 ZennoDroid 设备自动化研究项目. 现有代码保留意图, operation 和原生动作实验; 这些能力尚未获得现代化生产验收. 新架构计划由 Control Plane 管理身份与命令, GBrain Company 保存每个 Soul 的长期 Persona, Interests 和记忆, ZennoDroid 只执行经过授权的确定性步骤.

### 当前代码与目标方向

| 能力 | 当前事实 | 验证边界 |
|---|---|---|
| Intent, operation, ActionExecutor | Legacy 源码和配置存在 | 尚未完成 contract 和 real-device 证明 |
| Manifest 和 App Onboarder | Legacy 工具与样例存在 | 尚未证明任意 APP 可安全接入 |
| Vision 和 humanization | Legacy 实验代码存在 | 历史性能数字未在当前基线复验 |
| 多设备与会话门控 | 迁移对象 | 尚未达到跨 Soul, 设备和账号隔离门 |
| GBrain Soul memory | 离线 candidate 已实现; Company 实连仍为 Proposed | 需 GBrain Source 隔离, 精确读回, checksum, 删除/重建和两部非生产手机证明 |
| Thin ZennoDroid executor | Proposed | 需 Windows 探测, A/B 和 Zenno 不重启证明 |

### 目标边界, Proposed

```
DPS AI Factory --upgrade artifacts and evidence--> DPS Control Plane
                                                       |
                                     +-----------------+-----------------+
                                     |                                   |
                             GBrain Company                       Windows Edge
                          Persona and memory                 queue and A/B worker
                                                                         |
                                                                   ZennoDroid
                                                            deterministic executor
```

### 当前现代化工作区事实

下表描述未提交工作区中已能运行的 candidate/foundation, 不是正式签发等级:

| 范围 | 当前可执行事实 | 仍缺的出口证据 |
|---|---|---|
| F0-F1 Governance | 所有注册模块的 Manifest、AGENTS、公共合同、套件、DAG 和兼容关系均由唯一门禁现场重建；文档不另存易漂移的数量副本 | 当前工作区非干净; 受保护提交和 clean-checkout 正式 Phase 0 尚未形成 |
| Contract candidate | 唯一候选 Runner 必须精确匹配当前 Manifest 清单、受信策略、收据、测试树和证据摘要；任一漂移均失败 | Runner 固定为 unsigned；`candidate_verification_level` 和 `verification_level` 均为 `null`，不构成 `CONTRACT_VERIFIED` |
| Integration candidate | 套件明确区分真实 PostgreSQL、真实本地子进程、确定性模拟与外部设备；门禁从当前 Manifest 计算覆盖缺口 | 本机未配置受信 PostgreSQL；缺基础设施、覆盖缺口或任一必需套件非 `PASS` 时必须失败 |
| F2 Soul vertical slice | 已有 Soul resolve、append-only event/outbox、interest decay、离线 GBrain projection 和 evidence bundle 的 PostgreSQL 实现与必需测试 | 本轮环境无法执行真实 PostgreSQL 套件；独立证据签发及生产数据库权限也未配置 |
| F3-F4 AI Factory | 已实现外部 PostgreSQL lease/evidence、monotonic fence、真实临时 Git worktree 并行/依赖排序/合并头重测、受限 Runner、artifact/SBOM/provenance、BOM、发布与回滚状态机及故障注入模拟 | 当前模拟不是 100 台持续或 200 台突发真实并发；外部 signer、默认分支保护、两人审批和已部署上一稳定 Factory 尚未证明 |
| F5 Product modules | identity、device/account/binding、persona、planner/policy/compiler/orchestrator/executor/audit 等模块已有独立源码、合同、锁文件和分类测试 | Legacy SessionRunner 仅完成字节/签名/Golden Trace 静态冻结，尚未真实绞杀接线；需要 Integration 但没有套件的模块由门禁动态列为阻断；Bridge/Edge ABI 与后续迁移还需目标 Windows 探测 |
| F6 Edge foundation | .NET 10 A/B Supervisor/Worker/Journal 与 `net40`/C# 5 Bridge 已有分层源码、合同与失败关闭测试；仍属候选基础 | 生产 Host/ABI 组合必须在目标 Windows 能力探测后定稿；真实 Windows、ZennoDroid、ADB、100 次切换、PID/启动时间不变和 24 小时证据为 `WAITING_EXTERNAL` |
| F7-F9 External gates | GBrain/双非生产手机/30 台/200 台输入 Schema, 固定信任策略和失败关闭验证器已存在 | 实际 GBrain 读回, 真机, 灰度, 回滚演练, 72 小时和规模证据均为 `WAITING_EXTERNAL` |

当前正式等级仍为 `NONE`. 本地 Phase 0 diagnostic 或 Contract/Integration candidate 即使通过，也不签名、不归属正式提交、不签发任何验证等级。正式升级还需要受保护提交、clean-checkout 重测、外部受信 Runner、独立证据签发者和独立发布批准者。详细的仓库外权限条件见 [RepositoryProtection_仓库保护.md](Docs/Operations/RepositoryProtection_仓库保护.md), 本地 GBrain 运维边界见 [GBrainCompany_LocalNonProduction_本地非生产.md](Docs/Operations/GBrainCompany_LocalNonProduction_本地非生产.md), F6-F9 输入边界见 [ExternalVerification_F6-F9_外部验收.md](Docs/Platforms/ExternalVerification_F6-F9_外部验收.md).

本地 Phase 0 和 Candidate 默认每次写入唯一 run-id 目录，不覆盖已提交证据。Hosted CI 虽显式使用一次性 canonical 目录，artifact 仍必须上传整个目录；如果其中存在 `.publication.lock`，下载后的 reader 仍拒绝该证据。

---

## 🚀 Legacy 研究入口, 非生产验收

以下步骤只用于理解和复现现有 v4.x 研究代码. 它们不构成新 Control Plane, GBrain, Windows Edge 或真机门禁的安装说明, 也不得用于未授权账号或平台行为.

### 方式1: 现有项目升级

如果您从 v4.5.0 升级，请查看 **[CHANGELOG.md](CHANGELOG.md)** 获取版本变更详情。

### 方式2: 全新项目使用

#### 1. 环境准备

**必需**:
- ZennoDroid 7.x+ 已安装
- Android 模拟器或真机已连接
- 项目路径设置：`project_root = /absolute/path/to/DSP_ZD/`

**可选**（如需视觉纠错）:
- Gemini API 密钥（配置在 `Config/AIConfig.json`）

#### 2. 基础配置

##### 设置 ZennoDroid 变量

在 ZennoDroid 中设置：
```
project_root = /absolute/path/to/DSP_ZD/
```

##### 配置 AI（可选）

如果需要视觉纠错功能，复制 `Config/AIConfig.template.json` 为 `Config/AIConfig.json`，填写您的 API Key。

#### 3. 运行初始化

在 ZennoDroid Own Code 中运行：

```csharp
string result = Initializer.Run(project);
project.SendInfoToLog("初始化结果: " + result);
```

#### 4. 开始使用

```csharp
// 示例：点赞帖子
Intent intent = new Intent {
    Action = "like_post",
    Target = "like_button",
    Context = new Dictionary<string, string> {
        {"screen", "post_detail"}
    }
};

ZDCommand command = IntentTranslator.Translate(intent, manifestPath);
ZDResult result = ZennoDroidAdapter.Execute(command);

project.SendInfoToLog("执行结果: " + result.Status);
```

---

## 📁 目录结构

所有新模块使用唯一治理目录. 逻辑规范记作 `modules/<module-id>`, 但当前 macOS/Windows 大小写不敏感且已存在 legacy `Modules/`, 所以迁移期的真实物理目录使用 `Modules/<module-id>`. 不得尝试同时创建第二个 lowercase 目录. 模块状态以 `module.yaml` 和可执行证据为准:

```text
Modules/<module-id>/
├── AGENTS.md
├── module.yaml
├── src/
├── contracts/
│   ├── provided/
│   └── consumed/
├── tests/
├── migrations/
├── operations/
└── CHANGELOG.md
```

现有 `Core/`, loose `Modules/*.cs`, `Modules/Core/`, `ZDProjects/` 和 `Extensions/` 在迁移期作为 byte-preserved legacy runtime, 由 `legacy-runtime-adapter` 临时声明所有权, 不代表它们已经满足新模块标准. 下列目录树只是 legacy 快照:

```
DPS_v4.5/
├── Modules/                 # 业务逻辑模块
│   ├── Core/               # 核心工具（7个）
│   │   ├── Intent.cs               # 意图类定义 ⭐ NEW
│   │   ├── ZDCommand.cs           # 命令类定义 ⭐ NEW
│   │   ├── ZDResult.cs           # 结果类定义 ⭐ NEW
│   │   ├── ZennoDroidAdapter.cs   # ZennoDroid 适配器 ⭐ NEW
│   │   ├── IntentTranslator.cs    # 意图翻译器 ⭐ NEW
│   │   ├── ActionExecutor.cs      # 原执行器（兼容保留）
│   │   ├── ManifestLoader.cs      # Manifest 加载器
│   │   ├── NavigationResolver.cs  # 导航解析器
│   │   ├── VisionCorrector.cs      # 视觉纠错器（Gemini Flash）
│   │   ├── AppExplorer_v2.cs       # 自动探索 v2 ⭐ NEW
│   │   └── ...                  # 其他模块
│   ├── SessionRunner.cs       # 会话执行器
│   └── ...
├── ZDProjects/             # ZennoDroid 入口
│   ├── ModuleLoader.cs        # 动态编译加载器
│   ├── RuntimeTestRunner.cs   # 运行时测试 ⭐ NEW
│   ├── ClosedLoopTest.cs      # 闭环测试 ⭐ NEW
│   └── ...
├── Config/                 # 配置文件
│   ├── AIConfig.json         # AI 配置
│   ├── Operations/           # 操作步骤定义 JSON
│   └── ...
├── Configs/Manifests/      # APP 配置（Manifest 格式）
│   ├── manifest_schema.yaml  # Manifest Schema ⭐ NEW
│   ├── instagram_v2.yaml      # Instagram v2 格式 ⭐ NEW
│   ├── reddit_v2.yaml         # Reddit v2 格式 ⭐ NEW
│   ├── instagram.json        # 旧格式（保留兼容）
│   └── reddit.json           # 旧格式（保留兼容）
├── Tools/                  # 独立工具
│   └── app_onboarder/       # 新平台自动接入工具（Python CLI）
│       ├── main.py             # 入口（交互/命令行模式）
│       ├── adb_controller.py   # ADB 命令封装
│       ├── ui_analyzer.py      # UI Dump XML 解析引擎
│       ├── app_explorer.py     # 5 阶段自主探索引擎
│       ├── config_generator.py # 配置/操作/测试脚本生成器
│       ├── test_runner.py      # E2E 测试运行 + 自动修复
│       └── README.md           # 工具说明书
├── Docs/                   # 文档
│   ├── GETTING_STARTED.md    # 快速开始指南
│   ├── MultiPlatformFramework.md  # 多平台框架说明
│   └── ...
└── README.md               # 本文件
```

---

## 🔧 核心概念

### Intent（意图）

表示"想做什么"，例如：
- "点赞这条帖子"
- "浏览首页 Feed 5 次"
- "评论：'Hello World'"

```csharp
Intent intent = new Intent {
    Action = "like_post",
    Target = "like_button",
    Context = new Dictionary<string, string> {
        {"screen", "post_detail"},
        {"post_id", "123"}
    }
};
```

### ZDCommand（命令）

表示"怎么做"，例如：
- "在坐标 (500, 800) 点击"
- "从 (500, 1600) 滑动到 (500, 800)，持续 500ms"

```csharp
ZDCommand command = new ZDCommand {
    Action = "tap",
    X = 500,
    Y = 800
};
```

### YAML Manifest

APP 的配置文档，定义：
- **capabilities** - APP 有哪些功能
- **states** - 如何识别不同页面
- **intent_mappings** - 意图如何映射到命令

```yaml
manifest:
  version: "2.0"
  app:
    id: "instagram"
  
  capabilities:
    - name: "like_post"
      rate_limit:
        per_hour: 30
  
  intent_mappings:
    - intent: "like_post"
      zd_action: "tap"
      selector: "com.instagram.android:id/row_feed_button_like"
```

---

## 📚 文档导航

### 核心文档

| 文档 | 用途 |
|------|------|
| **[Docs/ProjectTechnicalBook_项目技术书.md](Docs/ProjectTechnicalBook_项目技术书.md)** | 换电脑、换 AI 的完整事实基线、架构、环境、边界、F0–F9 与接手协议 |
| **[Docs/ConfigGuide_配置指南.md](Docs/ConfigGuide_配置指南.md)** | 项目简介、核心概念、配置步骤 |
| **[Docs/TechManual_技术手册.md](Docs/TechManual_技术手册.md)** | Legacy 历史参考；不得据此绕过当前 fail-closed Bridge 边界 |
| **[Docs/GitWorkflow_Git工作流.md](Docs/GitWorkflow_Git工作流.md)** | Git 工作流规范 |
| **[Docs/PlatformTemplate_平台模块模板.md](Docs/PlatformTemplate_平台模块模板.md)** | 新平台模块开发模板 |

### 工具文档

| 文档 | 用途 |
|------|------|
| **Tools/app_onboarder/README.md** | App Onboarder 使用说明（新平台接入） |

### 配置文档

| 文档 | 用途 |
|------|------|
| **manifest_schema.yaml** | Manifest Schema 定义 |
| **instagram_v2.yaml** | Instagram 配置示例 |
| **reddit_v2.yaml** | Reddit 配置示例 |

### 技术文档

| 文档 | 用途 |
|------|------|
| **[Docs/EngineeringStandards_工程标准.md](Docs/EngineeringStandards_工程标准.md)** | 构建、测试、契约、安全和发布标准 |
| **[Docs/Architecture/TargetArchitecture_目标架构.md](Docs/Architecture/TargetArchitecture_目标架构.md)** | GBrain Soul memory 与 ZennoDroid 目标架构 |
| **[Docs/Platforms/](Docs/Platforms/)** | 各平台指南（BabyCenter、Reddit 等） |

---

## 🎯 使用场景

### 场景 1: 自动化 Instagram 操作

```csharp
// 1. 加载 Manifest
string manifestPath = project_root + "Configs/Manifests/instagram_v2.yaml";

// 2. 决策：浏览首页并点赞
Intent intent = new Intent {
    Action = "browse_and_like",
    Target = "feed_container",
    Context = new Dictionary<string, string> {
        {"count", "5"}  // 浏览 5 条帖子
    }
};

// 3. 翻译并执行
ZDCommand command = IntentTranslator.Translate(intent, manifestPath);
ZDResult result = ZennoDroidAdapter.Execute(command);

// 4. 验证（仅失败时）
if (result.Status != "success")
{
    VisionCorrector.VerifyError(intent, result, result.ScreenshotPath);
}
```

### 场景 2: 自动探索新 APP

```csharp
// 自动探索 Example APP
string result = AppExplorer_v2.ExploreAndGenerateManifest(
    project,
    instance,
    "com.example.app",
    project_root + "Configs/Manifests/example_v2.yaml"
);
```

### 场景 3: 添加新 APP（推荐使用 App Onboarder）

v4.5.10 起，新增独立 Python 工具自动完成接入：

```bash
# 1. 确保 ADB 已连接手机，目标 APP 已安装并登录
adb devices

# 2. 运行 App Onboarder（交互模式）
python Tools/app_onboarder/main.py

# 或命令行模式
python Tools/app_onboarder/main.py --package com.example.app --key example
```

工具自动完成：
- 探索 APP 的 UI 结构（底部导航、Feed、帖子详情、WebView）
- 生成 PlatformsConfig.json 平台条目（自动合并到现有配置）
- 生成 operations.json 操作定义
- 生成 E2E 测试脚本并自动验证

详细说明请查看 **[Tools/app_onboarder/README.md](Tools/app_onboarder/README.md)**。

> 如果不使用 App Onboarder，也可手动接入：

1. 使用 AppExplorer_v2 自动探索
2. 生成 YAML Manifest
3. 定义 capabilities 和 intent_mappings
4. 测试验证

---

## ⚡ 历史性能声明, 当前未复验

以下数字来自旧版本说明, 不属于当前 `REPOSITORY_STATIC_VERIFIED` 证据. 在可复现 benchmark, 原始结果和环境矩阵齐备前, 不得把这些数字用于发布或容量承诺.

### 快速模式（默认开启）

- **正常操作**: 0ms 额外开销（仅 ZennoDroid 执行时间）
- **失败操作**: ~2-5s（截图 + Gemini Flash 验证）
- **历史声明**: 旧文档记录为 90%+; 当前未复验

### 智能缓存

- 选择器定位结果缓存
- 状态识别结果缓存
- 避免重复的 Gemini API 调用

---

## 🔧 故障排查

### 问题1: 编译错误

**症状**: `CS0103: 当前上下文中不存在名称 'IntentTranslator'`

**解决**:
1. 检查 `ZDProjects/ModuleLoader.cs` 的 `coreFiles` 数组
2. 确认包含所有新文件
3. 重新运行 ZennoDroid 项目

### 问题2: 执行失败但未触发验证

**症状**: 操作失败但日志中无视觉验证记录

**解决**:
1. 检查快速模式状态：`ZennoDroidAdapter._fastMode`
2. 确认 `result.Status != "success"` 条件被触发
3. 查看日志中的详细错误信息

### 问题3: 性能没有提升

**症状**: 操作仍然很慢

**解决**:
1. 确认快速模式已开启
2. 检查是否有其他代码调用 `VisionCorrector.Verify`
3. 使用性能分析工具识别瓶颈

---

## 📊 版本历史

### v4.5.10 (2026-03-04) - App Onboarder 新平台自动接入工具

**新增**:
- `Tools/app_onboarder/` — 独立 Python CLI 工具（6 模块，零第三方依赖）
- 自动探索 APP UI 结构（5 阶段：首页 → 导航 → Feed → 帖子详情 → 交互按钮）
- 自动生成 PlatformsConfig.json、operations.json、E2E 测试脚本
- 测试运行器 + 5 种自动修复策略
- 支持 WebView accessibility nodes 检测、ViewPager 水平 / RecyclerView 垂直双 feed 模式

### v4.5.9 (2026-02-28) - 代码重构优化

**优化**:
- SessionRunner 帖子 JSON 构建逻辑封装
- .omo 2.0 模块追踪系统
- 文件 I/O 错误处理增强

### v4.5.8 (2026-02-27) - 稳定性复扫修复

**新增/调整**:
- 会话成功门控升级：`session_success_rate >= 95%` 且最少成功动作数（默认 6）
- SessionRunner 输出变量补齐：`session_success_rate`、`session_successful_actions`、`session_failed_actions`、`session_skipped_actions`、`action_attempt_count`
- `action_count` 语义调整为“成功动作数”
- ActionExecutor 语义字段回填增强（`post_title/post_body/...`）
- `ReportGen` 跳过状态统一为 `SKIP`
- `CoreHelper/Main/Maintenance` I/O 与清理流程容错增强

### v4.5.2 (2026-02-27) - Universal Framework

**新增**:
- 意图驱动架构（Intent, ZDCommand, ZDResult）
- ZennoDroidAdapter 执行适配器
- IntentTranslator 意图翻译器
- 快速模式（性能优化 90%+）
- YAML Manifest 格式
- AppExplorer_v2 自动探索
- ClosedLoopTest 测试套件

**优化**:
- 成功操作性能提升 90%+
- AI 调用频率降低 95%+

### v4.5.1 (2026-02-26) - 初始修复版本

**修复**:
- 11 个编译错误修复
- 导航路径和速率限制修正

### v4.5.0 (2026-02-07) - 原始版本

---

## 🤝 贡献

欢迎贡献和改进！请查看 **[GitWorkflow_Git工作流.md](Docs/GitWorkflow_Git工作流.md)** 了解协作流程。

---

## 📄 许可证

本项目遵循相应的开源许可证。详见 LICENSE 文件。

---

## 📧 联系

如有问题或建议，请提交 Issue 或 Pull Request。

**DPS - modernization and verification in progress**
