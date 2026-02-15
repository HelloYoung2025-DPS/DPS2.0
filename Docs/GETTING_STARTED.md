# DPS v4.5 新人入门指南

> **阅读时间**: 10 分钟  
> **目标**: 让你从零开始理解并运行 DPS v4.5  
> **更新**: 2026-02-13 (v4.5.1)

---

## 一、这是什么项目？

**DPS (Dynamic Persona System)** 是一个**多平台社交媒体自动化框架**，运行在 ZennoDroid 上。

### 核心功能

| 功能 | 说明 |
|------|------|
| **AI 画像生成** | 自动生成 100+ 字段的虚拟人物画像 |
| **多平台支持** | Reddit、Instagram（TikTok、Facebook 开发中） |
| **人性化行为** | 4 种行为模式，模拟真人操作 |
| **自动错误恢复** | 遇到问题自动重试，最多 3 次 |
| **记忆系统** | MemoryManager 结构化交互记录，去重 + 限额 |
| **规则引擎** | RuleEngine 帖子评估门控 |
| **统一执行引擎** | ActionExecutor JSON 步骤配置驱动 |

### 工作流程简图

```
┌─────────────┐    ┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│  初始化     │ → │  生成画像   │ → │  执行会话   │ → │  保存状态   │
│ Initializer │    │ PersonaCreate│   │ SessionRunner│   │ StateSaver  │
└─────────────┘    └─────────────┘    └─────────────┘    └─────────────┘
```

---

## 二、你需要准备什么？

### 必备软件

| 软件 | 版本要求 | 用途 |
|------|----------|------|
| ZennoDroid | 7.x 或更高 | 运行自动化脚本 |
| 文本编辑器 | 任意 | 编辑配置文件 |

### 必备知识

- 基本的 ZennoDroid 操作（创建项目、添加变量）
- 基本的 JSON 格式理解（知道 `{}` 和 `""` 是什么）

### 必备资源

- **AI API Key**（至少一个）：
  - Gemini API Key（推荐，免费额度高）
  - 或 OpenAI 兼容 API Key

---

## 三、5 分钟快速理解

### 3.1 项目结构

```
DPS_v4.5/
├── Config/              ← 配置文件（你需要编辑这里）
│   ├── Operations/      ← 平台操作步骤 JSON
│   └── Selectors/       ← UI 元素选择器 JSON
├── Modules/             ← 业务逻辑模块
│   └── Core/            ← 核心库（CoreHelper, JsonHelper, ActionExecutor 等）
├── Core/                ← ZD 运行时引擎（ScriptHelpers, UILocator 等）
├── Extensions/          ← 扩展插件
│   ├── DataSources/
│   └── Hooks/
├── ZDProjects/          ← ZennoDroid 入口代码（复制到 ZD）
├── Persons/             ← 生成的画像存储
├── Memory/              ← 交互记录（MemoryManager）
├── Data/                ← 运行数据
├── Reports/             ← 统计报告
├── Logs/                ← 日志
└── docs/                ← 文档（你在这里）
```

### 3.2 核心概念

| 概念 | 解释 | 类比 |
|------|------|------|
| **画像 (Persona)** | AI 生成的虚拟人物资料 | 就像创建一个游戏角色 |
| **阶段 (Stage)** | 人物的生命周期阶段 | TTC→T1→T2→T3→PP0→PP1→NP |
| **会话 (Session)** | 一次自动化操作周期 | 打开 APP → 操作 → 关闭 |
| **人性化 (Humanization)** | 模拟真人的随机行为 | 随机延迟、偶尔误触 |

### 3.3 执行流程

```
1. Initializer    → 检查环境，创建目录
2. Main           → 检查画像状态，决定下一步
   ├─ 无画像      → PersonaCreate（生成画像）
   ├─ 需要更新    → DailyUpdate（更新画像）
   └─ 准备就绪    → 返回 READY
3. SessionRunner  → 执行社交媒体操作
   ├─ ActionExecutor  → 统一引擎，执行 JSON 步骤
   ├─ MemoryManager   → 去重检测 + 交互记录
   └─ RuleEngine      → 帖子评估门控
4. StateSaver     → 保存状态和统计
```

---

## 四、下一步

现在你已经理解了项目的基本概念，请按顺序阅读：

| 顺序 | 文档 | 内容 |
|------|------|------|
| 1 | **[复制粘贴配置手册](CopyPaste_Setup.md)** | 一步步配置，只需复制粘贴 |
| 2 | **[快速配置流程图](QuickSetup_Flowchart.md)** | 可视化流程，快速定位问题 |
| 3 | **[术语表](GLOSSARY.md)** | 遇到不懂的词查这里 |

---

## 五、常见问题

### Q: 我需要会编程吗？
**A**: 不需要。所有代码都已写好，你只需要复制粘贴和修改配置文件。

### Q: 支持哪些平台？
**A**: 目前支持 **Reddit** 和 **Instagram**。TikTok 和 Facebook 在开发中。

### Q: API Key 从哪里获取？
**A**: 
- Gemini: https://makersuite.google.com/app/apikey
- OpenAI: https://platform.openai.com/api-keys

### Q: 运行出错怎么办？
**A**: 查看 `Logs/` 目录下的日志文件，或参考 [快速配置流程图](QuickSetup_Flowchart.md) 的故障排除部分。

---

## 六、获取帮助

如果遇到问题：

1. 先查看 [术语表](GLOSSARY.md) 确认理解正确
2. 再查看 [快速配置流程图](QuickSetup_Flowchart.md) 的故障排除
3. 检查 `Logs/` 目录下的错误日志

---

**准备好了吗？开始 → [复制粘贴配置手册](CopyPaste_Setup.md)**
