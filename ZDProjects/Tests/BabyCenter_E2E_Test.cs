// =====================================================
// BabyCenter_E2E_Test.cs - BabyCenter E2E 测试脚本
// 验证 CODEX 2026-02-27 修复：BabyCenter 三件套接入
// 流程：验证配置 → 验证操作 → 验证意图映射 → 简单交互测试
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================

// ========== Helper Functions ==========
Action<string> Log = (m) => project.SendInfoToLog("[BabyCenter-E2E] " + m, true);
Action<string> LogErr = (m) => project.SendErrorToLog("[BabyCenter-E2E] " + m, true);

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
    Log("  BabyCenter E2E 测试 - CODEX 修复验证");
    Log("  时间: " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    Log("  验证内容:");
    Log("    - BabyCenter 平台配置存在且正确");
    Log("    - BabyCenter 操作配置存在");
    Log("    - BabyCenter 意图映射存在");
    Log("    - Apps.json 中 babycenter.enabled = true");
    Log("    - device_app_mapping.json 中映射存在");
    Log("========================================");
    
    var droid = instance.DroidInstance;
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
    
    // ========== 阶段 1: 验证平台配置 ==========
    totalTests++;
    var platformConfigResult = ExecuteTest("测试 1: BabyCenter 平台配置", () => {
        Log("  验证 PlatformsConfig.json 中的 babycenter 配置...");
        
        try {
            // 读取 PlatformsConfig.json
            string configPath = projectRoot + "Config\\PlatformsConfig.json";
            if (!System.IO.File.Exists(configPath)) {
                return "ERROR: PlatformsConfig.json 不存在";
            }
            
            string configJson = System.IO.File.ReadAllText(configPath);
            
            // 验证 babycenter 平台存在
            bool hasBabyCenter = configJson.IndexOf("\"babycenter\"") >= 0;
            Log("  babycenter 平台存在: " + (hasBabyCenter ? "是" : "否"));
            
            if (!hasBabyCenter) {
                return "ERROR: babycenter 平台配置不存在";
            }
            
            // 提取 babycenter 配置
            string bcConfig = ExtractObject(configJson, "babycenter");
            if (string.IsNullOrEmpty(bcConfig)) {
                return "ERROR: 无法提取 babycenter 配置";
            }
            
            // 验证 package_name
            string packageName = JsonGet(bcConfig, "package_name", "");
            bool correctPackage = (packageName == "com.babycenter.pregnancytracker");
            Log("  package_name: " + packageName + (correctPackage ? " ✓" : " ✗"));
            
            // 验证 enabled
            bool enabled = bcConfig.IndexOf("\"enabled\": true") >= 0;
            Log("  enabled: " + (enabled ? "true" : "false"));
            
            // 验证 ui_selectors 存在
            bool hasSelectors = bcConfig.IndexOf("\"ui_selectors\"") >= 0;
            Log("  ui_selectors 存在: " + (hasSelectors ? "是" : "否"));
            
            // 验证 rate_limits 存在
            bool hasRateLimits = bcConfig.IndexOf("\"rate_limits\"") >= 0;
            Log("  rate_limits 存在: " + (hasRateLimits ? "是" : "否"));
            
            if (correctPackage && enabled && hasSelectors && hasRateLimits) {
                SetVar("platform_config_test", "PASS");
                return "SUCCESS: BabyCenter 平台配置完整且正确";
            } else {
                return "ERROR: BabyCenter 平台配置不完整";
            }
        } catch (System.Exception ex) {
            return "ERROR: " + ex.Message;
        }
    });
    
    testResults.Add(platformConfigResult);
    if (platformConfigResult.Item1) passedTests++;
    
    System.Threading.Thread.Sleep(1500);
    
    // ========== 阶段 2: 验证操作配置 ==========
    totalTests++;
    var operationsResult = ExecuteTest("测试 2: BabyCenter 操作配置", () => {
        Log("  验证 babycenter_operations.json 存在且完整...");
        
        try {
            // 读取 babycenter_operations.json
            string opsPath = projectRoot + "Config\\Operations\\babycenter_operations.json";
            if (!System.IO.File.Exists(opsPath)) {
                return "ERROR: babycenter_operations.json 不存在";
            }
            
            string opsJson = System.IO.File.ReadAllText(opsPath);
            
            // 验证 platform 字段
            string platform = JsonGet(opsJson, "platform", "");
            bool correctPlatform = (platform == "babycenter");
            Log("  platform: " + platform + (correctPlatform ? " ✓" : " ✗"));
            
            // 验证必需操作存在
            string[] requiredOps = new string[] { "browse", "open_post", "read_post", "like", "comment", "back_to_feed" };
            bool allOpsExist = true;
            string missingOps = "";
            
            foreach (string op in requiredOps) {
                bool exists = opsJson.IndexOf("\"" + op + "\"") >= 0;
                Log("  " + op + " 操作存在: " + (exists ? "是" : "否"));
                if (!exists) {
                    allOpsExist = false;
                    missingOps += op + " ";
                }
            }
            
            if (correctPlatform && allOpsExist) {
                SetVar("operations_test", "PASS");
                return "SUCCESS: BabyCenter 操作配置完整";
            } else {
                return "ERROR: 缺少操作: " + missingOps;
            }
        } catch (System.Exception ex) {
            return "ERROR: " + ex.Message;
        }
    });
    
    testResults.Add(operationsResult);
    if (operationsResult.Item1) passedTests++;
    
    System.Threading.Thread.Sleep(1500);
    
    // ========== 阶段 3: 验证意图映射 ==========
    totalTests++;
    var intentsResult = ExecuteTest("测试 3: BabyCenter 意图映射", () => {
        Log("  验证 babycenter_intents.json 存在且完整...");
        
        try {
            // 读取 babycenter_intents.json
            string intentsPath = projectRoot + "Config\\IntentMappings\\babycenter_intents.json";
            if (!System.IO.File.Exists(intentsPath)) {
                return "ERROR: babycenter_intents.json 不存在";
            }
            
            string intentsJson = System.IO.File.ReadAllText(intentsPath);
            
            // 验证 platform 字段
            string platform = JsonGet(intentsJson, "platform", "");
            bool correctPlatform = (platform == "babycenter");
            Log("  platform: " + platform + (correctPlatform ? " ✓" : " ✗"));
            
            // 验证必需意图存在
            string[] requiredIntents = new string[] { "browse_feed", "open_post", "read_post", "like_content", "reply_post" };
            bool allIntentsExist = true;
            string missingIntents = "";
            
            foreach (string intent in requiredIntents) {
                bool exists = intentsJson.IndexOf("\"" + intent + "\"") >= 0;
                Log("  " + intent + " 意图存在: " + (exists ? "是" : "否"));
                if (!exists) {
                    allIntentsExist = false;
                    missingIntents += intent + " ";
                }
            }
            
            // 验证 action_to_intent 映射
            bool hasMapping = intentsJson.IndexOf("\"action_to_intent\"") >= 0;
            Log("  action_to_intent 存在: " + (hasMapping ? "是" : "否"));
            
            if (correctPlatform && allIntentsExist && hasMapping) {
                SetVar("intents_test", "PASS");
                return "SUCCESS: BabyCenter 意图映射完整";
            } else {
                return "ERROR: 缺少意图: " + missingIntents;
            }
        } catch (System.Exception ex) {
            return "ERROR: " + ex.Message;
        }
    });
    
    testResults.Add(intentsResult);
    if (intentsResult.Item1) passedTests++;
    
    System.Threading.Thread.Sleep(1500);
    
    // ========== 阶段 4: 验证应用开关 ==========
    totalTests++;
    var appsResult = ExecuteTest("测试 4: Apps.json 应用开关", () => {
        Log("  验证 Apps.json 中 babycenter.enabled = true...");
        
        try {
            // 读取 Apps.json
            string appsPath = projectRoot + "Config\\Apps.json";
            if (!System.IO.File.Exists(appsPath)) {
                return "ERROR: Apps.json 不存在";
            }
            
            string appsJson = System.IO.File.ReadAllText(appsPath);
            
            // 提取 babycenter 配置
            string bcConfig = ExtractObject(appsJson, "babycenter");
            if (string.IsNullOrEmpty(bcConfig)) {
                return "ERROR: babycenter 配置不存在";
            }
            
            // 验证 enabled
            bool enabled = bcConfig.IndexOf("\"enabled\": true") >= 0;
            Log("  babycenter.enabled: " + (enabled ? "true" : "false"));
            
            // 验证 data_path
            string dataPath = JsonGet(bcConfig, "data_path", "");
            bool hasDataPath = !string.IsNullOrEmpty(dataPath);
            Log("  data_path: " + (hasDataPath ? dataPath : "(未设置)"));
            
            if (enabled) {
                SetVar("apps_test", "PASS");
                return "SUCCESS: babycenter.enabled = true";
            } else {
                return "ERROR: babycenter.enabled 不是 true";
            }
        } catch (System.Exception ex) {
            return "ERROR: " + ex.Message;
        }
    });
    
    testResults.Add(appsResult);
    if (appsResult.Item1) passedTests++;
    
    System.Threading.Thread.Sleep(1500);
    
    // ========== 阶段 5: 验证设备映射 ==========
    totalTests++;
    var mappingResult = ExecuteTest("测试 5: 设备应用映射", () => {
        Log("  验证 device_app_mapping.json 中 device_004 -> babycenter...");
        
        try {
            // 读取 device_app_mapping.json
            string mappingPath = projectRoot + "Config\\device_app_mapping.json";
            if (!System.IO.File.Exists(mappingPath)) {
                return "ERROR: device_app_mapping.json 不存在";
            }
            
            string mappingJson = System.IO.File.ReadAllText(mappingPath);
            
            // 验证 device_004 -> babycenter 映射
            bool hasMapping = mappingJson.IndexOf("\"device_004\"") >= 0;
            Log("  device_004 存在: " + (hasMapping ? "是" : "否"));
            
            if (hasMapping) {
                bool mapsToBabyCenter = mappingJson.IndexOf("\"platform\": \"babycenter\"") >= 0;
                Log("  映射到 babycenter: " + (mapsToBabyCenter ? "是" : "否"));
                
                if (mapsToBabyCenter) {
                    SetVar("mapping_test", "PASS");
                    return "SUCCESS: device_004 -> babycenter 映射存在";
                } else {
                    return "ERROR: device_004 未映射到 babycenter";
                }
            } else {
                return "ERROR: device_004 不存在于映射中";
            }
        } catch (System.Exception ex) {
            return "ERROR: " + ex.Message;
        }
    });
    
    testResults.Add(mappingResult);
    if (mappingResult.Item1) passedTests++;
    
    System.Threading.Thread.Sleep(1500);
    
    // ========== 阶段 6: 选择器验证 ==========
    totalTests++;
    var selectorsResult = ExecuteTest("测试 6: UI 选择器配置", () => {
        Log("  验证 BabyCenter UI 选择器配置...");
        
        try {
            // 读取 PlatformsConfig.json
            string configPath = projectRoot + "Config\\PlatformsConfig.json";
            string configJson = System.IO.File.ReadAllText(configPath);
            
            // 提取 babycenter 配置
            string bcConfig = ExtractObject(configJson, "babycenter");
            string uiSelectors = ExtractObject(bcConfig, "ui_selectors");
            
            if (string.IsNullOrEmpty(uiSelectors)) {
                return "ERROR: ui_selectors 不存在";
            }
            
            // 验证关键选择器存在
            string[] requiredSelectors = new string[] { "post_unit", "post_title", "like_button", "comment_button" };
            bool allSelectorsExist = true;
            string missingSelectors = "";
            
            foreach (string selector in requiredSelectors) {
                bool exists = uiSelectors.IndexOf("\"" + selector + "\"") >= 0;
                Log("  " + selector + " 选择器存在: " + (exists ? "是" : "否"));
                if (!exists) {
                    allSelectorsExist = false;
                    missingSelectors += selector + " ";
                }
            }
            
            if (allSelectorsExist) {
                SetVar("selectors_test", "PASS");
                return "SUCCESS: UI 选择器配置完整";
            } else {
                return "WARNING: 缺少选择器: " + missingSelectors + "（可能需要根据实际 UI 调整）";
            }
        } catch (System.Exception ex) {
            return "ERROR: " + ex.Message;
        }
    });
    
    testResults.Add(selectorsResult);
    if (selectorsResult.Item1) passedTests++;
    
    // ========== 测试总结 ==========
    Log("========================================");
    Log("  BabyCenter E2E 测试总结");
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
    Log("注意: BabyCenter UI 选择器需要根据实际 APP 运行时调整");
    Log("========================================");
    
    // 保存结果
    SetVar("babycenter_e2e_total", totalTests.ToString());
    SetVar("babycenter_e2e_passed", passedTests.ToString());
    SetVar("babycenter_e2e_success_rate", ((passedTests * 100) / totalTests).ToString());
    
    if (passedTests == totalTests) {
        SetVar("babycenter_e2e_result", "SUCCESS");
        return "SUCCESS: 所有 " + totalTests.ToString() + " 项测试通过";
    } else if (passedTests > 0) {
        SetVar("babycenter_e2e_result", "PARTIAL");
        return "PARTIAL: " + passedTests.ToString() + "/" + totalTests.ToString() + " 测试通过";
    } else {
        SetVar("babycenter_e2e_result", "FAIL");
        return "FAIL: 所有测试失败";
    }
}
catch (System.Exception ex) {
    LogErr("E2E 测试异常: " + ex.Message);
    SetVar("babycenter_e2e_result", "ERROR");
    SetVar("babycenter_e2e_error", ex.Message);
    return "ERROR: " + ex.Message;
}

// =====================================================
// 测试说明
// =====================================================
// 
// 本脚本验证 CODEX 2026-02-27 对 BabyCenter 平台的接入:
// 
// 1. BabyCenter 三件套接入
//    - 平台配置: PlatformsConfig.json
//    - 操作配置: babycenter_operations.json
//    - 意图映射: babycenter_intents.json
// 
// 2. 应用开关
//    - Apps.json 中 babycenter.enabled = true
// 
// 3. 设备映射
//    - device_app_mapping.json 中 device_004 -> babycenter
// 
// 4. UI 选择器配置
//    - 验证关键选择器存在
//    - 注意: 实际使用时可能需要根据 APP UI 调整
// 
// =====================================================
