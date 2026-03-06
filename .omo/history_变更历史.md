# .omo 2.0 全局变更历史
# 跨所有层级的统一变更追踪

开始日期: 2026-01-01
最后更新: 2026-02-28

---

## 快速索引

| 日期 | 层级 | 模块/操作 | 变更类型 | 描述 |
|------|------|-----------|----------|------|
| 2026-02-28 | L1 | 项目全局 | 升级 | .omo 2.0 系统激活 |
| 2026-02-27 | L2 | Intent 系统 | 功能 | v4.5.6 Intent 架构重构 |
| 2026-02-27 | L2 | 多模块 | 修复 | v4.5.7 CODEX 修复 |
| 2026-02-27 | L2 | SessionRunner | 修复 | v4.5.8 会话成功门控升级 |

---

## 2026-02-28 | .omo 2.0 系统升级

### 层级
- **L1**: 项目层
- **L2**: 模块层
- **L3**: 操作层
- **L4**: 步骤层

### 变更类型
- 新功能
- 架构升级

### 描述
激活 .omo 2.0 系统，实现 L1-L4 四层追踪和智能路由。

### 新增文件
```
.omo/
├── VERSION.md              # .omo 2.0 升级说明
├── router.yaml             # 智能路由配置
├── meta.yaml               # 项目元数据
├── history_变更历史.md     # 本文件
└── layers/
    ├── l1-project.yaml     # L1 层级定义
    ├── l2-module.yaml      # L2 层级定义
    ├── l3-operation.yaml   # L3 层级定义
    └── l4-step.yaml        # L4 层级定义
```

### 功能清单
1. **L1-L4 层级系统**
   - L1: 项目层 - 全局架构和优化
   - L2: 模块层 - 30+ 模块追踪
   - L3: 操作层 - 50+ 操作定义
   - L4: 步骤层 - 100+ 步骤记录

2. **智能路由**
   - 简单任务 → 直接执行
   - 中等任务 → Plan → Implement
   - 复杂任务 → Research → Plan → Implement
   - 层级路由、模块路由、关键词路由

3. **跨项目管理**
   - registry/ 跨项目模块注册
   - meta.yaml 项目元数据
   - 支持多项目并行开发

4. **持久化记忆**
   - history_变更历史.md 全局历史
   - 每个模块的变更记录
   - 结构图和依赖关系

### 影响
- ✅ 向后兼容，所有 v1.0 功能保留
- ✅ 为多项目并行开发提供基础
- ✅ 解决 context 清除后的记忆恢复问题

### 相关文档
- `.omo/VERSION.md` - 升级说明
- `.omo/layers/*.yaml` - 层级定义详情
- `.omo/router.yaml` - 路由规则

---

## 2026-02-27 | v4.5.8 会话成功门控升级

### 层级
- **L2**: SessionRunner 模块

### 变更类型
- Bug 修复
- 稳定性提升

### 描述
复扫修复：会话成功门控 >=95% + 最小成功动作数、语义链路补齐、I/O 容错增强。

### 修改文件
- `Modules/SessionRunner.cs`
- `Modules/Core/ActionExecutor.cs`
- `Config/IntentMappings/*.json`

### 关键变更
1. 会话成功门控从 90% 提升到 95%
2. 增加最小成功动作数检查
3. 语义字段链路完整性验证
4. I/O 操作容错增强
5. 返回码 `SKIP` 统一处理

### 相关文档
- `Docs/FIX_REPORT_2026-02-27.md`

---

## 2026-02-27 | v4.5.7 CODEX 修复

### 层级
- **L2**: 多模块

### 变更类型
- Bug 修复
- 新平台接入

### 描述
ActionExecutor 稳定性、SessionRunner 评估门控、意图映射修复、BabyCenter 接入。

