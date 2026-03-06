# App Onboarder -- 新平台自动接入工具

> 连接 Android 设备，自动探索未知 APP 的 UI 结构，一键生成 DPS v4.5 所需的全部配置文件。

## 这个工具是什么？

DPS v4.5 每接入一个新 APP 平台，都需要手动配置大量参数：包名、UI 选择器、滑动坐标、操作按钮定义、页面签名......过程繁琐、容易出错、耗时长。

App Onboarder 把这个过程自动化了。它通过 ADB 连接你的 Android 设备，对目标 APP 做一轮完整的 UI 探索，然后根据探索结果自动生成三样东西：

1. **PlatformsConfig.json 平台条目** -- 合并到现有配置文件中
2. **{platform}_operations.json 操作定义** -- 描述 APP 内各种交互动作
3. **{platform}_e2e_test.ps1 测试脚本** -- 验证生成的配置是否正确

生成完配置后，工具还能自动运行 E2E 测试，如果测试失败，它会尝试自动修复配置（调整等待时间、切换选择器、修正坐标等），最多重试 3 次。


## 前置条件

- **Python 3.x** -- 标准库即可，不需要安装任何第三方包
- **ADB** -- 已安装且在系统 PATH 中（命令行输入 `adb version` 能正常输出）
- **Android 设备** -- 通过 USB 或 WiFi 连接到电脑，USB 调试已开启，电脑端已获得调试授权
- **目标 APP** -- 已安装在设备上，已完成登录/注册等前置步骤
- **PowerShell** -- 运行 E2E 测试脚本需要（Windows 自带，跳过测试则不需要）


## 快速开始（3 步上手）

**第 1 步：确认 ADB 连接正常**

```bash
adb devices
```

输出应包含你的设备 ID，状态为 `device`。如果列表为空，先解决连接问题。

**第 2 步：打开目标 APP 到首页**

在手机上启动你要接入的 APP，确保已登录并停留在首页。如果 APP 不在前台，工具也会尝试自动启动。

**第 3 步：运行工具**

```bash
cd <project_root>\Tools\app_onboarder
python main.py
```

工具会进入交互模式，依次提示你输入包名和平台 Key，然后自动完成探索、配置生成和测试。


## 完整用法

### 交互模式

不带任何参数启动，工具会一步一步引导你：

```bash
python main.py
```

流程如下：

1. 检查 ADB 连接，显示设备屏幕分辨率
2. 提示输入 APP 包名（如 `com.babycenter.pregnancytracker`）
3. 提示输入平台 Key（如 `babycenter`），默认取包名最后一段
4. 确认后开始自动探索
5. 探索完成，自动生成配置文件
6. 询问是否运行 E2E 测试
7. 输出最终汇总

### 命令行模式

所有信息通过参数传入，适合脚本调用或重复执行：

```bash
# 指定包名和 Key，全流程运行
python main.py --package com.babycenter.pregnancytracker --key babycenter

# 跳过 E2E 测试
python main.py -p com.babycenter.pregnancytracker -k babycenter --skip-test

# 指定设备（多设备连接时）
python main.py -p com.babycenter.pregnancytracker -d emulator-5554

# 指定输出目录
python main.py -p com.babycenter.pregnancytracker -o D:\output
```

### 所有命令行参数

| 参数 | 缩写 | 说明 | 默认值 |
|------|------|------|--------|
| `--package` | `-p` | APP 包名 | 无（交互模式下手动输入） |
| `--key` | `-k` | 平台简称，用作配置中的标识符 | 从包名最后一段推断 |
| `--device` | `-d` | ADB 设备 ID | 使用默认设备 |
| `--skip-explore` | -- | 跳过探索阶段 | 不跳过 |
| `--skip-test` | -- | 跳过 E2E 测试阶段 | 不跳过 |
| `--output` | `-o` | 输出目录 | DPS v4.5 项目根目录 |

注意：`--skip-explore` 目前版本（v1.0）暂不支持加载已有探索数据，使用此参数会直接退出。


## 工具输出

### 生成的文件清单

运行完成后，工具会生成以下文件（以平台 Key 为 `babycenter` 为例）：

| 文件 | 路径 | 说明 |
|------|------|------|
| 平台配置 | `Config/PlatformsConfig.json` | 新平台条目合并到现有文件 |
| 操作定义 | `Config/Operations/babycenter_operations.json` | APP 交互操作的完整定义 |
| E2E 测试 | `{output_dir}/babycenter_e2e_test.ps1` | PowerShell 端到端测试脚本 |
| 探索日志 | `~/onboarder_babycenter_log.txt` | 探索过程的详细日志 |

所有路径相对于 DPS v4.5 项目根目录（`Tools/app_onboarder/` 往上两级）。

### PlatformsConfig.json 格式说明

工具在现有 `PlatformsConfig.json` 的 `platforms` 对象中新增一个条目，结构如下：

