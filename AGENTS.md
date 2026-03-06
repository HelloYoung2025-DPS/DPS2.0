# DPS v4.5 Project Rules

## .omo 工作流已激活

此项目使用 OhMyOpenCode 工作流标准。在执行任何实现之前，你必须遵循以下规则。

### 强制性工作流

1. 使用 Read 工具读取 `.omo.conf` 配置文件获取项目约束
2. 读取 `.omo/` 目录中的状态文件（context.md, decisions_架构决策.md, conventions_代码规范.md）
3. 加载 `~/.omo/global-hooks/pre-task.md` 工作流协议
4. 对于非简单任务，创建 `.omo/current-task/plan.md` 并等待用户批准
5. 所有变更记录到 CHANGELOG.md

### 项目约束 (来自 .omo 配置)

- **工作流**: Plan-first（中等/复杂任务必须先制定计划）
- **代码质量**: 所有 I/O 操作必须添加错误处理
- **架构原则**: 遵循现有代码库模式
- **向后兼容性**: 必须维护
- **变更记录**: 所有修改记录到 CHANGELOG.md

**注意**: 特定技术约束（如 C# 5.0 语法限制、.NET 4.5 框架要求）由开发环境决定，会触发相应的 SKILLS，不是 .omo 规范的核心要求。

### External File Loading

CRITICAL: 在每个编程任务开始时，读取以下文件：

@.omo.conf — 项目约束配置（语言、版本、平台、代码标准）
@.omo/context.md — 项目上下文（技术栈、架构、目录结构）
@.omo/decisions_架构决策.md — 架构决策记录
@.omo/conventions_代码规范.md — 代码约定
@Docs/DOCS_RULES.md — Docs/ 目录约束规则（禁止新建文件、命名规范）

如果 `~/.omo/global-hooks/pre-task.md` 存在，也必须读取以加载完整工作流协议。

### 根目录 .md 文件控制规则

根目录仅允许以下 .md 文件存在：

```
DPS_v4.5/
├── AGENTS.md          # AI 工具链配置（OpenCode 自动加载）
├── README.md          # 项目主页入口
└── CHANGELOG.md       # 版本变更记录（.omo.conf 要求）
```

**禁止在根目录新建任何 .md 文件**，包括但不限于：
- `*_REPORT.md` — AI 工作报告 → 存到 `.omo/history/`
- `*_GUIDE.md` / `*_PLAN.md` — 技术文档 → 合并到 `Docs/` 目录现有文件
- `*_INDEX.md` — 索引/导航 → 合并到 `.omo/AI_SESSION_GUIDE_AI会话指南.md`
- `*_Checklist.md` — 检查清单 → 合并到 `Docs/` 对应文档

**文档归属原则**:

| 文档类型 | 存放位置 | 说明 |
|---------|---------|------|
| 项目入口 | 根目录 `README.md` | 唯一的根目录文档入口 |
| 版本记录 | 根目录 `CHANGELOG.md` | .omo 工作流要求 |
| AI 工具配置 | 根目录 `AGENTS.md` | 工具链硬依赖 |
| 技术文档 | `Docs/` | 持久性技术文档，遵循 `Docs/DOCS_RULES.md` |
| 平台指南 | `Docs/Platforms/` | 按平台组织 |
| AI/工作流上下文 | `.omo/` | AI 会话恢复、架构决策、代码规范 |
| AI 工作报告 | `.omo/history/` | 一次性报告，按日期归档 |
| 子目录说明 | 各目录 `README.md` | 仅说明该目录用途 |

**AI 助手必须遵守**:
1. 不在根目录创建新 .md 文件
2. 任务报告写入 `.omo/history/{date}-{task}/` 目录
3. 技术文档更新合并到 `Docs/` 现有文件
4. 如确需新建根目录文件，必须先向用户提出请求并等待批准