# DPS v4.5 文档中心

> 最后更新: 2026-03-05（文档整合重组）

## 文档导航

| 文档 | 说明 | 目标读者 |
|------|------|----------|
| [ConfigGuide_配置指南.md](ConfigGuide_配置指南.md) | 项目简介、核心概念、配置步骤、系统流程图、故障排除 | 所有用户 |
| [TechManual_技术手册.md](TechManual_技术手册.md) | 系统架构、核心模块、SessionRunner、平台配置、Persona Schema、ActionExecutor 原语、测试方案、术语表 | 开发者 |
| [GitWorkflow_Git工作流.md](GitWorkflow_Git工作流.md) | Git 工作流规范、API Key 安全规则 | 所有贡献者 |
| [PlatformTemplate_平台模块模板.md](PlatformTemplate_平台模块模板.md) | 新平台模块开发模板 | 开发者 |

### 平台指南

| 文档 | 说明 |
|------|------|
| [Platforms/BabyCenter_APP_Guide_平台指南.md](Platforms/BabyCenter_APP_Guide_平台指南.md) | BabyCenter APP 平台指南（UI 架构、WebView Accessibility、操作配置） |

### 外部工具文档

| 文档 | 说明 |
|------|------|
| [App Onboarder README](../Tools/app_onboarder/README.md) | 新平台自动接入工具（v4.5.10+） |

---

## 推荐阅读顺序

### 新手用户
1. **ConfigGuide_配置指南.md** — 了解项目、按步骤配置

### 开发者
1. **ConfigGuide_配置指南.md** — 快速了解系统结构
2. **TechManual_技术手册.md Part 1-3** — 架构 + 核心模块 + SessionRunner
3. **TechManual_技术手册.md Part 4-6** — 平台配置 + Persona Schema + ActionExecutor
4. **TechManual_技术手册.md Part 8** — 术语表（遇到不懂的术语时查阅）

### 扩展新平台
1. **PlatformTemplate_平台模块模板.md** — 模板参考
2. **TechManual_技术手册.md Part 4.4** — 添加新平台完整步骤
3. **Platforms/BabyCenter_APP_Guide_平台指南.md** — 已有平台的实现参考

---

## 目录结构

```
Docs/
├── ConfigGuide_配置指南.md     — 配置/可视化指南（合并自 3 个文档）
├── TechManual_技术手册.md      — 技术手册（合并自 7 个文档）
├── GitWorkflow_Git工作流.md         — Git 工作流
├── PlatformTemplate_平台模块模板.md — 平台模块模板
├── README.md                   — 本文件
└── Platforms/
    └── BabyCenter_APP_Guide_平台指南.md — BabyCenter 平台指南
```

项目更新历史: 参见根目录 [CHANGELOG.md](../CHANGELOG.md)
