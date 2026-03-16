# Phase 0 技术验证 - 详细操作手册 (英文版 ProjectMaker)

> 基于用户确认的 ProjectMaker 英文版真实菜单结构
> 所有菜单路径已经过用户逐项核实
> 前提: ProjectMaker 已打开，设备已连接，变量已创建

---

## ProjectMaker 真实菜单结构（用户确认）

### 右键菜单一级分类
Data | Content analysis | Lists | Tables | **Logic** | Process e-mail | Proxychecker | http | Ftp | project | **custom code** | **android**

### Logic 子项
**If** | **Switch** | **Notification** | **Pause** | Waiting for user actions | BadEnd | GoodEnd

### Android 子项
Actions with device | Device settings | **Utils** | **Actions with application** | Files | LSPosed Management | **Get Value** | **Check for text** | **Set Value** | **Rise Event** | **Touch Emulation** | **Swipe Emulation** | Keyboard Emulation | Recognize captcha | Recognize ReCaptcha | Recognize hCaptcha

### Android → Actions with application 子项
Install application | Uninstall application | **Open application** | Open URL | **Close application** | Application cleanup | Save application's data | Restore application's data | Get apk of the application | Name of the active application | Get application list | Application already installed | Get notifications | Clear notifications | Get cookies from the app | Get accounts | Add account | Remove account

### 添加 Action 的操作方式
在工作区空白处**右键** → 从弹出菜单选择分类和动作

---

## 验证 0.1: Rise Event 块在元素找不到时是否触发 Bad 端口

### 目标
确认当 Rise Event 块找不到指定元素时，是否走红色 Bad 端口。

### 逐步操作

**步骤 1: 添加 Open application 块**
- 工作区空白处**右键** → **android** → **Actions with application** → **Open application**
- 块出现在工作区后，**双击**它打开属性
- Package name 填: `com.android.settings`
- 点确定

**步骤 2: 添加 Pause 块**
- 工作区空白处**右键** → **Logic** → **Pause**
- 双击配置: 时间填 `3000`（3 秒）

**步骤 3: 添加 Rise Event 块（故意找不存在的元素）**
- 工作区空白处**右键** → **android** → **Rise Event**
- 双击打开属性
- Attribute name 选: `resource-id`
- Value 填: `this_element_does_not_exist_12345`
- Search type 选: `text`
- Selection action 选: `Rise`（触发点击）
- 点确定

**步骤 4: 添加两个 Notification 块**
- 工作区空白处**右键** → **Logic** → **Notification**
- 双击第一个，Message 填: `0.1 FAIL: Bad port NOT triggered`
- 再右键 → Logic → Notification 添加第二个
- 双击第二个，Message 填: `0.1 PASS: Bad port triggered correctly`

**步骤 5: 添加 Close application 块**
- 右键 → **android** → **Actions with application** → **Close application**
- 双击配置: Package name 填 `com.android.settings`

**步骤 6: 连线**
```
[START]
   │ (从 START 的输出端口拖线到 Open application 的输入端口)
   v
[Open application]
   │ (Good/绿色端口 → Pause 输入端口)
   v
[Pause 3000ms]
   │ (Good/绿色端口 → Rise Event 输入端口)
   v
[Rise Event: 找不存在的元素]
   ├── Good/绿色端口 → [Notification: "0.1 FAIL"] → [Close application] → [END]
   └── Bad/红色端口  → [Notification: "0.1 PASS"] → [Close application] → [END]
```

**步骤 7: 运行**
- 点工具栏的 Play 按钮运行
- 看日志输出是 PASS 还是 FAIL

### 结果记录
```
验证 0.1: Rise Event 失败检测
走了哪条线: [Good/Bad]
日志输出: [PASS/FAIL]
```

---

## 验证 0.2: Custom code 异常后循环行为

### 目标
确认 C# 代码块抛出异常后，ZD 循环是否继续。

### 逐步操作

**步骤 1: 添加 C# 代码块（初始化）**
- 右键 → **custom code**（一级菜单直接点击）
- 双击打开代码编辑器，输入:
```csharp
project.Variables["loop_count"].Value = "0";
project.SendInfoToLog("Loop initialized", true);
```

**步骤 2: 添加 If 块**
- 右键 → **Logic** → **If**
- 双击配置:
  - Variable 1: `{-Variable.loop_count-}`
  - Operator: `<`
  - Variable 2: `3`

