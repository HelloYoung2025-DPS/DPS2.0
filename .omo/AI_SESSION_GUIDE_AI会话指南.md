# DPS v4.5 项目说明书（AI 必读）

> **每次会话开始时，AI 必须首先阅读此文件**

---

## 🚨 优先指令处理（最高优先级）

### 用户要求快速查询时，直接读取已有文件，不要重新分析：

| 用户指令 | AI 直接读取 | 禁止操作 |
|----------|------------|----------|
| "列出模块" | `.omo/layers/l2-module.yaml` | ❌ 不要分析项目 |
| "查看所有模块" | `.omo/layers/l2-module.yaml` | ❌ 不要分析项目 |
| "列出可以优化的模块" | `.omo/layers/l2-module.yaml` | ❌ 不要分析项目 |
| "查看模块列表" | `.omo/layers/l2-module.yaml` | ❌ 不要分析项目 |
| "查看操作列表" | `.omo/layers/l3-operation.yaml` | ❌ 不要分析项目 |
| "查看步骤列表" | `.omo/layers/l4-step.yaml` | ❌ 不要分析项目 |
| "查看活跃模块" | `.omo/modules/index.md` | ❌ 不要分析项目 |
| "查看项目架构" | `.omo/STRUCTURE_架构图.md` | ❌ 不要分析项目 |

### 快速查询响应格式：

```
用户: "列出可以优化的模块"

AI 直接读取 l2-module.yaml，返回格式:

## 可优化模块列表（32个）

### 核心工具模块
- JsonHelper (993行) - 高风险
- CoreHelper (470行) - 低风险
- ...

### 业务逻辑模块
- SessionRunner (1492行) - 高风险
- ...
```

---

> **每次会话开始时，AI 必须首先阅读此文件**

---

## 🎯 项目身份

| 属性 | 值 |
|------|-----|
| **项目名称** | DPS v4.5 (Dynamic Persona Simulation) |
| **项目类型** | 基于 ZennoDroid 的通用移动自动化框架 |
| **当前版本** | v4.5.9 |
| **项目路径** | 以实际工作目录为准 |
| **.omo 版本** | 2.2.0 |

---

## ⚠️ 关键约束（强制执行）

### C# 5.0 语法限制（Modules/ 目录）
**禁止使用**:
```csharp
❌ string interpolation:  $"Hello {name}"
❌ null-conditional:     user?.name
❌ nameof():              nameof(username)
❌ null-coalescing:       value ?? defaultValue
```

**必须使用**:
```csharp
✅ string.Format("Hello {0}", name)
✅ if (user != null) { var n = user.name; }
✅ "username" (硬编码字符串)
✅ value != null ? value : defaultValue
```

### 错误处理（强制）
**所有 I/O 操作必须包含 try-catch**:
```csharp
try {
    string content = FileHelper.ReadAllText(path);
    return content;
} catch (Exception ex) {
    CoreHelper.Log(TAG, "读取失败: " + ex.Message);
    return string.Empty;
}
```

---

## 📂 核心目录

```
DPS_v4.5/
├── Config/           # 14 个 JSON 配置文件（驱动行为）
├── Core/             # ZD 脚本层引擎（5 个文件）
├── Modules/          # 业务逻辑模块（14 个 .cs）
├── Modules/Core/     # 核心工具（14 个 .cs）
├── Platforms/        # 平台实现（Reddit, Instagram, BabyCenter）
├── ZDProjects/       # ZD Own Code 入口脚本
├── .omo/             # 工作流状态（本目录）
├── Persons/          # 画像存储（运行时生成）
└── Memory/           # 行为记忆（运行时生成）
```

---

## 🔥 核心模块速查

| 模块 | 文件 | 行数 | 作用 | 风险 |
|------|------|------|------|------|
| **SessionRunner** | Modules/SessionRunner.cs | 1492 | 会话执行引擎 | 高 |
| **JsonHelper** | Modules/Core/JsonHelper.cs | 993 | JSON 解析器 | 高 |
| **ActionExecutor** | Modules/Core/ActionExecutor.cs | 1401 | 操作执行器 | 高 |
| **RuleEngine** | Modules/RuleEngine.cs | 663 | 规则引擎 | 中 |
| **MemoryManager** | Modules/MemoryManager.cs | 782 | 记忆管理 | 中 |

