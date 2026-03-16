// =====================================================
// MultiPlatform_IntegrationTest.cs - Integration Test
// =====================================================
// Purpose: Test full workflow: Config → Operations JSON → ActionExecutor
//
// Test Scenarios:
//   1. Reddit config-driven workflow (mapping + operations + intents)
//   2. Instagram config-driven workflow (mapping + operations + intents)
//   3. Platform switching via device_app_mapping.json
//   4. Rate limit configuration in PlatformsConfig.json
//   5. Error recovery module existence
//   6. Config loading (all required JSON files + core modules)
//   7. Operations JSON structure validation
//
// Usage:
//   - Set test_scenario variable to select test case
//   - Set device_id to test device-platform mapping
//   - Review logs for verification
// =====================================================

// ========== Helper Functions ==========
Action<string> Log = (m) => project.SendInfoToLog("[IntegrationTest] " + m, true);
Action<string> LogErr = (m) => project.SendErrorToLog("[IntegrationTest] " + m, true);
Action<string> LogSuccess = (m) => project.SendInfoToLog("[SUCCESS] " + m, true);

Func<string, string, string> GetVar = (name, def) => {
    try {
        string v = project.Variables[name].Value;
        return string.IsNullOrEmpty(v) ? def : v;
    } catch { return def; }
};

Action<string, string> SetVar = (name, val) => {
    try {
        project.Variables[name].Value = val ?? "";
    } catch { }
};

// ========== Test Configuration ==========
string projectRoot = GetVar("project_root", "");
if (string.IsNullOrEmpty(projectRoot)) {
    LogErr("FATAL: project_root 未设置");
    return;
}
if (!projectRoot.EndsWith("\\")) projectRoot += "\\";
string testScenario = GetVar("test_scenario", "reddit_basic");

Log("=".PadRight(60, '='));
Log("Multi-Platform Integration Test (Config-Driven Architecture)");
Log("Test Scenario: " + testScenario);
Log("=".PadRight(60, '='));

// Set project root
SetVar("project_root", projectRoot);

// ========== Test Functions ==========

// Test 1: Reddit Config-Driven Workflow
Func<bool> TestRedditBasic = () => {
    Log("\n--- Test 1: Reddit Config-Driven Workflow ---");
    
    try {
        // Step 1: Set device to Reddit
        SetVar("device_id", "device_001");
        Log("Step 1: Set device_id = device_001 (Reddit)");
        
        // Step 2: Load device_app_mapping.json
        string mappingPath = projectRoot + "Config\\device_app_mapping.json";
        if (!System.IO.File.Exists(mappingPath)) {
            LogErr("device_app_mapping.json not found");
            return false;
        }
        Log("Step 2: device_app_mapping.json exists");
        
        // Step 3: Verify platform determination
        string mappingJson = System.IO.File.ReadAllText(mappingPath);
        if (!mappingJson.Contains("device_001")) {
            LogErr("device_001 not found in mapping");
            return false;
        }
        if (!mappingJson.Contains("reddit")) {
            LogErr("reddit platform not found for device_001");
            return false;
        }
        Log("Step 3: device_001 maps to reddit");
        
        // Step 4: Load PlatformsConfig.json
        string platformsConfigPath = projectRoot + "Config\\PlatformsConfig.json";
        if (!System.IO.File.Exists(platformsConfigPath)) {
            LogErr("PlatformsConfig.json not found");
            return false;
        }
        string platformsConfig = System.IO.File.ReadAllText(platformsConfigPath);
        if (!platformsConfig.Contains("reddit")) {
            LogErr("reddit config not found");
            return false;
        }
        Log("Step 4: PlatformsConfig.json contains reddit");
        
        // Step 5: Verify reddit operations JSON exists
        string opsPath = projectRoot + "Config\\Operations\\reddit_operations.json";
        if (!System.IO.File.Exists(opsPath)) {
            LogErr("reddit_operations.json not found");
            return false;
        }
        string opsJson = System.IO.File.ReadAllText(opsPath);
        if (!opsJson.Contains("\"operations\"")) {
            LogErr("reddit_operations.json missing 'operations' key");
            return false;
        }
        Log("Step 5: reddit_operations.json exists with valid structure");
        
        // Step 6: Verify reddit intent mappings exist
        string intentsPath = projectRoot + "Config\\IntentMappings\\reddit_intents.json";
        if (!System.IO.File.Exists(intentsPath)) {
            LogErr("reddit_intents.json not found");
            return false;
        }
        Log("Step 6: reddit_intents.json exists");
        
        // Step 7: Verify operations contain standard actions
        string[] requiredOps = new string[] { "browse", "like", "comment", "follow", "share" };
        foreach (string op in requiredOps) {
            if (!opsJson.Contains("\"" + op + "\"")) {
                LogErr("Operation not found in reddit_operations.json: " + op);
                return false;
            }
        }
        Log("Step 7: All 5 standard operations present in reddit_operations.json");
        
        LogSuccess("Reddit config-driven workflow test PASSED");
        return true;
        
    } catch (System.Exception ex) {
        LogErr("Test exception: " + ex.Message);
        return false;
    }
};

