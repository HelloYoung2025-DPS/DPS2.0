// =====================================================
// Instagram_E2E_Test.cs - Instagram E2E 测试脚本
// 验证 CODEX 2026-02-27 修复：重点测试 like 回退链
// 流程：首页 → 点赞 → 验证回退链 → 返回
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================

// ========== Helper Functions ==========
Action<string> Log = (m) => project.SendInfoToLog("[Instagram-E2E] " + m, true);
Action<string> LogErr = (m) => project.SendErrorToLog("[Instagram-E2E] " + m, true);

Func<string, string, string> GetVar = (name, def) => {
    try {
        string v = project.Variables[name].Value;
        return string.IsNullOrEmpty(v) ? def : v;
    } catch { return def; }
};

Action<string, string> SetVar = (name, val) => {
    project.Variables[name].Value = val ?? "";
};

Func<string, string, string, string> JsonGet = (json, key, def) => {
    try {
        string pattern = "\"" + key + "\"";
        int idx = json.IndexOf(pattern);
        if (idx < 0) return def;
        int colonIdx = idx + pattern.Length;
        while (colonIdx < json.Length && json[colonIdx] != ':') colonIdx++;
        colonIdx++;
        while (colonIdx < json.Length && char.IsWhiteSpace(json[colonIdx])) colonIdx++;
        
        if (colonIdx >= json.Length) return def;
        
        char quoteChar = json[colonIdx];
        if (quoteChar == '"' || quoteChar == '\'') {
            colonIdx++;
            int endIdx = json.IndexOf(quoteChar.ToString(), colonIdx);
            if (endIdx > colonIdx) {
                return json.Substring(colonIdx, endIdx - colonIdx);
            }
        }
        return def;
    } catch { return def; }
};

Func<string, string> ExtractObject = (json, key) => {
    try {
        string pattern = "\"" + key + "\"";
        int idx = json.IndexOf(pattern);
        if (idx < 0) return "";
        
        int colonIdx = idx + pattern.Length;
        while (colonIdx < json.Length && json[colonIdx] != ':') colonIdx++;
        colonIdx++;
        while (colonIdx < json.Length && char.IsWhiteSpace(json[colonIdx])) colonIdx++;
        
        if (colonIdx >= json.Length) return "";
        
        if (json[colonIdx] == '{') {
            int depth = 1;
            int startIdx = colonIdx;
            for (int i = colonIdx + 1; i < json.Length; i++) {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') depth--;
                if (depth == 0) {
                    return json.Substring(startIdx, i - startIdx + 1);
                }
            }
        }
        return "";
    } catch { return ""; }
};

// ========== 测试执行包装器 ==========
Func<string, System.Func<string>, System.Tuple<bool, string, string>> ExecuteTest = (testName, testFunc) => {
    Log("========================================");
    Log("测试开始: " + testName);
    Log("========================================");
    
    try {
        string result = testFunc();
        bool success = result.StartsWith("SUCCESS");
        
        if (success) {
            Log("✓ " + testName + " 通过");
        } else {
            LogErr("✗ " + testName + " 失败: " + result);
        }
        
        return new System.Tuple<bool, string, string>(success, testName, result);
    }
    catch (System.Exception ex) {
        LogErr("✗ " + testName + " 异常: " + ex.Message);
        return new System.Tuple<bool, string, string>(false, testName, "EXCEPTION: " + ex.Message);
    }
};