---

## 🏗️ 架构原则

1. **Intent-Based Execution** — 高层意图翻译为物理命令
2. **三管道回退** — ActionExecutor → PlatformModule → Independent Scripts
3. **零外部依赖** — 手写 JsonHelper，禁止 NuGet 包
4. **配置驱动** — 14 个 JSON 配置文件控制所有行为
5. **人格驱动** — 每个设备有独立 AI 生成的画像

---

## 🎮 主流程

```
Initializer → Main → PersonaCreate/DailyUpdate → SessionRunner → StateSaver
```

1. **Initializer**: 创建目录、验证配置
2. **Main**: 检查画像状态，分发流程
3. **PersonaCreate**: AI 生成画像（首次）
4. **DailyUpdate**: 更新年龄/孕周/阶段
5. **SessionRunner**: 核心执行引擎
6. **StateSaver**: 持久化状态

---

## 🌐 支持的平台

| 平台 | 状态 | 操作 |
|------|------|------|
| Reddit | ✅ 完整 | Initialize, Browse, Like, Comment, Follow, Share |
| Instagram | ✅ 完整 | Initialize, Browse, Like, Comment, Follow, Share |
| BabyCenter | ✅ 完整 | Initialize, Browse, Like, Comment, Follow, Share |
| TikTok | ⏳ 待实现 | 配置已准备，模块未实现 |
| Facebook | ⏳ 待实现 | 配置已准备，模块未实现 |

---

## 📖 需要详细信息时阅读

| 需求 | 文件 |
|------|------|
| 完整技术栈 | `.omo/context.md` |
| 架构决策记录 | `.omo/decisions_架构决策.md` |
| 代码规范 | `.omo/conventions_代码规范.md` |
| L1-L4 层级定义 | `.omo/layers/*.yaml` |
| 项目架构图 | `.omo/STRUCTURE_架构图.md` |
| 模块合约 | `.omo/contracts/*.json` |

---

## 📐 L1-L4 层级系统

项目按粒度分为 4 个层级，用于精确定位任务范围：

| 层级 | 名称 | 粒度 | 数量 | 数据文件 | 示例 |
|------|------|------|------|----------|------|
| **L1** | 项目层 | 整个项目 | 1 | `layers/l1-project.yaml` | "全面优化项目性能"、"接入 TikTok 平台" |
| **L2** | 模块层 | 单个 .cs 文件 | 32 | `layers/l2-module.yaml` | "优化 SessionRunner"、"重构 JsonHelper" |
| **L3** | 操作层 | 模块内的业务操作 | 51 | `layers/l3-operation.yaml` | "修复 parse-rule 操作"、"优化 apply-fatigue-model" |
| **L4** | 步骤层 | 单个方法/代码块 | 100+ | `layers/l4-step.yaml` | "修复 get-json-value 步骤" |

### 层级关系

```
L1 项目层（DPS v4.5 整体）
 └── L2 模块层（32 个模块，如 SessionRunner、JsonHelper）
      └── L3 操作层（每个模块 3-8 个操作，如 execute-action-sequence）
           └── L4 步骤层（每个操作 2-5 个步骤，如 create-droid-instance）
```

### 使用方式

用户可以在任何层级发起任务，AI 自动匹配对应层级：

```
"全面优化项目性能"      → L1 项目层（多模块协同）
"优化 SessionRunner"   → L2 模块层（单模块完整重构）
"修复 parse-rule 操作"  → L3 操作层（模块内局部修复）
"修复 get-json-value"   → L4 步骤层（单方法/代码块修复）
```

### L2 模块分类概览（32 个模块）

| 分类 | 数量 | 代表模块 |
|------|------|----------|
| 核心工具 | 9 | JsonHelper (993行)、CoreHelper、FileHelper、AIService、ActionExecutor (1401行) |
| Intent 系统 | 5 | Intent、ZDCommand、ZDResult、ZennoDroidAdapter、IntentTranslator |
| 业务逻辑 | 13 | SessionRunner (1492行)、RuleEngine、MemoryManager、Main、PersonaCreate |
| 平台模块 | 3 | RedditModule、InstagramModule、BabyCenterModule |
| ZD 引擎 | 5 | ScriptHelpers、HumanizationEngine、UILocator、ErrorRecovery、PlatformBase |