```json
{
  "platforms": {
    "babycenter": {
      "name": "BabyCenter",
      "package_name": "com.babycenter.pregnancytracker",
      "enabled": true,
      "rate_limits": { ... },
      "ui_selectors": { ... },
      "page_signatures": { ... },
      "action_weights": { ... },
      "scroll_config": { ... },
      "error_thresholds": {
        "max_ui_not_found": 6,
        "max_app_crashes": 2,
        "max_network_errors": 8
      },
      "verified": false,
      "verified_date": null,
      "notes": "由 App Onboarder 自动生成 (2026-03-04)"
    }
  }
}
```

关键字段含义：

- `ui_selectors` -- 各页面、各元素的定位策略和值（resource-id、text、xpath 等）
- `page_signatures` -- 用于识别当前处于哪个页面的特征元素
- `scroll_config` -- 滑动操作的坐标和参数，适配设备分辨率
- `action_weights` -- 各交互动作（点赞、评论、分享等）的执行权重
- `verified` -- 初始为 `false`，通过 E2E 测试后可手动改为 `true`

### operations.json 格式说明

操作定义文件描述 APP 内的各种交互动作，供 DPS 引擎调用。文件路径为 `Config/Operations/{platform}_operations.json`。

### E2E 测试脚本说明

生成的 PowerShell 脚本（`{platform}_e2e_test.ps1`）用于验证配置是否能正确驱动 APP。脚本会逐个测试关键操作（导航切换、Feed 滑动、帖子打开、按钮点击等），输出通过/失败状态。

测试运行器（TestRunner）会解析测试输出，分析失败原因，并自动应用修复策略：

| 修复策略 | 说明 |
|----------|------|
| `delay_increase` | 增加等待时间（乘以 1.5 倍） |
| `selector_swap` | 将 strategy/value 切换为 fallback_strategy/fallback_value |
| `scroll_adjust` | 增加滚动距离或最大滚动尝试次数 |
| `coordinate_adjust` | 根据设备屏幕尺寸重新计算滑动坐标 |
| `webview_wait_increase` | 增加 WebView 页面加载的等待时间 |

最多执行 3 轮"测试、修复、重试"循环。


## 工作原理

### 5 阶段探索流程

工具对 APP 的探索分为 5 个阶段，按顺序执行：

```
Phase 1: 首页扫描
    关闭弹窗，识别底部导航栏，获取所有 Tab

Phase 2: 导航探索
    逐个点击底部 Tab，记录每个页面的结构

Phase 3: Feed 分析
    定位社区/Feed Tab（基于关键词启发式匹配），
    识别 Feed 类型（viewpager_horizontal 或 recycler_vertical），
    提取帖子容器 ID 和帖子内元素

Phase 4: 帖子详情分析
    点击进入帖子详情页，
    判断是原生页面还是 WebView，
    如果是 WebView 则检测容器 ID 和 Accessibility 支持

Phase 5: 交互按钮发现
    在帖子详情页中查找点赞、评论、收藏、分享等按钮，
    记录每个按钮的定位信息
```

Feed Tab 的识别使用关键词列表匹配，优先级从高到低：`community`, `birth club`, `forum`, `social`, `feed`, `home`, `explore`, `discover`, `groups`, `discussions`, `trending`, 以及中文关键词 `社区`, `论坛`, `发现`, `关注`, `动态`。

当启发式方法无法判断时，工具会截图并向用户提问。

### 模块架构图

```
                    +------------------+
                    |    main.py       |
                    |  (入口 + 流程控制)|
                    +--------+---------+
                             |
            +----------------+----------------+
            |                |                |
   +--------v------+  +-----v-------+  +-----v--------+
   | ADBController  |  | AppExplorer |  | TestRunner   |
   | (设备通信)     |  | (自动探索)  |  | (测试+修复)  |
   +--------+------+  +------+------+  +--------------+
            |                |
            |         +------v------+
            |         | UIAnalyzer  |
            |         | (XML 解析)  |
            |         +-------------+
            |
            |         +-------------+
            +-------->|ConfigGenerator|
                      | (配置生成)  |
                      +-------------+
```

### 各模块职责

**main.py** -- 程序入口，解析命令行参数，串联 6 个步骤（ADB 检查、信息获取、探索、配置生成、E2E 测试、汇总），处理用户交互。

**adb_controller.py** -- 封装所有 ADB 命令：设备连接检测、屏幕分辨率获取、UI dump（uiautomator）、截图、点击、滑动、按键、APP 启动等。支持指定设备 ID，自动处理超时和编码问题。

**ui_analyzer.py** -- 解析 uiautomator 生成的 XML dump，提供元素查找、底部导航检测、页面分类、滚动容器识别、WebView 检测等功能。定义了 `UIElement` 数据结构，封装了 resource-id、text、content-desc、bounds 等属性。

**app_explorer.py** -- 自主探索引擎，执行 5 阶段探索流程，调用 ADBController 操作设备、UIAnalyzer 分析页面，最终输出 `app_map` 字典。探索过程中会自动关闭弹窗，失败时截图向用户提问。

