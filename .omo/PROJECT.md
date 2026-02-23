# 项目总览

**最后更新**：2026-02-22
**项目名称**：DPS v4.5 (Dynamic Persona Simulation)
**项目类型**：C#/.NET Framework — ZennoDroid 自动化框架
**当前版本**：v4.5.2
**Git 仓库**：本地仓库

---

## 技术栈

- **语言**：C# 5.0 (Modules/) + C# 7.0+ (ZDProjects/ Own Code)
- **运行时**：.NET Framework (ZennoDroid 7.x+ 嵌入式)
- **平台**：ZennoDroid on Windows
- **编译器**：CSharpCodeProvider 动态编译 + LRU 缓存 (32 条目上限)
- **依赖**：无外部 NuGet 包
- **JSON 解析**：手写 JsonHelper.cs (993 行, 23 个方法, 栈式解析器)
- **可用引用**：System.dll, System.Core.dll, System.Data.dll, System.Xml.dll, Microsoft.CSharp.dll
- **AI 服务**：3 级 fallback — Gemini → Claude (deeprouter) → GPT (shubiaobiao)

---

## 架构原则

1. **双层代码架构** — ZD Script Layer (Func/Action 委托, 无 class 包装) + Compiled Class Layer (标准 static class)
2. **三管道执行回退** — ActionExecutor (JSON 步骤驱动) → PlatformModule (编译加载) → Independent Scripts (独立脚本)
3. **零外部依赖** — 所有功能手写实现，禁止 NuGet 包
4. **配置驱动** — 14 个 JSON 配置文件控制行为，UI 选择器从 PlatformsConfig.json 读取
5. **向后兼容** — C# 5.0 语法约束（Modules/ 层），禁止字符串插值、null 条件运算符等
6. **人格驱动行为** — 每个设备拥有独立 AI 生成画像 (100+ 字段)，影响行为偏好
7. **7 阶段生命周期** — TTC → T1 → T2 → T3 → PP0 → PP1 → NP

---

## 目录结构概览

| 目录 | 用途 | 文件数 |
|------|------|--------|
| `Config/` | JSON 配置文件 + Operations/ 子目录 | 14 + 2 |
| `Core/` | ZD 脚本层引擎 (ScriptHelpers, HumanizationEngine, UILocator, ErrorRecovery, PlatformBase) | 5 |
| `Modules/` | 业务逻辑模块 | 14 |
| `Modules/Core/` | 核心工具 (JsonHelper, AIService, ActionExecutor, CoreHelper, FileHelper 等) | 9 |
| `Platforms/` | 平台实现 (Reddit, Instagram) | 2 |
| `Extensions/` | 可插拔扩展 (DataSources: IP/Weather) | 2 + Hooks(空) |
| `ZDProjects/` | ZD Own Code 入口 + ModuleLoader + Reddit 脚本 + Tests | 16 + Tests/ |
| `Persons/` | 画像存储 (运行时数据) | — |
| `Memory/` | 行为记忆 (运行时数据) | — |
| `Docs/` | 项目文档 | 15+ |

---

## 支持的平台

| 平台 | 状态 | 模块 |
|------|------|------|
| Reddit | ✅ 完整实现 | RedditModule.cs + Reddit_*.cs 脚本 |
| Instagram | ✅ 完整实现 | InstagramModule.cs (依赖 ActionExecutor) |
| TikTok | ⏳ 配置已准备 | enabled: false |
| Facebook | ⏳ 配置已准备 | enabled: false |

---

## 关键技术参考

- **SYSTEM_BIBLE.md** — 928 行完整技术手册，项目的终极参考文档
- **C#语法版本说明.md** — C# 5.0 vs 7.0+ 语法差异对照表
- **Docs/技术白皮书.md** — 深度技术架构说明
