# DPS v4.5 "Universal Framework" - 通用 APP 自动化框架

> **版本**: v4.5.10  
> **更新**: 2026-03-04  
> **平台**: ZennoDroid 7.x+  
> **架构**: 意图驱动 + JSON 操作编排 + 按需视觉验证

---

## 🎯 项目简介

DPS v4.5 是一个**通用 APP 自动化框架**，通过 **意图驱动**、**ActionExecutor + operations.json** 和 **Manifest 配置**实现多平台自动化操作。

### 核心特性

- ✅ **意图驱动架构** - 清晰的"大脑-手"分层
- ✅ **统一操作编排** - 多 APP 复用同一套 ActionExecutor + operations.json 能力
- ✅ **通用 APP 支持** - 通过 YAML 配置添加新 APP
- ✅ **自动探索能力** - 自动分析 APP 结构生成 Manifest
- ✅ **按需视觉纠错** - 仅在异常时触发 AI 验证（性能优化 90%+）
- ✅ **拟人化执行** - 随机延迟、贝塞尔曲线滑动
- ✅ **多设备并发** - 同时控制多部手机
- ✅ **会话成功门控** - v4.5.8 起要求有效成功率 `>=95%` 且最少成功动作数达标

### 架构亮点

```
┌─────────────────────────────────────────────────────────┐
│ DPS (大脑) - 决策层                                       │
│                                                         │
│ 1. 感知（Gemini Flash 视觉分析）                            │
│ 2. 记忆（历史行为、兴趣偏好）                                 │
│ 3. 决策（规则引擎 + 人格模型）                               │
│ 4. 验证（执行结果视觉检查）                                   │
└────────────────────────┬────────────────────────────────┘
                         │ Intent (意图)
                         ▼
┌─────────────────────────────────────────────────────────┐
│ ZennoDroid (手) - 执行层                                     │
│                                                         │
│ • 接收命令（坐标、滑动参数）                                │
│ • 元素定位（XML 路径、resource-id）                         │
│ • 拟人化执行（曲线、随机延迟）                                 │
│ • 异常处理（超时、弹窗、网络错误）                             │
└─────────────────────────────────────────────────────────┘
```

---

## 🚀 快速开始

### 方式1: 现有项目升级

如果您从 v4.5.0 升级，请查看 **[CHANGELOG.md](CHANGELOG.md)** 获取版本变更详情。

### 方式2: 全新项目使用

#### 1. 环境准备

**必需**:
- ZennoDroid 7.x+ 已安装
- Android 模拟器或真机已连接
- 项目路径设置：`project_root = /path/to/DPS_v4.5/`

**可选**（如需视觉纠错）:
- Gemini API 密钥（配置在 `Config/AIConfig.json`）

#### 2. 基础配置

##### 设置 ZennoDroid 变量

在 ZennoDroid 中设置：
```
project_root = /Users/hofishvn/openCode_Projects/DPS_v4.5/
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
| **[Docs/ConfigGuide_配置指南.md](Docs/ConfigGuide_配置指南.md)** | 项目简介、核心概念、配置步骤 |
| **[Docs/TechManual_技术手册.md](Docs/TechManual_技术手册.md)** | 系统架构、核心模块、测试方案、术语表 |
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
| **[.omo/conventions_代码规范.md](.omo/conventions_代码规范.md)** | C# 语法约束、命名规范、代码模式 |
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

## ⚡ 性能特性

### 快速模式（默认开启）

- **正常操作**: 0ms 额外开销（仅 ZennoDroid 执行时间）
- **失败操作**: ~2-5s（截图 + Gemini Flash 验证）
- **性能提升**: 90%+ 相比每次验证版本

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

欢迎贡献和改进！请查看 **[GIT_WORKFLOW.md](Docs/GIT_WORKFLOW.md)** 了解协作流程。

---

## 📄 许可证

本项目遵循相应的开源许可证。详见 LICENSE 文件。

---

## 📧 联系

如有问题或建议，请提交 Issue 或 Pull Request。

**DPS v4.5 - 让 APP 自动化更简单** 🚀