### 修改文件
- `Modules/Core/ActionExecutor.cs`
- `Modules/SessionRunner.cs`
- `Platforms/BabyCenter/BabyCenterModule.cs`
- `Config/Operations/babycenter_operations.json`
- `Config/IntentMappings/babycenter_intents.json`

### 关键变更
1. ActionExecutor 重复方法删除
2. SessionRunner 评估门控优化
3. 意图映射配置修复
4. BabyCenter 平台完整接入

### 新增平台
- BabyCenter (6 种操作)

### 相关文档
- `Docs/FIX_REPORT_2026-02-27.md`

---

## 2026-02-27 | v4.5.6 Intent 架构重构

### 层级
- **L2**: Intent 系统模块

### 变更类型
- 架构重构
- 新功能

### 描述
Universal Framework：Intent/ZDCommand/ZDResult/IntentTranslator 架构完整重构。

### 新增文件
```
Modules/Core/
├── Intent.cs              # 高层操作意图抽象 (168 行)
├── ZDCommand.cs           # ZennoDroid 物理命令 (333 行)
├── ZDResult.cs            # 统一执行结果 (311 行)
├── ZennoDroidAdapter.cs   # API 适配器 (408 行)
└── IntentTranslator.cs    # 意图翻译器 (472 行)
```

### 关键变更
1. 引入 Intent 抽象分离"做什么"和"怎么做"
2. ZDCommand 封装物理命令
3. ZDResult 统一执行结果
4. IntentTranslator 实现意图翻译
5. Manifest v2.0 格式

### 设计决策
- **ADR-011**: Intent-Based Execution
- **ADR-012**: Vision Verification Layer
- **ADR-013**: Manifest v2.0 Format

### 相关文档
- `Docs/UnifiedIntentArchitecture.md`

---

## 2026-02-17 | v4.5.2 P0 级 Bug 修复

### 层级
- **L2**: 核心模块

### 变更类型
- Bug 修复
- 性能优化

### 描述
ModuleLoader 缓存优化、SessionRunner 并发安全、MemoryManager 文件锁。

### 修改文件
- `ZDProjects/ModuleLoader.cs`
- `Modules/SessionRunner.cs`
- `Modules/MemoryManager.cs`

### 关键变更
1. ModuleLoader LRU 缓存优化
2. SessionRunner 并发安全增强
3. MemoryManager 文件锁机制

---

## 统计信息

### 变更频率
- 总变更次数: 50+
- 平均每周变更: 5-10 次
- 最近活跃度: 高

### 模块变更热度
| 模块 | 变更次数 | 最近变更 |
|------|----------|----------|
| SessionRunner | 8 | 2026-02-27 |
| ActionExecutor | 6 | 2026-02-27 |
| IntentTranslator | 4 | 2026-02-27 |
| RuleEngine | 3 | 2026-02-27 |
| JsonHelper | 2 | 2026-02-13 |

### 版本发布历史
| 版本 | 日期 | 主要变更 |
|------|------|----------|
| v4.5.8 | 2026-02-27 | 会话成功门控升级 |
| v4.5.7 | 2026-02-27 | CODEX 修复 + BabyCenter |
| v4.5.6 | 2026-02-27 | Intent 架构重构 |
| v4.5.5 | 2026-02-27 | 编译错误修复 |
| v4.5.4 | 2026-02-27 | Universal APP Framework |
| v4.5.3 | 2026-02-26 | ActionExecutor 原语 |
| v4.5.2 | 2026-02-17 | P0 级 Bug 修复 |

---

## 下一步计划

### 待处理项
1. [ ] 完成 Playwright Android 测试验证
2. [ ] 接入 TikTok 平台
3. [ ] 接入 Facebook 平台
4. [ ] 实现 Hooks 扩展系统
5. [ ] 统一行为配置命名

### 优化方向
1. 性能优化 - 编译缓存命中率
2. 稳定性提升 - 视觉验证覆盖
3. 可扩展性 - 新平台接入流程

---

*此文件由 .omo 2.0 系统自动维护*
