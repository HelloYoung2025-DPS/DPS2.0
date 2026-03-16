# DPS v4.7 ZennoDroid ProjectMaker 配置说明书

> **版本**: v4.7.0
> **架构**: ADR-015 ZD 外层流程编排 + 数据驱动步骤计划
> **适用**: ZennoDroid Enterprise + 真机 USB 连接
> **用途**: 本文档可直接发给 GPT/Gemini 辅助配置

---

## 一、概述

### 1.1 本文档做什么

指导你在 ZennoDroid ProjectMaker 中从零搭建一个完整的 DPS v4.7 会话执行项目。搭建完成后，系统将：

1. **C# 决策层** 决定下一步做什么（浏览/点赞/评论等）
2. **C# Locate 层** 在手机屏幕上查找目标元素，输出坐标
3. **ZD 原生动作块** 用拟人化的方式执行点击/滑动/等待
4. **C# 评估层** 判断执行结果，决定继续/重试/结束

### 1.2 架构图

```
ZD ProjectMaker 流程:

[1.DPS_Init]
    |
[2.If: init==SUCCESS]--NO-->[BadEnd]
    |YES
[3.OUTER LOOP]
    |
    +--[4.DPS_DecideAction]
    |       |
    |   [5.If: next_action==END]--YES-->[Break Loop → 11]
    |       |NO
    |   [6.C# StepRunner: 解析步骤计划, 设置第一步]
    |       |
    |   [7.INNER LOOP (step_index < step_count)]
    |       |
    |       +--[8.Switch: zd_step_type]
    |       |     L    → [9a.C# Locate]
    |       |     T    → [9b.ZD Touch]
    |       |     T:long → [9c.ZD Touch Long]
    |       |     S    → [9d.ZD Swipe]
    |       |     B    → [9e.ZD Back Key]
    |       |     W    → [9f.ZD Pause]
    |       |     V    → [9g.C# Verify]
    |       |
    |       +--[10.C# StepAdvancer: step_index++, 设置下一步]
    |       |     |
    |       +-----+ (回到 7 循环判断)
    |
    +--[11.DPS_CheckResult]
    |       |
    |   [12.If: eval_result==END]--YES-->[Break Loop → 13]
    |       |NO
    +-------+ (回到 3 循环顶部)
    
[13.DPS_Finalize]
    |
[GoodEnd]
```

### 1.3 前置条件