**步骤 3: 添加 C# 代码块（故意抛异常）**
- 右键 → **custom code**
- 双击输入:
```csharp
int count = int.Parse(project.Variables["loop_count"].Value);
project.SendInfoToLog("loop_count = " + count.ToString(), true);

if (count == 1)
{
    throw new System.Exception("Phase 0.2: intentional test exception!");
}

project.SendInfoToLog("OwnCode completed normally", true);
```

**步骤 4: 添加 Notification（Good 线）**
- 右键 → **Logic** → **Notification**
- Message: `OwnCode OK, no exception`

**步骤 5: 添加 Notification（Bad 线）**
- 右键 → **Logic** → **Notification**
- Message: `OwnCode exception caught by Bad port`

**步骤 6: 添加 C# 代码块（计数器 +1）**
- 右键 → **custom code**
- 双击输入:
```csharp
int c = int.Parse(project.Variables["loop_count"].Value);
c++;
project.Variables["loop_count"].Value = c.ToString();
```

**步骤 7: 添加 Notification（循环结束）**
- 右键 → **Logic** → **Notification**
- Message: `Loop ended, loop_count = {-Variable.loop_count-}`

**步骤 8: 连线**
```
[START] → [C# Init: loop_count=0]
              │ Good
              v
         [If: loop_count < 3]
              │                    \
              │ Good(条件真)         Bad(条件假)
              v                        │
         [C# Exception Test]           v
           │           \         [Notification: Loop ended] → [END]
           │Good        \Bad
           v             v
    [Notif: OK]    [Notif: caught]
           │             │
           └──────┬──────┘
                  v
         [C# loop_count++]
                  │ Good
                  └──→ 连线回到 [If] （形成循环）
```

**关键连线**: C# loop_count++ 的 **Good 端口** 连回 **If 块** 的输入端口。

**步骤 9: 运行并记录**

### 结果记录
```
验证 0.2: OwnCode 异常后循环行为
异常时走了: [Good/Bad]
循环是否继续: [是/否]
loop_count 最终值: [0/1/2/3]
结论: [最佳(Bad+继续)/可用(Bad+中断)/最差(崩溃)]
```

---

## 验证 0.3: Switch 多分支

### 逐步操作

直接用一个 C# 代码块测 switch 性能:

**步骤 1: 添加 C# 代码块**
- 右键 → **custom code**
- 双击输入:
```csharp
var sw = System.Diagnostics.Stopwatch.StartNew();

string val = project.Variables["switch_test"].Value;
string result = "unknown";

switch(val)
{
    case "case_a": result = "a"; break;
    case "case_b": result = "b"; break;
    case "case_c": result = "c"; break;
    case "case_d": result = "d"; break;
    case "case_e": result = "e"; break;
    case "case_f": result = "f"; break;
    case "case_g": result = "g"; break;
    case "case_h": result = "h"; break;
    case "case_i": result = "i"; break;
    case "case_j": result = "j"; break;
    case "case_k": result = "k"; break;
    case "case_l": result = "l"; break;
    case "case_m": result = "m"; break;
    case "case_n": result = "n"; break;
    case "case_o": result = "o"; break;
    default: result = "default"; break;
}

sw.Stop();
project.SendInfoToLog("Switch hit: " + result + ", latency: " + sw.ElapsedMilliseconds + "ms", true);
```

**步骤 2: 连线**
```
[START] → [C# Switch Test] → [END]
```

**步骤 3: 确认变量** `switch_test` 的值是 `case_k`，运行。

### 结果记录
```
验证 0.3: Switch 多分支
命中结果: [case_k/其他]
延迟: [X ms]
```

---

## 验证 0.4: Pause 块变量驱动时长

### 逐步操作

**步骤 1: C# 代码块（记录开始时间）**
- 右键 → **custom code**
```csharp
project.Variables["test_result"].Value = System.DateTime.Now.Ticks.ToString();
project.SendInfoToLog("Timer started", true);
```

**步骤 2: Pause 块**
- 右键 → **Logic** → **Pause**
- 双击打开属性
- 时间字段填: `{-Variable.zd_wait_ms-}`
- （变量 `zd_wait_ms` 值应为 `2000`）

