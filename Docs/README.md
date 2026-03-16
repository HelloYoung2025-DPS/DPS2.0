# DPS v4.5 文档中心

> 最后更新: 2026-03-07（文档体系优化）

## 文档导航

| 文档 | 说明 | 目标读者 |
|------|------|----------|
| [ConfigGuide_配置指南.md](ConfigGuide_配置指南.md) | 新人施工文档：变量、Own Code 复制、ZennoDroid 条件分支、首次运行闭环 | 所有用户 |
| [TechManual_技术手册.md](TechManual_技术手册.md) | 架构与参考文档：主执行链、模块、平台配置、ActionExecutor、测试方案、术语表 | 开发者 |
| [GitWorkflow_Git工作流.md](GitWorkflow_Git工作流.md) | Git 工作流规范、API Key 安全规则 | 所有贡献者 |
| [PlatformTemplate_平台模块模板.md](PlatformTemplate_平台模块模板.md) | 新平台接入模板（配置驱动优先） | 开发者 |

### 快速入口

| 场景 | 直接阅读 |
|------|----------|
| 第一次在 ZennoDroid 里搭建 DPS | [ConfigGuide_配置指南.md](ConfigGuide_配置指南.md)（先看“第六步”与“4.2 首次运行流程”） |
| 想看主执行链和真实代码结构 | [TechManual_技术手册.md](TechManual_技术手册.md) |
| 想接入一个新平台 | [PlatformTemplate_平台模块模板.md](PlatformTemplate_平台模块模板.md) + [App Onboarder README](../Tools/app_onboarder/README.md) |

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

### 新手用户
1. **ConfigGuide_配置指南.md** — 先按施工步骤搭建 ZennoDroid 项目
2. 按 `ConfigGuide` 中的最小闭环跑通 `Initializer -> Main -> PersonaCreate -> Main -> SessionRunner`
3. 不要直接把 `ConfigGuide` 里 `4.3 / 4.4` 的内部逻辑图拆成新的 ZennoDroid 动作块

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
├── ConfigGuide_配置指南.md          — 新人施工指南
├── TechManual_技术手册.md           — 架构与参考手册
├── GitWorkflow_Git工作流.md         — Git 工作流
├── PlatformTemplate_平台模块模板.md — 平台模块模板
├── README.md                   — 本文件
└── Platforms/
    ├── BabyCenter_APP_Guide_平台指南.md    — BabyCenter 平台指南
    └── Reddit_TestGuide_Reddit测试指南.md  — Reddit 测试指南
```

项目更新历史: 参见根目录 [CHANGELOG.md](../CHANGELOG.md)