| 项目 | 要求 |
|------|------|
| ZennoDroid | Enterprise 版，支持真机 |
| 手机连接 | USB 调试已开启，adb devices 可见 |
| 项目文件 | `D:\ZennoDroid-AI\DPS_v4.5\` 已存在 |
| Reddit APP | 已安装在手机上，可正常打开 |

---

## 二、ZD 变量表（完整）

在 ProjectMaker 中右键 Variables → Add，逐个创建以下变量。

### 2.1 系统级变量（DPS_Init 写入，全流程读取）

| 变量名 | 默认值 | 类型 | 说明 |
|--------|--------|------|------|
| `project_root` | `D:\ZennoDroid-AI\DPS_v4.5\` | string | 项目根目录（末尾带反斜杠） |
| `device_id` | `R58M255GNQZ` | string | 设备序列号 |
| `persona_json` | `{}` | string | 人设 JSON |
| `session_plan_json` | `{}` | string | 会话计划 JSON |
| `behavior_config_json` | | string | 行为配置 JSON（空则从文件读取） |
| `current_platform` | `reddit` | string | 当前平台名 |

### 2.2 会话状态变量（Init 写入，Decide/Check/Finalize 读写）

| 变量名 | 默认值 | 说明 |
|--------|--------|------|
| `session_init_result` | | Init 返回值: SUCCESS / ERROR:xxx |
| `next_action` | | Decide 返回值: 操作名 / END / SKIP / ERROR:xxx |
| `eval_result` | | Check 返回值: CONTINUE / RETRY / END |
| `session_result` | | 最终结果: SUCCESS / ERROR |
| `sr_session_end_time` | | 会话结束时间 ISO 格式 |
| `sr_session_start_time` | | 会话开始时间 ISO 格式 |
| `sr_session_state` | | SessionState 疲劳模型 JSON |
| `sr_action_weights` | | 动作权重 JSON |
| `sr_action_count` | `0` | 总动作计数 |
| `sr_success_count` | `0` | 成功计数 |
| `sr_fail_count` | `0` | 失败计数 |
| `sr_skip_count` | `0` | 跳过计数 |
| `sr_consecutive_skips` | `0` | 连续跳过计数 |
| `sr_vision_recovery_count` | `0` | Vision 调用计数 |
| `sr_orchestrator_state` | | SmartOrchestrator 状态 JSON |
| `sr_op_queue` | | 操作队列（管道分隔） |
| `sr_op_queue_index` | `0` | 队列当前索引 |
| `sr_current_session_action` | | 当前会话动作名 (browse/like...) |
| `sr_current_op_name` | | 当前操作名 |
| `sr_memory_entries` | | 记忆条目（逗号分隔 JSON） |
| `sr_memory_file` | | 记忆文件路径 |

### 2.3 步骤执行变量（StepRunner/Locate/原生块 读写）

| 变量名 | 默认值 | 说明 |
|--------|--------|------|
| `zd_step_plan` | | 当前操作的完整步骤计划字符串 |
| `zd_step_count` | `0` | 步骤总数 |
| `zd_step_index` | `0` | 当前步骤索引 |
| `zd_step_type` | | 当前步骤类型: L / T / T:long / S / B / W / V |
| `zd_step_arg` | | 当前步骤参数 |
| `zd_tap_x` | `0` | Locate 输出的点击 X 坐标 |
| `zd_tap_y` | `0` | Locate 输出的点击 Y 坐标 |
| `zd_found` | `false` | Locate 是否找到元素 |
| `zd_swipe_x1` | `540` | Swipe 起点 X |
| `zd_swipe_y1` | `1400` | Swipe 起点 Y |
| `zd_swipe_x2` | `540` | Swipe 终点 X |
| `zd_swipe_y2` | `500` | Swipe 终点 Y |
| `zd_wait_sec` | `2` | Wait 秒数（单位: 秒） |
| `zd_action_result` | | ZD 原生块执行结果 |
| `zd_action_duration` | `3` | 实际执行耗时（秒） |
| `zd_verify_rule` | | 验证规则 |
| `zd_verify_pass` | `false` | 验证是否通过 |

### 2.4 辅助变量

| 变量名 | 默认值 | 说明 |
|--------|--------|------|
| `current_page` | `unknown` | 当前页面状态 |
| `current_action` | | 当前执行的动作 |
| `current_intent` | | 当前意图 |
| `effective_intent` | | 有效意图 |
| `current_post_id` | | 当前帖子 ID |
| `current_post_json` | | 当前帖子 JSON |
| `initial_page` | | 初始页面 |
| `last_error` | | 最后的错误信息 |
| `run_result` | | 运行结果 |
| `action_count` | `0` | 输出用动作数 |

---

## 三、C# Code Cube 代码（共 6 个）

### 3.1 DPS_Init（块 1）

**位置**: 流程最前面
**文件**: `ZDProjects\DPS_Init_OwnCode.cs`

将该文件的全部内容复制粘贴到 C# code 动作块中。

> 该块调用 `SessionRunner.InitSession()`，完成配置加载、平台确认、编排器初始化。
> 结果写入变量 `session_init_result`。

### 3.2 DPS_DecideAction（块 4）

**位置**: 外层循环内第一个块
**文件**: `ZDProjects\DPS_DecideAction_OwnCode.cs`

> 调用 `SessionRunner.DecideNextAction()`，返回操作名或 "END"。
> 结果写入变量 `next_action`。

### 3.3 StepRunner（块 6）— 解析步骤计划

**位置**: DecideAction 之后，内层循环之前
**作用**: 读取 `next_action` 对应的步骤计划，解析并设置第一步

粘贴以下代码到 C# code 块：

```csharp
// =============================================
// StepRunner - 解析操作步骤计划
// 读取 next_action → 查找步骤计划 → 设置 zd_step_* 变量
// =============================================
try
{
    string action = project.Variables["next_action"].Value;
    string root = project.Variables["project_root"].Value;
    if (!root.EndsWith("\\")) root += "\\";
    string platform = project.Variables["current_platform"].Value;
    
    // 读取步骤计划配置
    string planPath = root + "Config\\StepPlans\\" + platform + "_step_plans.json";
    string planJson = "";
    if (System.IO.File.Exists(planPath))
    {
        planJson = System.IO.File.ReadAllText(planPath, System.Text.Encoding.UTF8);
    }
    else
    {
        project.SendErrorToLog("[StepRunner] 步骤计划文件不存在: " + planPath);
        project.Variables["zd_step_count"].Value = "0";
        return "ERROR";
    }
    
    // 用简单字符串查找提取对应操作的计划
    // 格式: "操作名": "L:selector|T|W:3|..."
    string searchKey = "\"" + action + "\"";
    int keyIdx = planJson.IndexOf(searchKey);
    if (keyIdx < 0)
    {
        project.SendInfoToLog("[StepRunner] 操作无步骤计划: " + action);
        project.Variables["zd_step_count"].Value = "0";
        return "NO_PLAN";
    }
    
    // 提取值（引号内内容）
    int colonIdx = planJson.IndexOf(":", keyIdx + searchKey.Length);
    int quoteStart = planJson.IndexOf("\"", colonIdx + 1);
    int quoteEnd = planJson.IndexOf("\"", quoteStart + 1);
    string stepPlan = planJson.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
    
    project.Variables["zd_step_plan"].Value = stepPlan;
    
    // 计算步骤数（按 | 分隔）
    string[] steps = stepPlan.Split('|');
    project.Variables["zd_step_count"].Value = steps.Length.ToString();
    project.Variables["zd_step_index"].Value = "0";
    
    // 解析第一步
    ParseStep(steps[0], project);
    
    project.SendInfoToLog("[StepRunner] 计划: " + stepPlan + " (" + steps.Length + " 步)");
    return "OK";
}
catch (System.Exception ex)
{
    project.SendErrorToLog("[StepRunner] 异常: " + ex.Message);
    project.Variables["zd_step_count"].Value = "0";
    return "ERROR";
}