**config_generator.py** -- 接收 `app_map` 数据，生成 PlatformsConfig.json 平台条目（合并到现有文件）、operations.json 操作定义、E2E 测试 PowerShell 脚本。E2E 脚本以 UTF-8 with BOM 编码写入，兼容 PowerShell 中文注释。

**test_runner.py** -- 运行 E2E PowerShell 测试脚本，解析输出，分析失败原因，自动应用 5 种修复策略后重试。最多 3 轮循环，输出包含尝试次数、修复记录和最终通过率的报告。


## 常见问题 (FAQ)

### ADB 连接问题

**Q: 运行工具时提示"ADB 设备未连接"**

检查以下几点：

1. USB 调试是否已开启（设置 > 开发者选项 > USB 调试）
2. 数据线是否正常（换根线试试，充电线可能不支持数据传输）
3. 手机上是否弹出了"允许 USB 调试"的授权对话框，点"允许"
4. 命令行运行 `adb devices`，确认设备列表中有你的设备且状态为 `device`

WiFi 连接的情况：

```bash
adb tcpip 5555
adb connect 192.168.x.x:5555
```

多设备同时连接时，用 `-d` 参数指定设备 ID：

```bash
python main.py -d emulator-5554
```

### APP 弹窗干扰

**Q: 探索时 APP 弹出各种对话框，影响结果**

工具在首页扫描阶段会自动尝试关闭弹窗。但某些弹窗（如强制更新、权限请求）可能无法自动处理。

建议在运行工具前：

1. 手动打开 APP，关闭所有弹窗
2. 如有"不再提示"选项，勾选它
3. 提前授予 APP 所需的权限（存储、通知等）
4. 确保 APP 版本是最新的，避免强制更新弹窗

### WebView 页面检测不到

**Q: 帖子详情页是 WebView，但工具没有检测到**

Phase 4 通过 uiautomator dump 检查是否存在 `android.webkit.WebView` 类型的节点。如果检测不到，可能的原因：

1. WebView 还没加载完，工具已经 dump 了 -- 可以在运行前确保网络通畅
2. APP 使用了自定义 WebView 实现，class name 不是标准的 `android.webkit.WebView`
3. WebView 嵌套在其他容器中，层级较深

这种情况下，生成的配置中 `post_detail_is_webview` 会是 `false`。你可以手动修改 `PlatformsConfig.json`，将对应字段改为 `true` 并补充 `webview_container_id`。

### 生成的配置不准确怎么办

**Q: 自动生成的配置运行时出错**

这是正常的。自动探索基于启发式方法，不可能 100% 准确。处理步骤：

1. 先运行 E2E 测试（不要加 `--skip-test`），让工具尝试自动修复
2. 查看测试报告，找到失败的步骤
3. 打开 `PlatformsConfig.json` 和 `{platform}_operations.json`，手动调整对应字段
4. 重新运行测试验证

常见需要手动调整的项目：

- `ui_selectors` 中的 resource-id 不准确 -- 用 `uiautomator dump` 手动查看正确的 ID
- `scroll_config` 的坐标不合适 -- 根据设备分辨率调整起止坐标
- `action_weights` 权重分配不符合需求 -- 按业务要求调整


## 文件清单

| 文件名 | 行数 | 功能 |
|--------|------|------|
| `main.py` | 314 | 程序入口，命令行解析，6 步流程串联 |
| `adb_controller.py` | 300 | ADB 命令封装，设备操作接口 |
| `ui_analyzer.py` | 483 | UI dump XML 解析，元素查找，页面分类 |
| `app_explorer.py` | 699 | 5 阶段自主探索引擎 |
| `config_generator.py` | 1812 | 配置文件和测试脚本生成 |
| `test_runner.py` | 950 | E2E 测试运行、失败分析、自动修复 |
| **合计** | **4558** | -- |

零第三方依赖，全部使用 Python 标准库（`subprocess`, `xml.etree.ElementTree`, `argparse`, `json`, `re`, `os`, `time`, `random`, `copy`, `datetime`, `sys`）。


## 与 DPS v4.5 的关系

App Onboarder 位于 DPS v4.5 项目的 `Tools/app_onboarder/` 目录下，是一个独立的辅助工具。

DPS v4.5 是社交平台自动化引擎，核心运行时读取 `Config/PlatformsConfig.json` 和 `Config/Operations/` 下的操作文件来驱动不同 APP 的交互。App Onboarder 的作用就是自动生成这些配置，降低接入新平台的门槛。

项目目录关系：

```
DPS_v4.5/
  Config/
    PlatformsConfig.json    <-- App Onboarder 写入新平台条目
    Operations/
      {platform}_operations.json  <-- App Onboarder 生成
  Tools/
    app_onboarder/          <-- 本工具所在目录
      main.py
      adb_controller.py
      ui_analyzer.py
      app_explorer.py
      config_generator.py
      test_runner.py
      README.md             <-- 你正在阅读的这个文件
```

DPS_ROOT 变量通过 `main.py` 所在路径往上推两级自动计算，不需要手动配置。