**步骤 3: C# 代码块（计算实际等待）**
- 右键 → **custom code**
```csharp
long start = long.Parse(project.Variables["test_result"].Value);
long end = System.DateTime.Now.Ticks;
long elapsedMs = (end - start) / 10000;
project.SendInfoToLog("Pause actual: " + elapsedMs + "ms (expected 2000ms)", true);
project.SendInfoToLog("0.4 Result: " + ((elapsedMs >= 1500 && elapsedMs <= 3000) ? "PASS" : "FAIL"), true);
```

**步骤 4: 连线**
```
[START] → [C# Timer Start] → [Pause] → [C# Timer End] → [END]
```

### 结果记录
```
验证 0.4: Pause 变量驱动
Pause 字段是否接受 {-Variable.zd_wait_ms-}: [是/否]
实际等待: [X ms]
结论: [PASS/FAIL]
```

---

## 验证 0.5: Set Value 块变量输入

### 逐步操作

**步骤 1: Open application**
- 右键 → **android** → **Actions with application** → **Open application**
- Package name: `com.android.settings`

**步骤 2: Pause**
- 右键 → **Logic** → **Pause** → `3000`

**步骤 3: Rise Event（点击搜索图标）**
- 右键 → **android** → **Rise Event**
- Attribute name: `content-desc`
- Value: `Search`
- Search type: `text`
- Selection action: `Rise`

**步骤 4: Pause**
- 右键 → **Logic** → **Pause** → `1500`

**步骤 5: Set Value**
- 右键 → **android** → **Set Value**
- 双击配置:
  - Attribute name: `class`
  - Value: `android.widget.EditText`
  - Search type: `text`
  - Selection action: `Set`
  - Attribute: `Input`
  - Value 字段填: `{-Variable.zd_text-}`

**步骤 6: Pause + Notification**
- Pause `2000` → Notification: `0.5 done, check screen`

**步骤 7: Close application**
- 右键 → **android** → **Actions with application** → **Close application**
- Package name: `com.android.settings`

**步骤 8: 连线并运行**

Set Value 的 Good 线走正常流程，Bad 线接一个 Notification: `0.5 FAIL: Set Value rejected variable`。

### 结果记录
```
验证 0.5: Set Value 变量输入
Set Value 是否接受 {-Variable.zd_text-}: [是/否]
搜索框是否出现 "Hello Phase0": [是/否]
```

---

## 验证 0.6: 30+ 块性能

### 逐步操作

1. 右键 → **Logic** → **Notification** → Message: `test block`
2. 点击该块 → **Ctrl+C** → **Ctrl+V** 重复 30 次
3. 逐个连线（每个 Good 端口连到下一个输入端口）
4. 运行，观察:
   - ProjectMaker 是否卡顿？
   - 运行是否正常完成？

### 结果记录
```
验证 0.6: 30+ 块性能
创建块数: [X]
卡顿情况: [无/轻微/严重]
运行正常: [是/否]
```

---

## 验证 0.7: AI 视觉延迟（可选）

### 逐步操作

- 右键 → **custom code**
```csharp
var sw = System.Diagnostics.Stopwatch.StartNew();

byte[] screenshot = instance.DroidInstance.Screen.ScreenshotAsArray();
string path = project.Variables["project_root"].Value + "\\Screenshots\\phase0_test.png";

string dir = System.IO.Path.GetDirectoryName(path);
if (!System.IO.Directory.Exists(dir))
    System.IO.Directory.CreateDirectory(dir);

System.IO.File.WriteAllBytes(path, screenshot);

sw.Stop();
project.SendInfoToLog("Screenshot capture: " + sw.ElapsedMilliseconds + "ms, saved to " + path, true);
```

连线: `[START] → [C# Screenshot] → [END]`

### 结果记录
```
验证 0.7: 截图延迟
截图耗时: [X ms]
文件是否保存成功: [是/否]
```

---

## 汇总表

完成所有测试后填写:

| # | 验证项 | 结果 | 详情 | 应对 |
|---|---|---|---|---|
| 0.1 | Rise Event Bad 端口 | | | |
| 0.2 | OwnCode 异常+循环 | | loop_count= | |
| 0.3 | Switch 15 分支 | | 延迟= ms | |
| 0.4 | Pause 变量驱动 | | 实际= ms | |
| 0.5 | Set Value 变量 | | | |
| 0.6 | 30+ 块性能 | | | |
| 0.7 | 截图延迟 | | 耗时= ms | |

**填好后发给我，我根据结果开始 Phase A + B0 编码。**
