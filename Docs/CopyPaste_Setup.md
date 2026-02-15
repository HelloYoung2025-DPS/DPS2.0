# DPS v4.5 复制粘贴配置手册

> **目标**: 按照本手册一步步操作，只需复制粘贴即可完成配置  
> **预计时间**: 15-20 分钟  
> **难度**: ⭐ 入门级  
> **更新**: 2026-02-14 (v4.5.2)

---

## 📋 配置检查清单

在开始之前，请确认：

- [ ] 已安装 ZennoDroid 7.x 或更高版本
- [ ] 已下载 DPS_v4.5 项目文件夹
- [ ] 已获取至少一个 AI API Key

---

## 第一步：设置项目路径

### 1.1 确定项目位置

将 `DPS_v4.5` 文件夹放到你想要的位置，例如：

```
C:\DPS_v4.5\
```

> ⚠️ **重要**: 路径中不要有中文或空格！

### 1.2 记录你的项目路径

我的项目路径是：`________________________`（填写后继续）

---

## 第二步：配置 AI 服务

### 2.1 打开配置文件

用文本编辑器打开：
```
你的项目路径\Config\AIConfig.json
```

### 2.2 修改 API Key

找到以下内容并替换为你的 API Key：

```json
{
    "version": "1.0",
    "description": "DPS v4.5 AI服务配置",
    "models": {
        "primary": {
            "provider": "gemini",
            "model": "gemini-3-flash-preview",
            "api_key": "在这里填入你的Gemini API Key",
            "base_url": "https://generativelanguage.googleapis.com/v1beta",
            "timeout_ms": 60000,
            "max_tokens": 8192,
            "temperature": 0.7
        },
        "fallback": {
            "provider": "openai",
            "model": "claude-sonnet-4-5-thinking",
            "api_key": "在这里填入你的备用API Key（可选）",
            "base_url": "https://deeprouter.top/v1",
            "timeout_ms": 60000,
            "max_tokens": 4096,
            "temperature": 0.7
        },
        "backup": {
            "provider": "openai",
            "model": "gpt-5.2-2025-12-11",
            "api_key": "在这里填入第三备用API Key（可选）",
            "base_url": "https://api.shubiaobiao.com/v1",
            "timeout_ms": 30000,
            "max_tokens": 2048,
            "temperature": 0.7
        }
    },
    "retry_config": {
        "max_retries": 3,
        "retry_delay_ms": 1000,
        "backoff_multiplier": 2.0
    },
    "usage_limits": {
        "daily_request_limit": 1000,
        "hourly_request_limit": 100,
        "cost_alert_threshold_usd": 10.0
    }
}
```

> 💡 **提示**: 只需要配置 `primary`，`fallback` 和 `backup` 是可选的备用方案。

### 2.3 保存文件

按 `Ctrl + S` 保存。

---

## 第三步：配置设备映射

### 3.1 打开配置文件

用文本编辑器打开：
```
你的项目路径\Config\device_app_mapping.json
```

### 3.2 添加你的设备

复制以下模板，修改设备 ID 和平台：

```json
{
    "version": "1.0",
    "description": "Maps devices to social media platforms for multi-platform automation",
    "devices": {
        "你的设备ID": {
            "platform": "reddit",
            "device_name": "你的设备名称",
            "notes": "备注信息"
        }
    },
    "default_platform": "reddit"
}
```

**平台选项**:
| 平台值 | 说明 |
|--------|------|
| `reddit` | Reddit 自动化 |
| `instagram` | Instagram 自动化 |

**示例**（多设备）:
```json
{
    "version": "1.0",
    "description": "Maps devices to social media platforms for multi-platform automation",
    "devices": {
        "R58M4816G8Y": {
            "platform": "reddit",
            "device_name": "Samsung S21",
            "notes": "主力 Reddit 设备"
        },
        "PIXEL6PRO001": {
            "platform": "instagram",
            "device_name": "Pixel 6 Pro",
            "notes": "Instagram 设备"
        }
    },
    "default_platform": "reddit"
}
```

### 3.3 保存文件

按 `Ctrl + S` 保存。

---

## 第四步：在 ZennoDroid 中创建变量

### 4.1 打开 ZennoDroid

启动 ZennoDroid，创建一个新项目或打开现有项目。