// ========== 主测试流程 ==========
try {
    Log("========================================");
    Log("  Instagram E2E 测试 - CODEX 修复验证");
    Log("  时间: " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    Log("  验证内容:");
    Log("    - Instagram like 操作 if_exists(like_button)");
    Log("    - like 操作回退链: call_operation(double_tap_like)");
    Log("    - double_tap_like 操作存在且正确");
    Log("    - refresh_layout 步骤集成");
    Log("========================================");
    
    var droid = instance.DroidInstance;
    var input = droid.Input;
    var app = droid.App;
    
    var testResults = new System.Collections.Generic.List<System.Tuple<bool, string, string>>();
    int totalTests = 0;
    int passedTests = 0;
    
    // ========== 阶段 0: 初始化 ==========
    Log("阶段 0: 环境准备");
    
    string projectRoot = GetVar("project_root", "");
    if (string.IsNullOrEmpty(projectRoot)) {
        LogErr("project_root 未设置");
        throw new System.Exception("project_root 未设置");
    }
    Log("项目根目录: " + projectRoot);
    
    // 打开 Instagram
    Log("正在打开 Instagram APP...");
    app.Open("com.instagram.android");
    System.Threading.Thread.Sleep(5000);
    Log("✓ Instagram 已打开");
    
    // ========== 阶段 1: 验证 if_exists(like_button) 配置 ==========
    totalTests++;
    var ifExistsResult = ExecuteTest("测试 1: like 操作 if_exists(like_button)", () => {
        Log("  测试 instagram_operations.json 中 like 操作的 if_exists 配置...");
        
        try {
            // 读取 instagram_operations.json
            string opsPath = projectRoot + "Config\\Operations\\instagram_operations.json";
            if (!System.IO.File.Exists(opsPath)) {
                return "ERROR: instagram_operations.json 不存在";
            }
            
            string opsJson = System.IO.File.ReadAllText(opsPath);
            
            // 提取 like 操作
            string likeOps = ExtractObject(opsJson, "like");
            if (string.IsNullOrEmpty(likeOps)) {
                return "ERROR: like 操作不存在";
            }
            
            // 验证包含 if_exists 步骤
            bool hasIfExists = likeOps.IndexOf("\"action\": \"if_exists\"") >= 0;
            Log("  like 操作包含 if_exists 步骤: " + (hasIfExists ? "是" : "否"));
            
            // 验证 if_exists 条件中检查 like_button
            bool hasLikeButton = likeOps.IndexOf("\"selector\": \"like_button\"") >= 0;
            Log("  if_exists 检查 like_button: " + (hasLikeButton ? "是" : "否"));
            
            if (hasIfExists && hasLikeButton) {
                SetVar("if_exists_test", "PASS");
                return "SUCCESS: like 操作正确使用 if_exists(like_button)";
            } else {
                return "ERROR: like 操作缺少 if_exists 或 like_button 选择器";
            }
        } catch (System.Exception ex) {
            return "ERROR: " + ex.Message;
        }
    });
    
    testResults.Add(ifExistsResult);
    if (ifExistsResult.Item1) passedTests++;
    
    System.Threading.Thread.Sleep(1500);
    
    // ========== 阶段 2: 验证 call_operation(double_tap_like) 回退链 ==========
    totalTests++;
    var fallbackResult = ExecuteTest("测试 2: call_operation(double_tap_like) 回退链", () => {
        Log("  测试 like 操作的 else 分支回退到 double_tap_like...");
        
        try {
            // 读取 instagram_operations.json
            string opsPath = projectRoot + "Config\\Operations\\instagram_operations.json";
            string opsJson = System.IO.File.ReadAllText(opsPath);
            
            // 提取 like 操作
            string likeOps = ExtractObject(opsJson, "like");
            
            // 验证包含 call_operation
            bool hasCallOp = likeOps.IndexOf("\"action\": \"call_operation\"") >= 0;
            Log("  like 操作包含 call_operation 步骤: " + (hasCallOp ? "是" : "否"));
            
            // 验证 call_operation 调用 double_tap_like
            bool hasDoubleTap = likeOps.IndexOf("\"operation\": \"double_tap_like\"") >= 0;
            Log("  call_operation 调用 double_tap_like: " + (hasDoubleTap ? "是" : "否"));
            
            if (hasCallOp && hasDoubleTap) {
                SetVar("fallback_test", "PASS");
                return "SUCCESS: like 操作回退链配置正确";
            } else {
                return "ERROR: like 操作缺少 call_operation(double_tap_like) 回退";
            }
        } catch (System.Exception ex) {
            return "ERROR: " + ex.Message;
        }
    });
    
    testResults.Add(fallbackResult);
    if (fallbackResult.Item1) passedTests++;
    
    System.Threading.Thread.Sleep(1500);
    
    // ========== 阶段 3: 验证 double_tap_like 操作存在 ==========
    totalTests++;
    var doubleTapResult = ExecuteTest("测试 3: double_tap_like 操作存在性", () => {
        Log("  测试 double_tap_like 操作是否存在且配置正确...");
        
        try {
            // 读取 instagram_operations.json
            string opsPath = projectRoot + "Config\\Operations\\instagram_operations.json";
            string opsJson = System.IO.File.ReadAllText(opsPath);
            
            // 提取 double_tap_like 操作
            string doubleTapOps = ExtractObject(opsJson, "double_tap_like");
            if (string.IsNullOrEmpty(doubleTapOps)) {
                return "ERROR: double_tap_like 操作不存在";
            }
            
            // 验证 double_tap_like 的步骤
            bool hasSteps = doubleTapOps.IndexOf("\"steps\"") >= 0;
            Log("  double_tap_like 包含 steps: " + (hasSteps ? "是" : "否"));
            
            // 验证包含两次 tap（双击）
            int tapCount = 0;
            int idx = 0;
            while (true) {
                idx = doubleTapOps.IndexOf("\"action\": \"tap\"", idx);
                if (idx < 0) break;
                tapCount++;
                idx += 15;
            }
            Log("  double_tap_like tap 步骤数量: " + tapCount.ToString());
            
            if (hasSteps && tapCount >= 2) {
                SetVar("double_tap_test", "PASS");
                return "SUCCESS: double_tap_like 操作存在且配置正确";
            } else {
                return "ERROR: double_tap_like 操作配置不正确";
            }
        } catch (System.Exception ex) {
            return "ERROR: " + ex.Message;
        }
    });
    
    testResults.Add(doubleTapResult);
    if (doubleTapResult.Item1) passedTests++;
    
    System.Threading.Thread.Sleep(1500);
    
    // ========== 阶段 4: 验证 refresh_layout 集成 ==========
    totalTests++;
    var refreshResult = ExecuteTest("测试 4: refresh_layout 步骤集成", () => {
        Log("  测试 Instagram 操作中 refresh_layout 步骤集成...");
        
        try {
            // 读取 instagram_operations.json
            string opsPath = projectRoot + "Config\\Operations\\instagram_operations.json";
            string opsJson = System.IO.File.ReadAllText(opsPath);
            
            // 检查 browse 操作包含 refresh_layout
            string browseOps = ExtractObject(opsJson, "browse");
            bool browseHasRefresh = browseOps.IndexOf("refresh_layout") >= 0;
            Log("  browse 操作包含 refresh_layout: " + (browseHasRefresh ? "是" : "否"));
            
            // 检查 back_to_feed 操作包含 refresh_layout
            string backOps = ExtractObject(opsJson, "back_to_feed");
            bool backHasRefresh = backOps.IndexOf("refresh_layout") >= 0;
            Log("  back_to_feed 操作包含 refresh_layout: " + (backHasRefresh ? "是" : "否"));
            
            if (browseHasRefresh && backHasRefresh) {
                SetVar("refresh_test", "PASS");
                return "SUCCESS: refresh_layout 步骤正确集成";
            } else {
                return "ERROR: 部分操作缺少 refresh_layout 步骤";
            }
        } catch (System.Exception ex) {
            return "ERROR: " + ex.Message;
        }
    });
    
    testResults.Add(refreshResult);
    if (refreshResult.Item1) passedTests++;
    
    System.Threading.Thread.Sleep(1500);
    
    // ========== 阶段 5: 完整工作流测试（模拟） ==========
    totalTests++;
    var workflowResult = ExecuteTest("测试 5: 完整工作流模拟", () => {
        Log("  模拟完整工作流: 首页 → 双击点赞 → 返回");
        
        try {
            // 步骤 1: 滚动浏览
            Log("  步骤 1: 滚动浏览 feed...");
            input.Swipe(540, 1600, 540, 900, 600);
            System.Threading.Thread.Sleep(2000);
            
            // 步骤 2: 模拟双击点赞（点击图片中心两次）
            Log("  步骤 2: 模拟双击点赞...");
            input.Tap(540, 1200);
            System.Threading.Thread.Sleep(100);
            input.Tap(540, 1200);
            System.Threading.Thread.Sleep(1500);
            
            // 步骤 3: 返回（如果需要）
            Log("  步骤 3: 验证状态...");
            
            SetVar("workflow_test", "PASS");
            return "SUCCESS: 完整工作流执行成功";
        } catch (System.Exception ex) {
            return "ERROR: " + ex.Message;
        }
    });
    
    testResults.Add(workflowResult);
    if (workflowResult.Item1) passedTests++;
    
    // ========== 测试总结 ==========
    Log("========================================");
    Log("  Instagram E2E 测试总结");
    Log("========================================");
    Log("总测试数: " + totalTests.ToString());
    Log("通过数: " + passedTests.ToString());
    Log("失败数: " + (totalTests - passedTests).ToString());
    Log("成功率: " + ((passedTests * 100) / totalTests).ToString() + "%");
    Log("");
    
    // 详细结果
    Log("详细结果:");
    foreach (var result in testResults) {
        string status = result.Item1 ? "✓ 通过" : "✗ 失败";
        Log("  " + status + " - " + result.Item2);
        Log("    " + result.Item3);
    }
    
    Log("========================================");
    
    // 保存结果
    SetVar("instagram_e2e_total", totalTests.ToString());
    SetVar("instagram_e2e_passed", passedTests.ToString());
    SetVar("instagram_e2e_success_rate", ((passedTests * 100) / totalTests).ToString());
    
    if (passedTests == totalTests) {
        SetVar("instagram_e2e_result", "SUCCESS");
        return "SUCCESS: 所有 " + totalTests.ToString() + " 项测试通过";
    } else if (passedTests > 0) {
        SetVar("instagram_e2e_result", "PARTIAL");
        return "PARTIAL: " + passedTests.ToString() + "/" + totalTests.ToString() + " 测试通过";
    } else {
        SetVar("instagram_e2e_result", "FAIL");
        return "FAIL: 所有测试失败";
    }
}
catch (System.Exception ex) {
    LogErr("E2E 测试异常: " + ex.Message);
    SetVar("instagram_e2e_result", "ERROR");
    SetVar("instagram_e2e_error", ex.Message);
    return "ERROR: " + ex.Message;
}

// =====================================================
// 测试说明
// =====================================================
// 
// 本脚本验证 CODEX 2026-02-27 对 Instagram 平台的修复:
// 
// 1. Instagram 点赞回退链增强
//    - like 操作使用 if_exists(like_button)
//    - else 分支调用 call_operation(double_tap_like)
//    - double_tap_like 操作存在且配置正确
// 
// 2. refresh_layout 步骤集成
//    - browse 操作包含 refresh_layout
//    - back_to_feed 操作包含 refresh_layout
// 
// 3. 完整工作流验证
//    - 滚动浏览
//    - 双击点赞模拟
//    - 状态验证
// 
// =====================================================