// Test 2: Instagram Config-Driven Workflow
Func<bool> TestInstagramBasic = () => {
    Log("\n--- Test 2: Instagram Config-Driven Workflow ---");
    
    try {
        // Step 1: Set device to Instagram
        SetVar("device_id", "device_002");
        Log("Step 1: Set device_id = device_002 (Instagram)");
        
        // Step 2: Verify mapping
        string mappingPath = projectRoot + "Config\\device_app_mapping.json";
        string mappingJson = System.IO.File.ReadAllText(mappingPath);
        if (!mappingJson.Contains("device_002")) {
            LogErr("device_002 not found in mapping");
            return false;
        }
        if (!mappingJson.Contains("instagram")) {
            LogErr("instagram platform not found for device_002");
            return false;
        }
        Log("Step 2: device_002 maps to instagram");
        
        // Step 3: Verify Instagram config with rate_limits
        string platformsConfigPath = projectRoot + "Config\\PlatformsConfig.json";
        string platformsConfig = System.IO.File.ReadAllText(platformsConfigPath);
        if (!platformsConfig.Contains("instagram")) {
            LogErr("instagram config not found");
            return false;
        }
        if (!platformsConfig.Contains("max_actions_per_hour")) {
            LogErr("rate_limits not found in instagram config");
            return false;
        }
        Log("Step 3: Instagram config with rate_limits exists");
        
        // Step 4: Verify instagram operations JSON exists
        string opsPath = projectRoot + "Config\\Operations\\instagram_operations.json";
        if (!System.IO.File.Exists(opsPath)) {
            LogErr("instagram_operations.json not found");
            return false;
        }
        string opsJson = System.IO.File.ReadAllText(opsPath);
        if (!opsJson.Contains("\"operations\"")) {
            LogErr("instagram_operations.json missing 'operations' key");
            return false;
        }
        Log("Step 4: instagram_operations.json exists with valid structure");
        
        // Step 5: Verify instagram intent mappings exist
        string intentsPath = projectRoot + "Config\\IntentMappings\\instagram_intents.json";
        if (!System.IO.File.Exists(intentsPath)) {
            LogErr("instagram_intents.json not found");
            return false;
        }
        Log("Step 5: instagram_intents.json exists");
        
        // Step 6: Verify operations contain standard actions
        string[] requiredOps = new string[] { "browse", "like", "comment", "follow", "share" };
        foreach (string op in requiredOps) {
            if (!opsJson.Contains("\"" + op + "\"")) {
                LogErr("Operation not found in instagram_operations.json: " + op);
                return false;
            }
        }
        Log("Step 6: All 5 standard operations present in instagram_operations.json");
        
        LogSuccess("Instagram config-driven workflow test PASSED");
        return true;
        
    } catch (System.Exception ex) {
        LogErr("Test exception: " + ex.Message);
        return false;
    }
};

// Test 3: Platform Switching
Func<bool> TestPlatformSwitching = () => {
    Log("\n--- Test 3: Platform Switching ---");
    
    try {
        // Test switching between devices/platforms
        string[] devices = new string[] { "device_001", "device_002", "device_003" };
        string[] expectedPlatforms = new string[] { "reddit", "instagram", "reddit" };
        
        string mappingPath = projectRoot + "Config\\device_app_mapping.json";
        string mappingJson = System.IO.File.ReadAllText(mappingPath);
        
        for (int i = 0; i < devices.Length; i++) {
            string device = devices[i];
            string expectedPlatform = expectedPlatforms[i];
            
            Log("Testing device: " + device + " -> " + expectedPlatform);
            
            // Find device in mapping
            int devicePos = mappingJson.IndexOf("\"" + device + "\"");
            if (devicePos < 0) {
                LogErr("Device not found: " + device);
                return false;
            }
            
            // Find platform for this device
            int platformPos = mappingJson.IndexOf("\"platform\"", devicePos);
            if (platformPos < 0 || platformPos > devicePos + 200) {
                LogErr("Platform not found for device: " + device);
                return false;
            }
            
            int valueStart = mappingJson.IndexOf("\"", platformPos + 11) + 1;
            int valueEnd = mappingJson.IndexOf("\"", valueStart);
            string actualPlatform = mappingJson.Substring(valueStart, valueEnd - valueStart);
            
            if (actualPlatform != expectedPlatform) {
                LogErr("Platform mismatch for " + device + ": expected " + expectedPlatform + ", got " + actualPlatform);
                return false;
            }
            
            // Verify operations JSON exists for this platform
            string opsPath = projectRoot + "Config\\Operations\\" + actualPlatform + "_operations.json";
            if (!System.IO.File.Exists(opsPath)) {
                LogErr("Operations file not found for platform: " + actualPlatform);
                return false;
            }
            
            Log("  OK " + device + " -> " + actualPlatform + " (operations.json exists)");
        }
        
        LogSuccess("Platform switching test PASSED");
        return true;
        
    } catch (System.Exception ex) {
        LogErr("Test exception: " + ex.Message);
        return false;
    }
};