### 4.2 创建必填变量（3个）

在 ZennoDroid 的「项目变量」面板中，右键 → 添加变量：

| 变量名 | 类型 | 初始值 | 说明 |
|--------|------|--------|------|
| `project_root` | String | `C:\DPS_v4.5\` | ⚠️ 必须以 `\` 结尾 |
| `device_id` | String | `你的设备ID` | 与 device_app_mapping.json 中一致 |
| `current_app` | String | `reddit` | 当前运行的平台 |

### 4.3 创建模块结果变量（8个）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `initializer_result` | String | （留空） |
| `main_result` | String | （留空） |
| `persona_result` | String | （留空） |
| `daily_result` | String | （留空） |
| `session_result` | String | （留空） |
| `report_result` | String | （留空） |
| `evolve_result` | String | （留空） |
| `extension_result` | String | （留空） |

### 4.4 创建配置缓存变量（5个）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `ai_config_json` | String | （留空） |
| `stage_config_json` | String | （留空） |
| `behavior_config_json` | String | （留空） |
| `persona_json` | String | （留空） |
| `session_plan_json` | String | （留空） |

### 4.5 创建控制变量（1个）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `force_regenerate` | String | `false` |

### 4.6 创建状态变量（8个）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `last_error` | String | （留空） |
| `run_result` | String | （留空） |
| `action_count` | String | `0` |
| `effective_date` | String | （留空） |
| `current_action` | String | （留空） |
| `current_post_id` | String | （留空） |
| `current_platform` | String | `reddit` |
| `_init_status` | String | （留空） |
| `_config_ok` | String | （留空） |
| `_module_ok` | String | （留空） |

### 4.7 创建画像变量（3个）

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `pregnancy_status` | String | （留空） |
| `special_requirements` | String | （留空） |
| `_config_missing` | String | （留空） |

> ✅ **检查点**: 你现在应该有 **30 个变量**

### 4.8 创建 Selector 变量（v4.5.1 新增，7个）

以下变量由 `RedditModule.cs` 初始化时自动设置，也可手动创建：

| 变量名 | 类型 | 初始值 |
|--------|------|--------|
| `reddit_sel_post_unit` | String | `post_unit` |
| `reddit_sel_post_footer` | String | `post_footer` |
| `reddit_sel_upvote_button` | String | `post_footer_first_child` |
| `reddit_sel_comment_button` | String | `comment_button` |
| `reddit_sel_submit_button` | String | `submit_comment` |
| `reddit_sel_follow_button` | String | `follow_button` |
| `reddit_sel_share_button` | String | `share_button` |

> 💡 这些变量通常由平台模块自动从 `PlatformsConfig.json` 读取并设置，无需手动填写。

---

## 第五步：创建 Own Code 动作块

### 5.1 创建 Initializer 动作块

1. 在 ZennoDroid 项目中，添加一个 **Own Code** 动作块
2. 命名为 `Initializer`
3. 复制 `ZDProjects\Initializer_OwnCode.cs` 文件的**全部内容**到动作块中

> ⚠️ **重要**: 直接打开并复制整个文件内容，不要手动输入！文件路径：
> ```
> 你的项目路径\ZDProjects\Initializer_OwnCode.cs
> ```

### 5.2 创建 Main 动作块

1. 添加另一个 **Own Code** 动作块
2. 命名为 `Main`
3. 复制 `ZDProjects\Main_OwnCode.cs` 文件的**全部内容**到动作块中

> ⚠️ 文件路径：`你的项目路径\ZDProjects\Main_OwnCode.cs`

### 5.3 创建 SessionRunner 动作块

1. 添加另一个 **Own Code** 动作块
2. 命名为 `SessionRunner`
3. 复制 `ZDProjects\SessionRunner_OwnCode.cs` 文件的**全部内容**到动作块中

> ⚠️ 文件路径：`你的项目路径\ZDProjects\SessionRunner_OwnCode.cs`

> 💡 **提示**: 其他模块的 OwnCode 同理，每个都对应 `ZDProjects/` 下的 `*_OwnCode.cs` 文件。
> 需要创建的动作块总共 **10 个**：Initializer, Main, SessionRunner, DailyUpdate, Extension, Maintenance, PersonaCreate, ReportGen, StateSaver, WeeklyEvolve

---

## 第六步：设置执行流程

### 6.1 创建主流程

在 ZennoDroid 中按以下顺序连接动作块：

```
┌─────────────────┐
│   开始          │
└────────┬────────┘
         ▼
