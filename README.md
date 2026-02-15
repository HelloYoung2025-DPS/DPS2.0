# DPS v4.5 "Persona" - 动态人物画像系统

> **版本**: v4.5.0  
> **代号**: Persona  
> **平台**: ZennoDroid 7.x+
> **更新**: 2026-02-07

---

## 项目简介

DPS v4.5 是一个**人物画像驱动的行为决策框架**：

1. **生成和管理动态人物画像** - 100+ 字段，AI 驱动
2. **提供行为决策引擎** - 基于画像 + 阶段 + 扩展属性
3. **记录和分析用户行为** - 短期 + 长效记忆系统
4. **支持画像自然进化** - 每周深度学习用户行为

---

## 目录结构

```
DPS_v4.1/
├── Modules/             # 业务逻辑模块
│   ├── Core/            # 核心工具 (4个)
│   └── *.cs             # 10个业务模块
├── ZDProjects/          # ZD Own Code 入口
├── Config/              # 8个配置文件
├── Persons/             # 人物画像存储
├── Memory/              # 记忆数据
├── Data/                # APP特定数据
├── Logs/                # 系统日志
├── Reports/             # 统计报告
└── Docs/                # 文档
```

---

## 快速开始

1. **创建 ZD 变量** - 参考 `Docs/变量清单.md`
2. **配置 AI** - 先复制 `Config/AIConfig.template.json` 为 `Config/AIConfig.json`，再填写你的 API Key
3. **运行初始化** - 在 ZD 中运行 Initializer
4. **开始使用** - 运行 MainProject

详细步骤见 `QUICK_START.txt`

> Git 协作与版本发布建议见 `Docs/GIT_WORKFLOW.md`

---

## 7阶段生命周期

| 阶段码 | 名称 | 条件 |
|--------|------|------|
| TTC | 备孕期 | 未怀孕无孩子 |
| T1 | 孕早期 | 怀孕1-13周 |
| T2 | 孕中期 | 怀孕14-27周 |
| T3 | 孕晚期 | 怀孕28周+ |
| PP0 | 产后早期 | 孩子0-3个月 |
| PP1 | 产后中期 | 孩子4-12个月 |
| NP | 育儿期 | 孩子12个月+ |

---

## 文档列表

- `QUICK_START.txt` - 快速开始指南
- `CHECKLIST.txt` - 初始化检查清单
- `Docs/ZennoDroid配置指南.md` - ZD 项目设置
- `Docs/变量清单.md` - ZD 变量说明
- `Docs/架构说明.md` - 系统架构详解
- `Docs/流程图/` - 系统流程图

---

## 技术约束

- ❌ 无外部 DLL 依赖
- ❌ 无 .bat/.ps1 脚本
- ✅ 所有代码为 .cs 文件（运行时编译）
- ✅ ZD 变量需手动创建
- ✅ C# 5.0 语法（CSharpCodeProvider 限制）

---

## v4.5.0 新特性

### 🌐 多平台支持
- **Reddit 平台**: 完整的浏览、点赞、评论、关注、分享功能
- **Instagram 平台**: 完整的互动功能，60 actions/hour 速率限制
- **平台配置**: `Config/PlatformsConfig.json` 统一管理平台参数
- **设备映射**: `Config/device_app_mapping.json` 设备-平台映射

### 🧩 核心模块
- **HumanizationEngine**: 4 种行为配置文件 (speed_demon, casual, deep_reader, distracted)
- **UILocator**: 多策略 UI 定位 (resource-id → XPath → 图像识别)
- **ErrorRecovery**: 自动错误恢复，指数退避重试 (2s, 4s, 8s)
- **PlatformBase**: 标准化平台接口定义

### 📚 文档增强
- **GETTING_STARTED.md**: 新人入门指南
- **QuickSetup_Flowchart.md**: 快速配置流程图
- **CopyPaste_Setup.md**: 复制粘贴配置手册
- **MultiPlatformFramework.md**: 多平台框架详细文档

### 🚀 性能优化 (继承自 v4.1)
- **编译缓存**: ModuleLoader 现在缓存编译结果，第二次运行从 ~500ms 降至 <10ms
- **智能失效**: 仅当源文件变更时重新编译

### 🔧 稳定性改进 (继承自 v4.1)
- **健壮的 JSON 解析**: 完全重写的 JsonHelper，正确处理嵌套对象、转义字符、Unicode
- **API 错误检测**: AIService 现在检测并报告 API 错误响应
- **代码去重**: CoreHelper 的 JSON 方法现在委托给 JsonHelper
