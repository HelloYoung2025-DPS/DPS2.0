# DPS 文档中心

> 最后更新: 2026-07-16

当前仓库正在从 legacy ZennoDroid 单体流程迁移为 `DPS Control Plane + GBrain Company + ZennoDroid Thin Executor`. 旧手册描述当前或历史实现, 新架构文档描述目标状态. 阅读时请注意文档状态标签.

## 文档导航

| 文档 | 说明 | 目标读者 |
|------|------|----------|
| [RebuildPlan_重构计划书.md](RebuildPlan_重构计划书.md) | DPS 2.0 最新 Proposed 重构设计、迁移顺序与验收合同；不代表已实施或已获验证等级 | 用户、架构师、开发与审查人员 |
| [ProjectTechnicalBook_项目技术书.md](ProjectTechnicalBook_项目技术书.md) | 换电脑、换 AI 的完整项目事实、架构、模块、环境、边界、F0–F9、门禁与接手协议 | 新 AI、架构师、开发与运维人员 |
| [ConfigGuide_配置指南.md](ConfigGuide_配置指南.md) | 新人施工文档：变量、Own Code 复制、ZennoDroid 条件分支、首次运行闭环 | 所有用户 |
| [TechManual_技术手册.md](TechManual_技术手册.md) | Legacy 历史参考；其中 SessionRunner 直接执行说明与当前 fail-closed 状态冲突，不得用来重启旧执行链 | Legacy 审计人员 |
| [GitWorkflow_Git工作流.md](GitWorkflow_Git工作流.md) | Git 工作流规范、API Key 安全规则 | 所有贡献者 |
| [PlatformTemplate_平台模块模板.md](PlatformTemplate_平台模块模板.md) | 新平台接入模板（配置驱动优先） | 开发者 |
| [EngineeringStandards_工程标准.md](EngineeringStandards_工程标准.md) | 构建、测试、CI、安全、发布和 Definition of Done | 所有贡献者 |
| [Architecture/TargetArchitecture_目标架构.md](Architecture/TargetArchitecture_目标架构.md) | GBrain Soul 记忆与 ZennoDroid 薄执行器目标架构 | 架构与开发人员 |
| [Operations/RepositoryProtection_仓库保护.md](Operations/RepositoryProtection_仓库保护.md) | Factory 治理路径, 两人审批, 受保护工作流和外部信任根要求 | 仓库与发布管理员 |
| [Operations/GBrainCompany_LocalNonProduction_本地非生产.md](Operations/GBrainCompany_LocalNonProduction_本地非生产.md) | GBrain Company 的本机 PostgreSQL, Voyage, Source/OAuth 和证据边界 | 运维与测试管理员 |
| [Operations/ExternalReview_外审机制.md](Operations/ExternalReview_外审机制.md) | 重构批次/会话/里程碑三级异构双复核外审程序（Codex + 第二异构 reviewer）与意见处置纪律 | 所有贡献者与批准者 |
| [Operations/RebuildSessionPrompts_施工会话提示词.md](Operations/RebuildSessionPrompts_施工会话提示词.md) | 重构全程零填空施工会话模板（T0 补证 + T1-T19 + 兜底），按序整块粘贴即可 | 施工发起人 |
| [Platforms/GBrainCompany_Compatibility.md](Platforms/GBrainCompany_Compatibility.md) | 本机 GBrain 0.42.42.0 Source 隔离、32 字符 Source ID、软删除与 OAuth 能力探测；仅限诊断事实 | 架构、运维与适配器开发人员 |
| [Platforms/ExternalVerification_F6-F9_外部验收.md](Platforms/ExternalVerification_F6-F9_外部验收.md) | Windows, GBrain, 真机, 灰度和规模证据输入边界 | 测试与发布管理员 |

### 快速入口