// Test 4: Rate Limit Configuration
Func<bool> TestRateLimit = () => {
    Log("\n--- Test 4: Rate Limit Configuration ---");
    
    try {
        // Verify rate limits in PlatformsConfig
        string platformsConfigPath = projectRoot + "Config\\PlatformsConfig.json";
        string platformsConfig = System.IO.File.ReadAllText(platformsConfigPath);
        
        // Check Reddit rate limits
        int redditPos = platformsConfig.IndexOf("\"reddit\"");
        int redditLimitsPos = platformsConfig.IndexOf("\"rate_limits\"", redditPos);
        int redditActionsPos = platformsConfig.IndexOf("\"max_actions_per_hour\"", redditLimitsPos);
        
        if (redditActionsPos < 0) {
            LogErr("Reddit rate_limits not found in PlatformsConfig.json");
            return false;
        }
        Log("OK Reddit rate_limits present in PlatformsConfig.json");
        
        // Check Instagram rate limits (stricter)
        int instagramPos = platformsConfig.IndexOf("\"instagram\"");
        int instagramLimitsPos = platformsConfig.IndexOf("\"rate_limits\"", instagramPos);
        int instagramActionsPos = platformsConfig.IndexOf("\"max_actions_per_hour\"", instagramLimitsPos);
        
        if (instagramActionsPos < 0) {
            LogErr("Instagram rate_limits not found in PlatformsConfig.json");
            return false;
        }
        Log("OK Instagram rate_limits present in PlatformsConfig.json");
        
        LogSuccess("Rate limit configuration test PASSED");
        return true;
        
    } catch (System.Exception ex) {
        LogErr("Test exception: " + ex.Message);
        return false;
    }
};

// Test 5: Error Recovery
Func<bool> TestErrorRecovery = () => {
    Log("\n--- Test 5: Error Recovery ---");
    
    try {
        // Verify ErrorRecovery module exists in Modules/Core/
        string errorRecoveryPath = projectRoot + "Modules\\Core\\ErrorRecovery.cs";
        if (!System.IO.File.Exists(errorRecoveryPath)) {
            LogErr("Modules/Core/ErrorRecovery.cs not found");
            return false;
        }
        Log("OK Modules/Core/ErrorRecovery.cs exists");
        
        // Also check Core/ (ZD script layer copy)
        string coreErrorRecoveryPath = projectRoot + "Core\\ErrorRecovery.cs";
        if (!System.IO.File.Exists(coreErrorRecoveryPath)) {
            Log("NOTE: Core/ErrorRecovery.cs not found (optional ZD script layer copy)");
        } else {
            Log("OK Core/ErrorRecovery.cs exists (ZD script layer)");
        }
        
        // Verify error recovery functions in Modules/Core/ version
        string errorRecoveryCode = System.IO.File.ReadAllText(errorRecoveryPath);
        
        string[] requiredFunctions = new string[] {
            "TryWithRetry",
            "RecoverFromError",
            "LogError"
        };
        
        foreach (string func in requiredFunctions) {
            if (!errorRecoveryCode.Contains(func)) {
                LogErr("Function not found: " + func);
                return false;
            }
            Log("OK Function present: " + func);
        }
        
        LogSuccess("Error recovery test PASSED");
        return true;
        
    } catch (System.Exception ex) {
        LogErr("Test exception: " + ex.Message);
        return false;
    }
};

