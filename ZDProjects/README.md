# DPS v4.5 ZDProjects 目录

## 概述

此目录包含 ZennoDroid 专用的 **Own Code 入口文件**。

每个文件只包含 **模块加载器**（~200行，含缓存逻辑），业务逻辑在 `Modules/` 目录中。

---

## v4.5 新特性：多平台支持

- **Reddit 模块**: 完整周期（浏览、点赞、评论、关注、分享）
- **Instagram 模块**: 完整周期 + 速率限制（60 actions/hour）
- **人性化引擎**: 4种行为配置文件
- **错误恢复**: 自动重试 + 指数退避

## v4.1 新特性：编译缓存

ModuleLoader 现在包含**静态编译缓存**：
- 首次运行：编译并缓存 (~500ms)
- 后续运行：直接使用缓存 (<10ms)
- 文件变更：自动检测并重新编译

```
┌─────────────────┐
│  Own Code 入口   │  检查缓存
└────────┬────────┘
         │
    ┌────┴────┐
    │ 缓存命中? │
    └────┬────┘
    是 ↙     ↘ 否
┌──────────┐  ┌──────────┐
│ 直接执行  │  │ 编译+缓存 │
│  <10ms   │  │  ~500ms  │
└──────────┘  └──────────┘
```

---

## 使用方法

1. **设置 ZD 变量**
   - `project_root` = `C:\DPS_v4.5` (项目根目录)
   - `device_id` = `device_001` (设备标识)
   - `current_platform` = `reddit` 或 `instagram` (v4.5 新增)

2. **复制代码到 ZD**
   - 打开对应的 `*_OwnCode.cs` 文件
   - 全选复制到 ZD 的 Own Code 动作块

3. **执行顺序**
   ```
   Initializer_OwnCode → Main_OwnCode → SessionRunner_OwnCode
   ```

---

## 文件列表

| 文件 | 功能 | 调用的模块 |
|------|------|-----------|
| `Initializer_OwnCode.cs` | 初始化 | `Modules/Initializer.cs` |
| `Main_OwnCode.cs` | 主入口 | `Modules/Main.cs` |
| `PersonaCreate_OwnCode.cs` | 创建画像 | `Modules/PersonaCreate.cs` |
| `DailyUpdate_OwnCode.cs` | 每日更新 | `Modules/DailyUpdate.cs` |
| `Extension_OwnCode.cs` | 扩展功能 | `Modules/Extension.cs` |
| `SessionRunner_OwnCode.cs` | 执行会话 | `Modules/SessionRunner.cs` |
| `StateSaver_OwnCode.cs` | 保存状态 | `Modules/StateSaver.cs` |
| `ReportGen_OwnCode.cs` | 生成报告 | `Modules/ReportGen.cs` |
| `WeeklyEvolve_OwnCode.cs` | 每周进化 | `Modules/WeeklyEvolve.cs` |
| `Maintenance_OwnCode.cs` | 系统维护 | `Modules/Maintenance.cs` |
| `RedditModule_OwnCode.cs` | Reddit 自动化 (v4.5) | `Platforms/Reddit/RedditModule.cs` |
| `InstagramModule_OwnCode.cs` | Instagram 自动化 (v4.5) | `Platforms/Instagram/InstagramModule.cs` |
| `ModuleLoader.cs` | 加载器模板 | - |

---

## 工作流程图

```
┌─────────────────┐
│  Own Code 入口   │  (~70行，ZD中执行)
│  *_OwnCode.cs   │
└────────┬────────┘
         │ 读取并编译
         ▼
┌─────────────────┐
│  业务模块       │  (外部.cs文件)
│  Modules/*.cs   │
└────────┬────────┘
         │ 依赖
         ▼
┌─────────────────┐
│  核心辅助       │
│  Core/*.cs      │
└─────────────────┘
```

---

## 修改业务逻辑

**无需重新复制到 ZD！**

直接编辑 `Modules/` 中的 .cs 文件即可，下次执行时会自动加载最新代码。

---

## 语法版本说明

| 位置 | C# 版本 | 字符串插值 |
|------|---------|-----------|
| Own Code (本目录) | ~7.0+ | ✅ 可用 |
| Modules/*.cs | ~5.0 | ❌ 禁止 |

详见 `Modules/README.md`