// 辅助: 解析单个步骤，设置 zd_step_type 和 zd_step_arg
// 不能用 static 方法，内联在此
void ParseStep(string step, ZennoLab.InterfacesLibrary.ProjectModel.IZennoPosterProjectModel p)
{
    step = step.Trim();
    int colonPos = step.IndexOf(':');
    if (colonPos > 0)
    {
        string stype = step.Substring(0, colonPos);
        string sarg = step.Substring(colonPos + 1);
        p.Variables["zd_step_type"].Value = stype;
        p.Variables["zd_step_arg"].Value = sarg;
        
        // W 步骤: 设置等待秒数
        if (stype == "W")
        {
            p.Variables["zd_wait_sec"].Value = sarg;
        }
        // S 步骤: 设置滑动参数
        else if (stype == "S")
        {
            SetSwipeParams(sarg, p);
        }
    }
    else
    {
        // 无参数步骤（T, B 等）
        p.Variables["zd_step_type"].Value = step;
        p.Variables["zd_step_arg"].Value = "";
    }
}

void SetSwipeParams(string preset, ZennoLab.InterfacesLibrary.ProjectModel.IZennoPosterProjectModel p)
{
    // 预设滑动参数（基于 1080x2400 分辨率，按比例调整）
    // 格式: direction_distance，如 down_900, up_1200
    string[] parts = preset.Split('_');
    if (parts.Length < 2) return;
    string dir = parts[0];
    int dist = 900;
    int.TryParse(parts[1], out dist);
    
    int cx = 540; // 屏幕中心 X
    
    if (dir == "down")
    {
        p.Variables["zd_swipe_x1"].Value = cx.ToString();
        p.Variables["zd_swipe_y1"].Value = "1400";
        p.Variables["zd_swipe_x2"].Value = cx.ToString();
        p.Variables["zd_swipe_y2"].Value = (1400 - dist).ToString();
    }
    else if (dir == "up")
    {
        p.Variables["zd_swipe_x1"].Value = cx.ToString();
        p.Variables["zd_swipe_y1"].Value = "500";
        p.Variables["zd_swipe_x2"].Value = cx.ToString();
        p.Variables["zd_swipe_y2"].Value = (500 + dist).ToString();
    }
}
```

### 3.4 Locate（块 9a）— 查找元素输出坐标

**位置**: Switch 的 "L" 分支

```csharp
// =============================================
// Locate - 查找元素并输出坐标到 ZD 变量
// 内置 retry: 找不到 → 等 500ms → 再试, 最多 3 次
// =============================================
try
{
    string selectorKey = project.Variables["zd_step_arg"].Value;
    string root = project.Variables["project_root"].Value;
    if (!root.EndsWith("\\")) root += "\\";
    string platform = project.Variables["current_platform"].Value;
    
    project.SendInfoToLog("[Locate] 查找: " + selectorKey);
    
    var driver = instance.DroidInstance.AppiumDriver;
    
    // 读取 UI selectors 配置
    string configPath = root + "Config\\PlatformsConfig.json";
    string configJson = System.IO.File.ReadAllText(configPath, System.Text.Encoding.UTF8);
    
    // 简易 JSON 解析: 提取 selector 定义
    // 支持两种查找策略:
    //   1. selectorKey 对应 PlatformsConfig 中的 ui_selectors
    //   2. "text_Xxx" 格式 → 按文本查找 "Xxx"
    
    bool found = false;
    int maxRetries = 3;
    
    for (int attempt = 0; attempt < maxRetries; attempt++)
    {
        try
        {
            OpenQA.Selenium.IWebElement element = null;
            
            if (selectorKey.StartsWith("text_"))
            {
                // 按文本查找
                string searchText = selectorKey.Substring(5).Replace("_", " ");
                var elements = driver.FindElementsByXPath(
                    "//*[contains(@text,'" + searchText + "')]");
                if (elements.Count > 0)
                {
                    element = elements[0];
                }
            }
            else
            {
                // 从配置读取选择器
                // 简化: 尝试 resource-id, content-desc, text 三种策略
                string rid = ExtractSelectorValue(configJson, platform, selectorKey, "resource_id");
                string cdesc = ExtractSelectorValue(configJson, platform, selectorKey, "content_desc");
                string txt = ExtractSelectorValue(configJson, platform, selectorKey, "text");
                
                if (!string.IsNullOrEmpty(rid))
                {
                    var els = driver.FindElementsById(rid);
                    if (els.Count > 0) element = els[0];
                }
                if (element == null && !string.IsNullOrEmpty(cdesc))
                {
                    var els = driver.FindElementsByXPath(
                        "//*[@content-desc='" + cdesc + "']");
                    if (els.Count > 0) element = els[0];
                }
                if (element == null && !string.IsNullOrEmpty(txt))
                {
                    var els = driver.FindElementsByXPath(
                        "//*[@text='" + txt + "']");
                    if (els.Count > 0) element = els[0];
                }
            }
            
            if (element != null && element.Displayed)
            {
                var rect = element.Rect;
                int cx = rect.X + rect.Width / 2;
                int cy = rect.Y + rect.Height / 2;
                
                project.Variables["zd_tap_x"].Value = cx.ToString();
                project.Variables["zd_tap_y"].Value = cy.ToString();
                project.Variables["zd_found"].Value = "true";
                
                project.SendInfoToLog("[Locate] 找到 " + selectorKey 
                    + " center=(" + cx + "," + cy + ")");
                found = true;
                break;
            }
        }
        catch (System.Exception) { }
        
        if (attempt < maxRetries - 1)
        {
            project.SendInfoToLog("[Locate] 未找到 " + selectorKey 
                + ", 重试 " + (attempt + 1) + "/" + maxRetries);
            System.Threading.Thread.Sleep(500);
        }
    }
    
    if (!found)
    {
        project.Variables["zd_found"].Value = "false";
        project.Variables["zd_action_result"].Value = "SKIP";
        project.SendInfoToLog("[Locate] 最终未找到: " + selectorKey);
    }
    
    return found ? "FOUND" : "NOT_FOUND";
}
catch (System.Exception ex)
{
    project.Variables["zd_found"].Value = "false";
    project.Variables["zd_action_result"].Value = "SKIP";
    project.SendErrorToLog("[Locate] 异常: " + ex.Message);
    return "ERROR";
}