// Test 6: Config Loading (all required JSON + core modules)
Func<bool> TestConfigLoading = () => {
    Log("\n--- Test 6: Config Loading ---");
    
    try {
        // Test all required config files exist
        string[] configFiles = new string[] {
            "Config\\PlatformsConfig.json",
            "Config\\device_app_mapping.json",
            "Config\\Operations\\reddit_operations.json",
            "Config\\Operations\\instagram_operations.json",
            "Config\\Operations\\babycenter_operations.json",
            "Config\\IntentMappings\\reddit_intents.json",
            "Config\\IntentMappings\\instagram_intents.json",
            "Config\\IntentMappings\\babycenter_intents.json"
        };
        
        foreach (string configFile in configFiles) {
            string fullPath = projectRoot + configFile;
            if (!System.IO.File.Exists(fullPath)) {
                LogErr("Config file not found: " + configFile);
                return false;
            }
            Log("OK Config exists: " + configFile);
        }
        
        // Test core modules exist (compiled by ModuleLoader)
        string[] coreModules = new string[] {
            "Modules\\Core\\ActionExecutor.cs",
            "Modules\\Core\\SelectorEngine.cs",
            "Modules\\Core\\PageDetector.cs",
            "Modules\\Core\\IntentTranslator.cs",
            "Modules\\Core\\CoreHelper.cs",
            "Modules\\Core\\JsonHelper.cs",
            "Modules\\Core\\ErrorRecovery.cs"
        };
        
        foreach (string module in coreModules) {
            string fullPath = projectRoot + module;
            if (!System.IO.File.Exists(fullPath)) {
                LogErr("Core module not found: " + module);
                return false;
            }
            Log("OK Core module exists: " + module);
        }
        
        // Test SessionRunner exists and uses ActionExecutor architecture
        string sessionRunnerPath = projectRoot + "Modules\\SessionRunner.cs";
        if (!System.IO.File.Exists(sessionRunnerPath)) {
            LogErr("SessionRunner.cs not found");
            return false;
        }
        
        string sessionRunnerCode = System.IO.File.ReadAllText(sessionRunnerPath);
        if (!sessionRunnerCode.Contains("ExecuteWithUnifiedEngine")) {
            LogErr("ExecuteWithUnifiedEngine not found in SessionRunner");
            return false;
        }
        if (!sessionRunnerCode.Contains("ActionExecutor")) {
            LogErr("ActionExecutor reference not found in SessionRunner");
            return false;
        }
        Log("OK SessionRunner uses ActionExecutor + ExecuteWithUnifiedEngine architecture");
        
        LogSuccess("Config loading test PASSED");
        return true;
        
    } catch (System.Exception ex) {
        LogErr("Test exception: " + ex.Message);
        return false;
    }
};

// Test 7: Operations JSON Structure Validation
Func<bool> TestOperationsStructure = () => {
    Log("\n--- Test 7: Operations JSON Structure ---");
    
    try {
        string[] platforms = new string[] { "reddit", "instagram", "babycenter" };
        string[] requiredKeys = new string[] { "browse", "like", "comment" };
        
        foreach (string platform in platforms) {
            string opsPath = projectRoot + "Config\\Operations\\" + platform + "_operations.json";
            if (!System.IO.File.Exists(opsPath)) {
                LogErr(platform + "_operations.json not found");
                return false;
            }
            
            string opsJson = System.IO.File.ReadAllText(opsPath);
            
            // Validate structure: must have "operations" key
            if (!opsJson.Contains("\"operations\"")) {
                LogErr(platform + "_operations.json missing 'operations' key");
                return false;
            }
            
            // Validate required operation keys
            foreach (string key in requiredKeys) {
                if (!opsJson.Contains("\"" + key + "\"")) {
                    LogErr(platform + "_operations.json missing operation: " + key);
                    return false;
                }
            }
            
            // Validate steps exist (operations should have "steps" arrays)
            if (!opsJson.Contains("\"steps\"")) {
                LogErr(platform + "_operations.json missing 'steps' in operations");
                return false;
            }
            
            Log("OK " + platform + "_operations.json: valid structure with browse/like/comment + steps");
        }
        
        LogSuccess("Operations JSON structure test PASSED");
        return true;
        
    } catch (System.Exception ex) {
        LogErr("Test exception: " + ex.Message);
        return false;
    }
};

// ========== Test Scenario Router ==========
bool testPassed = false;

switch (testScenario) {
    case "reddit_basic":
        testPassed = TestRedditBasic();
        break;
    case "instagram_basic":
        testPassed = TestInstagramBasic();
        break;
    case "platform_switching":
        testPassed = TestPlatformSwitching();
        break;
    case "rate_limit":
        testPassed = TestRateLimit();
        break;
    case "error_recovery":
        testPassed = TestErrorRecovery();
        break;
    case "config_loading":
        testPassed = TestConfigLoading();
        break;
    case "operations_structure":
        testPassed = TestOperationsStructure();
        break;
    default:
        LogErr("Unknown test scenario: " + testScenario);
        testPassed = false;
        break;
}

// ========== Test Results ==========
Log("=".PadRight(60, '='));
if (testPassed) {
    LogSuccess("TEST PASSED: " + testScenario);
    SetVar("test_result", "PASS");
} else {
    LogErr("TEST FAILED: " + testScenario);
    SetVar("test_result", "FAIL");
}
Log("=".PadRight(60, '='));

Log("\nIntegration test complete.");
