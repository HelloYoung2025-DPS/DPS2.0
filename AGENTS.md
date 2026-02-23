# DPS v4.5 Project Rules

## .omo 工作流已激活

此项目使用 OhMyOpenCode 工作流标准。在执行任何实现之前，你必须遵循以下规则。

### 强制性工作流

1. 使用 Read 工具读取 `.omo.conf` 配置文件获取项目约束
2. 读取 `.omo/` 目录中的状态文件（context.md, decisions.md, conventions.md）
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
@.omo/decisions.md — 架构决策记录
@.omo/conventions.md — 代码约定

如果 `~/.omo/global-hooks/pre-task.md` 存在，也必须读取以加载完整工作流协议。