| 场景 | 直接阅读 |
|------|----------|
| 换电脑或让新的 AI 接手 | [ProjectTechnicalBook_项目技术书.md](ProjectTechnicalBook_项目技术书.md) + 根 [AGENTS.md](../AGENTS.md) |
| 审计旧 ZennoDroid 搭建方式 | [ConfigGuide_配置指南.md](ConfigGuide_配置指南.md)（仅历史/非生产审计；不得启用旧 SessionRunner 执行链） |
| 想看主执行链和真实代码结构 | [TechManual_技术手册.md](TechManual_技术手册.md) |
| 想接入一个新平台 | [PlatformTemplate_平台模块模板.md](PlatformTemplate_平台模块模板.md) + [App Onboarder README](../Tools/app_onboarder/README.md) |
| 想了解项目未来如何重构 | [RebuildPlan_重构计划书.md](RebuildPlan_重构计划书.md) + [Architecture/TargetArchitecture_目标架构.md](Architecture/TargetArchitecture_目标架构.md) |
| 想知道现代工程的验收标准 | [EngineeringStandards_工程标准.md](EngineeringStandards_工程标准.md) |
| 想配置 AI Factory 的权限分离 | [Operations/RepositoryProtection_仓库保护.md](Operations/RepositoryProtection_仓库保护.md) |
| 想配置本地 Company GBrain | [Operations/GBrainCompany_LocalNonProduction_本地非生产.md](Operations/GBrainCompany_LocalNonProduction_本地非生产.md) |
| 重构批次收尾要跑外审 | [Operations/ExternalReview_外审机制.md](Operations/ExternalReview_外审机制.md) |
| 要开新施工会话，找开工提示词 | [Operations/RebuildSessionPrompts_施工会话提示词.md](Operations/RebuildSessionPrompts_施工会话提示词.md) |
| 想核对当前 GBrain 本机能力与仍未验证项 | [Platforms/GBrainCompany_Compatibility.md](Platforms/GBrainCompany_Compatibility.md) |

### 平台指南

| 文档 | 说明 |
|------|------|
| [Platforms/BabyCenter_APP_Guide_平台指南.md](Platforms/BabyCenter_APP_Guide_平台指南.md) | BabyCenter APP 平台指南（UI 架构、WebView Accessibility、操作配置） |
| [Platforms/Reddit_TestGuide_Reddit测试指南.md](Platforms/Reddit_TestGuide_Reddit测试指南.md) | Reddit 测试指南（Own Code 脚本验证与证据清单） |

### 外部工具文档

| 文档 | 说明 |
|------|------|
| [App Onboarder README](../Tools/app_onboarder/README.md) | 新平台自动接入工具（v4.5.10+） |

---

## 推荐阅读顺序

### 新 AI 或新电脑
1. **ProjectTechnicalBook_项目技术书.md** — 先恢复当前事实、环境和安全边界
2. 根 **AGENTS.md** — 读取最高项目指令
3. 目标模块的 **AGENTS.md + module.yaml + contracts + tests + operations**
4. 当前 Legacy 入口保持 `ERROR_BRIDGE_REQUIRED`；在 F6/F7 前不得按旧指南启用 SessionRunner

### Legacy 审计
1. **ConfigGuide_配置指南.md** 和 **TechManual_技术手册.md** 只作历史实现参考
2. 不得按其中旧的最小闭环连接生产账号或设备
3. 当前执行真相以根 `AGENTS.md`、模块 Manifest、合同和可执行门禁为准

### 开发者
1. **ConfigGuide_配置指南.md** — 快速了解系统结构
2. **TechManual_技术手册.md Part 1-3** — 架构 + 核心模块 + SessionRunner
3. **TechManual_技术手册.md Part 4-6** — 平台配置 + Persona Schema + ActionExecutor
4. **TechManual_技术手册.md Part 8** — 术语表（遇到不懂的术语时查阅）

### 扩展新平台
1. **PlatformTemplate_平台模块模板.md** — 配置驱动模板
2. **TechManual_技术手册.md Part 4.4** — 添加新平台完整步骤
3. **Platforms/BabyCenter_APP_Guide_平台指南.md** — 已有平台的实现参考

---

## 目录结构

```
Docs/
├── Architecture/
│   └── TargetArchitecture_目标架构.md — GBrain 和 ZennoDroid 目标架构
├── RebuildPlan_重构计划书.md       — DPS 2.0 最新 Proposed 重构设计与验收合同
├── ProjectTechnicalBook_项目技术书.md — 换机与新 AI 接手总技术书
├── ConfigGuide_配置指南.md          — 新人施工指南
├── EngineeringStandards_工程标准.md — 工程质量和发布标准
├── Operations/
│   ├── GBrainCompany_LocalNonProduction_本地非生产.md — 本机 Company GBrain 安全边界
│   └── RepositoryProtection_仓库保护.md — Factory 权限分离与仓库保护
├── TechManual_技术手册.md           — 架构与参考手册
├── GitWorkflow_Git工作流.md         — Git 工作流
├── PlatformTemplate_平台模块模板.md — 平台模块模板
├── README.md                   — 本文件
└── Platforms/
    ├── BabyCenter_APP_Guide_平台指南.md    — BabyCenter 平台指南
    ├── GBrainCompany_Compatibility.md       — GBrain 本机兼容性探测与未验证边界
    ├── ExternalVerification_F6-F9_外部验收.md — Windows、真机、灰度与规模证据合同
    └── Reddit_TestGuide_Reddit测试指南.md  — Reddit 测试指南
```

项目更新历史: 参见根目录 [CHANGELOG.md](../CHANGELOG.md)