// 辅助: 从 PlatformsConfig JSON 提取选择器值
string ExtractSelectorValue(string json, string platform, string selectorKey, string field)
{
    // 简易搜索: 找 "selectorKey" 对象内的 field 值
    // 真实实现应用 JsonHelper, 此处为独立版本
    try
    {
        int sIdx = json.IndexOf("\"" + selectorKey + "\"");
        if (sIdx < 0) return "";
        int objStart = json.IndexOf("{", sIdx);
        if (objStart < 0) return "";
        int objEnd = json.IndexOf("}", objStart);
        if (objEnd < 0) return "";
        string obj = json.Substring(objStart, objEnd - objStart + 1);
        
        int fIdx = obj.IndexOf("\"" + field + "\"");
        if (fIdx < 0) return "";
        int vStart = obj.IndexOf("\"", fIdx + field.Length + 3);
        if (vStart < 0) return "";
        int vEnd = obj.IndexOf("\"", vStart + 1);
        if (vEnd < 0) return "";
        return obj.Substring(vStart + 1, vEnd - vStart - 1);
    }
    catch { return ""; }
}
```

### 3.5 StepAdvancer（块 10）— 推进步骤索引

**位置**: Switch 之后，内层循环结束前

```csharp
// =============================================
// StepAdvancer - 推进步骤索引, 解析下一步参数
// =============================================
try
{
    int idx = 0;
    int.TryParse(project.Variables["zd_step_index"].Value, out idx);
    idx++;
    project.Variables["zd_step_index"].Value = idx.ToString();
    
    int count = 0;
    int.TryParse(project.Variables["zd_step_count"].Value, out count);
    
    if (idx >= count)
    {
        // 所有步骤已完成
        project.SendInfoToLog("[StepAdvancer] 所有步骤完成 (" + count + "/" + count + ")");
        return "DONE";
    }
    
    // 解析下一步
    string plan = project.Variables["zd_step_plan"].Value;
    string[] steps = plan.Split('|');
    string nextStep = steps[idx].Trim();
    
    int colonPos = nextStep.IndexOf(':');
    if (colonPos > 0)
    {
        string stype = nextStep.Substring(0, colonPos);
        string sarg = nextStep.Substring(colonPos + 1);
        project.Variables["zd_step_type"].Value = stype;
        project.Variables["zd_step_arg"].Value = sarg;
        
        if (stype == "W")
        {
            project.Variables["zd_wait_sec"].Value = sarg;
        }
        else if (stype == "S")
        {
            // 滑动参数解析
            string[] parts = sarg.Split('_');
            if (parts.Length >= 2)
            {
                string dir = parts[0];
                int dist = 900;
                int.TryParse(parts[1], out dist);
                int cx = 540;
                
                if (dir == "down")
                {
                    project.Variables["zd_swipe_x1"].Value = cx.ToString();
                    project.Variables["zd_swipe_y1"].Value = "1400";
                    project.Variables["zd_swipe_x2"].Value = cx.ToString();
                    project.Variables["zd_swipe_y2"].Value = (1400 - dist).ToString();
                }
                else if (dir == "up")
                {
                    project.Variables["zd_swipe_x1"].Value = cx.ToString();
                    project.Variables["zd_swipe_y1"].Value = "500";
                    project.Variables["zd_swipe_x2"].Value = cx.ToString();
                    project.Variables["zd_swipe_y2"].Value = (500 + dist).ToString();
                }
            }
        }
    }
    else
    {
        project.Variables["zd_step_type"].Value = nextStep;
        project.Variables["zd_step_arg"].Value = "";
    }
    
    project.SendInfoToLog("[StepAdvancer] 步骤 " + (idx + 1) + "/" + count + ": " + nextStep);
    return "NEXT";
}
catch (System.Exception ex)
{
    project.SendErrorToLog("[StepAdvancer] 异常: " + ex.Message);
    return "ERROR";
}
```

### 3.6 Verify（块 9g）— 页面验证

**位置**: Switch 的 "V" 分支

```csharp
// =============================================
// Verify - 验证当前页面是否匹配预期
// =============================================
try
{
    string expectedPage = project.Variables["zd_step_arg"].Value;
    string root = project.Variables["project_root"].Value;
    if (!root.EndsWith("\\")) root += "\\";
    string platform = project.Variables["current_platform"].Value;
    
    project.SendInfoToLog("[Verify] 验证页面: " + expectedPage);
    
    var driver = instance.DroidInstance.AppiumDriver;
    string pageSource = driver.PageSource;
    
    // 读取页面签名配置
    string configPath = root + "Config\\PlatformsConfig.json";
    string configJson = System.IO.File.ReadAllText(configPath, System.Text.Encoding.UTF8);
    
    // 简易页面检测: 根据签名元素判断
    bool matched = false;
    
    if (expectedPage == "feed")
    {
        // feed 页特征: 有 RecyclerView 或 feed 容器
        matched = pageSource.Contains("feed_container") 
               || pageSource.Contains("recyclerview")
               || pageSource.Contains("com.reddit.frontpage:id/main_community_feed");
    }
    else if (expectedPage == "post_detail")
    {
        // 详情页特征: 有评论区域
        matched = pageSource.Contains("comment") 
               && (pageSource.Contains("post_detail") 
                   || pageSource.Contains("com.reddit.frontpage:id/detail"));
    }
    
    project.Variables["zd_verify_pass"].Value = matched ? "true" : "false";
    project.Variables["current_page"].Value = matched ? expectedPage : "unknown";
    
    project.SendInfoToLog("[Verify] 结果: " + (matched ? "PASS" : "FAIL") 
        + " (expected=" + expectedPage + ")");
    
    return matched ? "PASS" : "FAIL";
}
catch (System.Exception ex)
{
    project.Variables["zd_verify_pass"].Value = "false";
    project.SendErrorToLog("[Verify] 异常: " + ex.Message);
    return "ERROR";
}
```

### 3.7 DPS_CheckResult（块 11）

**文件**: `ZDProjects\DPS_CheckResult_OwnCode.cs`

将该文件全部内容复制粘贴到 C# code 块。

### 3.8 DPS_Finalize（块 13）

**文件**: `ZDProjects\DPS_Finalize_OwnCode.cs`

将该文件全部内容复制粘贴到 C# code 块。

---

## 四、ZD 原生动作块配置

### 4.1 Touch Emulation — 普通 Tap（块 9b）

| 参数 | 值 |
|------|-----|
| Action | Touch |
| X | `{-Variable.zd_tap_x-}` |
| Y | `{-Variable.zd_tap_y-}` |
| Touch type | Normal |
| Spread | Normal |
| Duration | 50-150 ms |

### 4.2 Touch Emulation — Long Tap（块 9c）

| 参数 | 值 |
|------|-----|
| Action | Touch |
| X | `{-Variable.zd_tap_x-}` |
| Y | `{-Variable.zd_tap_y-}` |
| Touch type | Long |
| Spread | Normal |
| Duration | 800-1200 ms |

### 4.3 Swipe Emulation（块 9d）

| 参数 | 值 |
|------|-----|
| Action | Swipe |
| X from | `{-Variable.zd_swipe_x1-}` |
| Y from | `{-Variable.zd_swipe_y1-}` |
| X to | `{-Variable.zd_swipe_x2-}` |
| Y to | `{-Variable.zd_swipe_y2-}` |
| Duration | 600-1000 ms |
| Spread | Normal |

### 4.4 Back Key（块 9e）

| 参数 | 值 |
|------|-----|
| Action | Keyboard |
| Key | `{AndroidKeys.BACK}` |

### 4.5 Pause（块 9f）

| 参数 | 值 |
|------|-----|
| Action | Pause |
| Duration | `{-Variable.zd_wait_sec-}` **秒** |

---

## 五、Switch 配置

### 5.1 外层 If 判断

**块 2: Init 结果判断**
```
条件: {-Variable.session_init_result-} == "SUCCESS"
绿色(true) → 进入外层循环
红色(false) → BadEnd
```

**块 5: DecideAction 结果判断**
```
条件: {-Variable.next_action-} == "END"
绿色(true) → 跳出外层循环 → Finalize
红色(false) → 进入 StepRunner
```

**块 12: CheckResult 结果判断**
```
条件: {-Variable.eval_result-} == "END"
绿色(true) → 跳出外层循环 → Finalize
红色(false) → 回到外层循环顶部
```

### 5.2 内层循环条件

**块 7: Inner Loop**
```
循环条件: {-Variable.zd_step_index-} < {-Variable.zd_step_count-}
```

> 注意: ZD 的 Loop 可能不直接支持 "<" 比较。替代方案:
> 使用 If 条件块 + 箭头跳转模拟循环。
> 具体: StepAdvancer 返回 "DONE" 时跳出, 返回 "NEXT" 时回到 Switch。

### 5.3 Switch 步骤路由（块 8）

**Switch 变量**: `{-Variable.zd_step_type-}`

| Case 值 | 连接到 | 说明 |
|---------|--------|------|
| `L` | 块 9a: C# Locate | 查找元素 |
| `T` | 块 9b: ZD Touch (Normal) | 普通点击 |
| `T:long` | 块 9c: ZD Touch (Long) | 长按 |
| `S` | 块 9d: ZD Swipe | 滑动 |
| `B` | 块 9e: ZD Back Key | 返回键 |
| `W` | 块 9f: ZD Pause | 等待 |
| `V` | 块 9g: C# Verify | 页面验证 |

**所有 Case 的输出箭头** → 连接到块 10 (StepAdvancer)。

---

## 六、步骤计划配置文件

### 6.1 文件位置

```
Config/StepPlans/reddit_step_plans.json
```

### 6.2 文件内容

```json
{
    "platform": "reddit",
    "version": "1.0",
    "description": "Reddit 操作步骤计划 (v4.7 数据驱动格式)",
    "plans": {
        "browse":              "L:post_unit|W:5|S:down_900|W:2",
        "like":                "L:upvote_button|T|W:1",
        "downvote_post":       "L:downvote_button|T|W:1",
        "undo_vote":           "L:upvote_button|T|W:1",
        "open_post":           "L:post_unit|T|W:3|V:post_detail",
        "open_comments":       "L:comment_button|T|W:3|V:post_detail",
        "tap_subreddit":       "L:subreddit_name|T|W:3",
        "tap_author":          "L:post_header|T|W:3",
        "scroll_to_top":       "L:toolbar|T|W:2",
        "pull_refresh":        "S:up_1200|W:3",
        "share":               "L:share_button|T|W:3|B|W:1",

        "save_post":           "L:post_overflow|T|L:text_Save|T|W:1",
        "hide_post":           "L:post_overflow|T|L:text_Hide post|T|W:1",
        "report_post":         "L:post_overflow|T|L:text_Report|T|W:1",
        "copy_link":           "L:post_overflow|T|L:text_Copy link|T|W:1",
        "block_author":        "L:post_overflow|T|L:text_Block account|T|W:1",
        "mute_subreddit":      "L:post_overflow|T|L:text_Mute|T|W:1",
        "dismiss_ad":          "L:post_overflow|T|L:text_Hide|T|W:1",
        "switch_feed_home":    "L:feed_options|T|L:text_Home|T|W:2",
        "switch_feed_popular": "L:feed_options|T|L:text_Popular|T|W:2",
        "switch_feed_latest":  "L:feed_options|T|L:text_Latest|T|W:2",
        "sort_feed_hot":       "L:feed_options|T|L:text_Hot|T|W:2",
        "sort_feed_new":       "L:feed_options|T|L:text_New|T|W:2",
        "sort_feed_top":       "L:feed_options|T|L:text_Top|T|W:2",
        "sort_feed_rising":    "L:feed_options|T|L:text_Rising|T|W:2",

        "read_post":           "L:post_body|W:8|S:down_400|W:4",
        "back_to_feed":        "L:back_button|T|W:3|V:feed",
        "upvote_detail":       "L:vote_upvote|T|W:1",
        "downvote_detail":     "L:vote_downvote|T|W:1",
        "comment":             "L:comment_input|T|W:3|L:submit_button|T|W:2",
        "scroll_comments":     "S:down_600|W:3|S:down_600|W:3|S:down_400|W:2",
        "open_media_fullscreen":"L:media_video_view|T|W:3",
        "play_video":          "L:media_play_button|T|W:5",
        "load_more_comments":  "L:text_more repl|T|W:3",
        "upvote_comment":      "S:down_800|W:1|L:comment_vote_upvote|T|W:1",
        "downvote_comment":    "S:down_800|W:1|L:comment_vote_downvote|T|W:1",
        "collapse_thread":     "S:down_800|W:1|L:comment_header|T|W:1",
        "expand_thread":       "S:down_800|W:1|L:comment_header|T|W:1",
        "reply_comment":       "L:comment_reply_button|T|W:2|L:comment_text_input|T|W:3|L:text_Reply|T|W:2",
        "sort_comments":       "L:detail_comment_sort|T|L:text_Best|T|W:1",

        "save_post_detail":    "L:detail_overflow|T|L:text_Save|T|W:1",
        "share_post_detail":   "L:detail_share|T|W:3|B|W:1",
        "copy_link_detail":    "L:detail_overflow|T|L:text_Copy link|T|W:1",
        "hide_post_detail":    "L:detail_overflow|T|L:text_Hide post|T|W:1",
        "report_post_detail":  "L:detail_overflow|T|L:text_Report|T|W:1",
        "crosspost":           "L:detail_overflow|T|L:text_Crosspost|T|W:1",
        "follow_author_detail":"L:detail_overflow|T|L:text_Follow|T|W:1",

        "save_comment":        "L:comment_body|T:long|L:text_Save|T|W:1",
        "report_comment":      "L:comment_body|T:long|L:text_Report|T|W:1",

        "follow":              "L:post_header|T|W:3|L:follow_button|T|W:2|B|W:1",
        "nav_home":            "L:bottom_nav_home|T|W:3",
        "nav_create":          "L:bottom_nav_create|T|W:3",
        "nav_inbox":           "L:bottom_nav_inbox|T|W:3",
        "nav_profile":         "L:bottom_nav_profile|T|W:3"
    }
}
```

---

## 七、最小闭环测试

### 7.1 测试目标

验证 Locate → Switch → Touch/Swipe 链路在真机上跑通。

### 7.2 简化测试流程（不依赖 SessionRunner）

只搭建以下 5 个块：

```
[1. C# StepRunner_Test]  ← 硬编码 browse 计划
    |
[2. Switch: zd_step_type]
    L → [3. C# Locate]
    S → [4. ZD Swipe]
    W → [5. ZD Pause]
    |
    ↓ (所有 Case 输出)
[6. C# StepAdvancer]
    | "NEXT" → 回到 [2]
    | "DONE" → [GoodEnd]
```

**块 1: StepRunner_Test（测试用简化版）**

```csharp
// 测试用: 硬编码 browse 步骤计划
project.Variables["zd_step_plan"].Value = "L:post_unit|W:3|S:down_900|W:2";
project.Variables["zd_step_count"].Value = "4";
project.Variables["zd_step_index"].Value = "0";

// 解析第一步: L:post_unit
project.Variables["zd_step_type"].Value = "L";
project.Variables["zd_step_arg"].Value = "post_unit";
project.Variables["project_root"].Value = @"D:\ZennoDroid-AI\DPS_v4.5\";
project.Variables["current_platform"].Value = "reddit";

project.SendInfoToLog("[Test] 步骤计划已设置, 共 4 步");
return "OK";
```

### 7.3 运行步骤

1. 手机打开 Reddit，停在 feed 首页
2. ProjectMaker 选择设备
3. 点 Run
4. 观察: 停顿(Locate找帖子) → 等3秒 → 下滑一屏 → 等2秒

### 7.4 预期日志

```
[Test] 步骤计划已设置, 共 4 步
[Locate] 查找: post_unit
[Locate] 找到 post_unit center=(540, 800)
[StepAdvancer] 步骤 2/4: W:3
（ZD Pause 3 秒）
[StepAdvancer] 步骤 3/4: S:down_900
（ZD Swipe 执行）
[StepAdvancer] 步骤 4/4: W:2
（ZD Pause 2 秒）
[StepAdvancer] 所有步骤完成 (4/4)
```

---

## 八、扩展新操作（只需加数据）

新增操作只需 2 步：

1. **在 `reddit_step_plans.json` 中加一行**:
```json
"new_action": "L:some_selector|T|W:2"
```

2. **如果用了新 selector，在 `PlatformsConfig.json` 的 `ui_selectors` 中加定义**:
```json
"some_selector": {
    "resource_id": "com.reddit.frontpage:id/some_element",
    "content_desc": "Some Element"
}
```

不需要改 ZD 流程图，不需要改 C# 代码。

---

## 九、给 GPT/Gemini 的使用说明

> 将本文档完整发送给 GPT/Gemini，然后用以下 prompt：

```
我正在按照这份配置说明书在 ZennoDroid ProjectMaker 中搭建 DPS v4.7 项目。
当前进度: [描述你到了哪一步]
遇到的问题: [描述具体问题]
请根据说明书帮我解决。
```

**GPT/Gemini 可以帮你的事:**
- 解释每个块的作用和连接方式
- 调试 C# code cube 的编译错误
- 修改步骤计划以适应不同 APP
- 添加新的 selector 定义
- 调整 Swipe 参数适配不同分辨率

**GPT/Gemini 无法帮你的事:**
- ZD ProjectMaker 的 GUI 操作（需要你自己拖拽块和连线）
- 真机调试（需要你观察手机屏幕）
- 具体的 resource-id 值（需要用 ZD 的 Layout Inspector 或 UIAutomator 查看）

---

## 十、故障排查

| 现象 | 原因 | 解决 |
|------|------|------|
| Locate 始终找不到元素 | resource-id 变了 | 用 Layout Inspector 重新获取 |
| Tap 点错位置 | 分辨率不匹配 | 检查 zd_tap_x/y 日志，调整 |
| Swipe 无效 | 坐标超出屏幕 | 检查 zd_swipe_* 变量值 |
| Pause 时长不对 | 单位混淆（秒 vs 毫秒） | 确认 zd_wait_sec 是秒 |
| StepAdvancer 死循环 | step_index 未递增 | 检查 StepAdvancer 代码 |
| Switch 无匹配 | zd_step_type 值有空格 | 检查 step_plan 格式 |
| C# 编译错误 | 缺少引用 | 确认 coreFiles 列表完整 |