> **完整模块清单**：`layers/l2-module.yaml`（含行数、风险、依赖、合约路径）
> **操作清单**：`layers/l3-operation.yaml`（含入口方法、步骤引用）

---

## 🔄 工作流程（.omo 2.0）

### 常规任务流程
1. **读取此文件**（AI_SESSION_GUIDE_AI会话指南.md）
2. **读取 context.md** 了解技术栈
3. **读取 conventions_代码规范.md** 了解代码规范
4. **根据任务层级** 读取对应的 layers/*.yaml
5. **执行开发任务**
6. **更新 history_变更历史.md** 记录变更

### 模块修改流程（无缝衔接）

当需要**修改模块**时，系统自动创建模块追踪文件：

1. **开始修改**
   - 在 `.omo/modules/{{ModuleName}}.md` 创建追踪文件
   - 更新 `l2-module.yaml` 中模块状态为 `modifying`
   - 记录修改目标和影响范围

2. **修改过程中**
   - 实时更新追踪文件的进度
   - 记录 L2/L3/L4 层级变更
   - 记录依赖影响

3. **会话结束前**
   - 更新"下次会话继续点"
   - 更新 `.omo/modules/index.md`
   - 保存当前状态

4. **下次会话恢复**
   - 读取 `.omo/modules/{{ModuleName}}.md`
   - 定位"下次会话继续点"
   - 无缝继续工作

**详细说明**: 参阅 `.omo/modules/WORKFLOW.md`

---

## 🔧 模块修改追踪

如果正在修改模块，查看：
- `.omo/modules/index.md` — 查看活跃模块
- `.omo/modules/{{ModuleName}}.md` — 查看模块修改记录
- `.omo/modules/WORKFLOW.md` — 查看完整工作流

---

## 🚨 已知问题

1. **Extensions/Hooks/** 目录为空 — 钩子机制未实现
2. **行为配置命名不一致** — 代码使用 `speed_demon`，部分文档写 `active`
3. **TikTok/Facebook** 模块未实现

---

## 📌 版本历史速查

| 版本 | 日期 | 核心变更 |
|------|------|----------|
| v4.5.9 | 2026-03-03 | SessionRunner 编译修复、BuildPostJson 封装、L3 op-sr-008、.omo 文档精简 |
| v4.5.8 | 2026-02-27 | 会话成功门控 >=95%、语义字段链路增强 |
| v4.5.7 | 2026-02-27 | BabyCenter 接入、Playwright 测试 |
| v4.5.6 | 2026-02-27 | Intent 系统重构（Intent/ZDCommand/ZDResult） |

---

## 👋 新人快速入门

### 你只需要做一件事：跟 AI 说话

| 你说什么 | AI 自动做什么 |
|----------|-------------|
| "查看所有模块" | 读取 `l2-module.yaml`，返回 32 个模块列表 |
| "优化 [模块名]" | 创建追踪文件 → 分析 → 优化 → 保存状态 |
| "继续 [模块名]" | 读取追踪文件 → 定位继续点 → 无缝继续 |
| "查看活跃模块" | 读取 `modules/index.md`，返回正在修改的模块 |
| "查看项目架构" | 读取 `STRUCTURE_架构图.md`，返回架构图 |

### AI 自动读取的文件（你不需要手动操作）

```
AI_SESSION_GUIDE_AI会话指南.md  → 项目概况和约束（本文件）
context.md           → 完整技术栈和目录结构
conventions_代码规范.md       → C# 5.0 代码规范
layers/*.yaml        → 模块/操作/步骤定义
contracts/*.json     → 模块接口合约
modules/index.md     → 活跃模块状态
```

### 关键约束（必须知道）

- **C# 5.0**：`Modules/` 下禁止 `$""`、`?.`、`nameof`
- **配置同步**：`ZDProjects/*_OwnCode.cs` 改完需手动复制到 ZennoDroid 项目
- **所有 I/O 必须 try-catch**：无裸文件读写

---

**⚡ AI 开始工作前，请确认已阅读并理解以上内容！**

---

*最后更新: 2026-03-03*