┌─────────────────┐
│  Initializer    │  ← 检查环境
└────────┬────────┘
         ▼
┌─────────────────┐
│  检查结果       │  ← 判断 initializer_result
└────────┬────────┘
         │
    ┌────┴────┐
    ▼         ▼
 SUCCESS    ERROR → 结束
    │
    ▼
┌─────────────────┐
│     Main        │  ← 检查画像状态
└────────┬────────┘
         ▼
┌─────────────────┐
│  检查结果       │  ← 判断 main_result
└────────┬────────┘
         │
    ┌────┼────┐
    ▼    ▼    ▼
 READY  NEED  ERROR → 结束
    │   CREATE
    │    │
    │    ▼
    │  PersonaCreate（自动触发）
    │    │
    ▼    ▼
┌─────────────────┐
│  SessionRunner  │  ← 执行社交媒体操作
└────────┬────────┘
         ▼
┌─────────────────┐
│     结束        │
└─────────────────┘
```

### 6.2 添加条件判断

在 `Initializer` 后添加条件判断：
- 条件：`{-Variable.initializer_result-}` 包含 `SUCCESS`
- 是 → 继续到 Main
- 否 → 结束

在 `Main` 后添加条件判断：
- 条件：`{-Variable.main_result-}` 包含 `READY`
- 是 → 继续到 SessionRunner
- 否 → 结束（或处理其他状态）

---

## 第七步：首次运行测试

### 7.1 运行 Initializer

1. 在 ZennoDroid 中运行 `Initializer` 动作块
2. 检查日志输出

**预期结果**:
```
[Initializer] 加载模块: C:\DPS_v4.5\Modules\Initializer.cs
[Initializer] 目录检查完成
[Initializer] 配置文件检查完成
SUCCESS: 初始化完成
```

### 7.2 运行 Main

1. 运行 `Main` 动作块
2. 检查日志输出

**首次运行预期结果**:
```
[Main] 加载模块: C:\DPS_v4.5\Modules\Main.cs
[Main] 画像不存在，需要创建
NEED_CREATE_PERSONA
```

### 7.3 检查生成的画像

运行后检查 `Persons/` 目录，应该有一个以你的 `device_id` 命名的 JSON 文件。

---

## 🎉 配置完成！

恭喜！你已经完成了 DPS v4.5 的基本配置。

### 下一步

- 查看 [快速配置流程图](QuickSetup_Flowchart.md) 了解完整流程
- 遇到问题查看 [术语表](GLOSSARY.md)
- 深入了解查看 [技术白皮书](技术白皮书.md)

---

## 常见问题排查

### 问题 1: "模块不存在"

**原因**: `project_root` 路径设置错误

**解决**:
1. 检查 `project_root` 变量值
2. 确保路径以 `\` 结尾
3. 确保路径指向正确的 DPS_v4.5 目录

### 问题 2: "编译错误"

**原因**: 代码复制不完整或有格式问题

**解决**:
1. 重新复制完整代码
2. 确保没有多余的空格或换行
3. 检查 ZennoDroid 日志中的具体错误行号

### 问题 3: "API 调用失败"

**原因**: API Key 无效或网络问题

**解决**:
1. 检查 `AIConfig.json` 中的 API Key
2. 测试网络连接
3. 尝试使用备用 API

### 问题 4: "变量未设置"

**原因**: ZennoDroid 变量未创建或名称错误

**解决**:
1. 对照第四步检查所有变量
2. 变量名区分大小写
3. 确保必填变量有初始值

---

## 变量快速参考表

### 必须设置的变量（3个）

| 变量名 | 示例值 | 说明 |
|--------|--------|------|
| `project_root` | `C:\DPS_v4.5\` | 必须以 `\` 结尾 |
| `device_id` | `R58M4816G8Y` | 你的设备 ID |
| `current_app` | `reddit` | reddit 或 instagram |

### 其他变量（25个）

全部创建为 String 类型，初始值留空即可。系统运行时会自动填充。

---

**配置完成后，你的 ZennoDroid 项目应该可以正常运行 DPS v4.5 了！**
