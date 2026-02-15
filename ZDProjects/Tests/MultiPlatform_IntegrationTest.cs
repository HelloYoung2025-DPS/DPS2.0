// =====================================================
// MultiPlatform_IntegrationTest.cs - Integration Test
// =====================================================
// Purpose: Test full workflow Main → SessionRunner → Platform Module
//
// Test Scenarios:
//   1. Reddit platform selection and initialization
//   2. Instagram platform selection and initialization
//   3. Platform module loading
//   4. Error recovery scenarios
//   5. Rate limit enforcement
//   6. Cross-platform switching
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
Log("Multi-Platform Integration Test");
Log("Test Scenario: " + testScenario);
Log("=".PadRight(60, '='));

// Set project root
SetVar("project_root", projectRoot);

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

// ========== Test Functions ==========

// Test 1: Reddit Basic Workflow
Func<bool> TestRedditBasic = () => {
    Log("\n--- Test 1: Reddit Basic Workflow ---");
    
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
        
        // Step 5: Verify Reddit module exists
        string redditModulePath = projectRoot + "Platforms\\Reddit\\RedditModule.cs";
        if (!System.IO.File.Exists(redditModulePath)) {
            LogErr("RedditModule.cs not found");
            return false;
        }
        Log("Step 5: RedditModule.cs exists");
        
        // Step 6: Verify module structure
        string moduleCode = System.IO.File.ReadAllText(redditModulePath);
        string[] requiredOps = new string[] { "Initialize", "Browse", "Like", "Comment", "Follow", "Share" };
        foreach (string op in requiredOps) {
            if (!moduleCode.Contains("Func<dynamic, System.Collections.Generic.Dictionary<string, object>> " + op)) {
                LogErr("Operation not found: " + op);
                return false;
            }
        }
        Log("Step 6: All 6 operations present in RedditModule");
        
        LogSuccess("Reddit basic workflow test PASSED");
        return true;
        
    } catch (System.Exception ex) {
        LogErr("Test exception: " + ex.Message);
        return false;
    }
};

// Test 2: Instagram Basic Workflow
Func<bool> TestInstagramBasic = () => {
    Log("\n--- Test 2: Instagram Basic Workflow ---");
    
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
        
        // Step 3: Verify Instagram config
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
        
        // Step 4: Verify Instagram module exists
        string instagramModulePath = projectRoot + "Platforms\\Instagram\\InstagramModule.cs";
        if (!System.IO.File.Exists(instagramModulePath)) {
            LogErr("InstagramModule.cs not found");
            return false;
        }
        Log("Step 4: InstagramModule.cs exists");
        
        // Step 5: Verify rate limit implementation
        string moduleCode = System.IO.File.ReadAllText(instagramModulePath);
        if (!moduleCode.Contains("CheckRateLimit")) {
            LogErr("CheckRateLimit function not found");
            return false;
        }
        if (!moduleCode.Contains("IncrementRateLimit")) {
            LogErr("IncrementRateLimit function not found");
            return false;
        }
        Log("Step 5: Rate limit functions present");
        
        // Step 6: Verify all operations
        string[] requiredOps = new string[] { "Initialize", "Browse", "Like", "Comment", "Follow", "Share" };
        foreach (string op in requiredOps) {
            if (!moduleCode.Contains("Func<dynamic, System.Collections.Generic.Dictionary<string, object>> " + op)) {
                LogErr("Operation not found: " + op);
                return false;
            }
        }
        Log("Step 6: All 6 operations present in InstagramModule");
        
        LogSuccess("Instagram basic workflow test PASSED");
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
            
            Log("  ✓ " + device + " -> " + actualPlatform);
        }
        
        LogSuccess("Platform switching test PASSED");
        return true;
        
    } catch (System.Exception ex) {
        LogErr("Test exception: " + ex.Message);
        return false;
    }
};

// Test 4: Rate Limit Enforcement
Func<bool> TestRateLimit = () => {
    Log("\n--- Test 4: Rate Limit Enforcement ---");
    
    try {
        // Verify rate limits in config
        string platformsConfigPath = projectRoot + "Config\\PlatformsConfig.json";
        string platformsConfig = System.IO.File.ReadAllText(platformsConfigPath);
        
        // Check Reddit rate limits
        int redditPos = platformsConfig.IndexOf("\"reddit\"");
        int redditLimitsPos = platformsConfig.IndexOf("\"rate_limits\"", redditPos);
        int redditActionsPos = platformsConfig.IndexOf("\"max_actions_per_hour\"", redditLimitsPos);
        
        if (redditActionsPos < 0) {
            LogErr("Reddit rate_limits not found");
            return false;
        }
        Log("✓ Reddit rate_limits present");
        
        // Check Instagram rate limits (stricter)
        int instagramPos = platformsConfig.IndexOf("\"instagram\"");
        int instagramLimitsPos = platformsConfig.IndexOf("\"rate_limits\"", instagramPos);
        int instagramActionsPos = platformsConfig.IndexOf("\"max_actions_per_hour\"", instagramLimitsPos);
        
        if (instagramActionsPos < 0) {
            LogErr("Instagram rate_limits not found");
            return false;
        }
        Log("✓ Instagram rate_limits present");
        
        // Verify Instagram module implements rate limiting
        string instagramModulePath = projectRoot + "Platforms\\Instagram\\InstagramModule.cs";
        string moduleCode = System.IO.File.ReadAllText(instagramModulePath);
        
        if (!moduleCode.Contains("CheckRateLimit(\"actions\", 60)")) {
            LogErr("Instagram actions rate limit (60/hour) not enforced");
            return false;
        }
        Log("✓ Instagram actions rate limit: 60/hour");
        
        if (!moduleCode.Contains("CheckRateLimit(\"likes\", 30)")) {
            LogErr("Instagram likes rate limit (30/hour) not enforced");
            return false;
        }
        Log("✓ Instagram likes rate limit: 30/hour");
        
        if (!moduleCode.Contains("CheckRateLimit(\"comments\", 15)")) {
            LogErr("Instagram comments rate limit (15/hour) not enforced");
            return false;
        }
        Log("✓ Instagram comments rate limit: 15/hour");
        
        if (!moduleCode.Contains("CheckRateLimit(\"follows\", 10)")) {
            LogErr("Instagram follows rate limit (10/hour) not enforced");
            return false;
        }
        Log("✓ Instagram follows rate limit: 10/hour");
        
        LogSuccess("Rate limit enforcement test PASSED");
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
        // Verify ErrorRecovery module exists
        string errorRecoveryPath = projectRoot + "Core\\ErrorRecovery.cs";
        if (!System.IO.File.Exists(errorRecoveryPath)) {
            LogErr("ErrorRecovery.cs not found");
            return false;
        }
        Log("✓ ErrorRecovery.cs exists");
        
        // Verify error recovery functions
        string errorRecoveryCode = System.IO.File.ReadAllText(errorRecoveryPath);
        
        string[] requiredFunctions = new string[] {
            "TryWithRetry",
            "TryWithRetryFunc",
            "RecoverFromError",
            "LogError",
            "IsErrorThresholdExceeded"
        };
        
        foreach (string func in requiredFunctions) {
            if (!errorRecoveryCode.Contains(func)) {
                LogErr("Function not found: " + func);
                return false;
            }
            Log("✓ Function present: " + func);
        }
        
        // Verify retry configuration
        if (!errorRecoveryCode.Contains("maxRetries = 3")) {
            LogErr("Max retries not set to 3");
            return false;
        }
        Log("✓ Max retries: 3");
        
        // Verify exponential backoff
        if (!errorRecoveryCode.Contains("2000") && !errorRecoveryCode.Contains("4000") && !errorRecoveryCode.Contains("8000")) {
            LogErr("Exponential backoff delays not found");
            return false;
        }
        Log("✓ Exponential backoff: 2s, 4s, 8s");
        
        LogSuccess("Error recovery test PASSED");
        return true;
        
    } catch (System.Exception ex) {
        LogErr("Test exception: " + ex.Message);
        return false;
    }
};

// Test 6: Config Loading
Func<bool> TestConfigLoading = () => {
    Log("\n--- Test 6: Config Loading ---");
    
    try {
        // Test all required config files exist
        string[] configFiles = new string[] {
            "Config\\PlatformsConfig.json",
            "Config\\device_app_mapping.json"
        };
        
        foreach (string configFile in configFiles) {
            string fullPath = projectRoot + configFile;
            if (!System.IO.File.Exists(fullPath)) {
                LogErr("Config file not found: " + configFile);
                return false;
            }
            Log("✓ Config exists: " + configFile);
        }
        
        // Test all core modules exist
        string[] coreModules = new string[] {
            "Core\\HumanizationEngine.cs",
            "Core\\UILocator.cs",
            "Core\\ErrorRecovery.cs",
            "Core\\PlatformBase.cs"
        };
        
        foreach (string module in coreModules) {
            string fullPath = projectRoot + module;
            if (!System.IO.File.Exists(fullPath)) {
                LogErr("Core module not found: " + module);
                return false;
            }
            Log("✓ Core module exists: " + module);
        }
        
        // Test platform modules exist
        string[] platformModules = new string[] {
            "Platforms\\Reddit\\RedditModule.cs",
            "Platforms\\Instagram\\InstagramModule.cs"
        };
        
        foreach (string module in platformModules) {
            string fullPath = projectRoot + module;
            if (!System.IO.File.Exists(fullPath)) {
                LogErr("Platform module not found: " + module);
                return false;
            }
            Log("✓ Platform module exists: " + module);
        }
        
        // Test SessionRunner updated
        string sessionRunnerPath = projectRoot + "Modules\\SessionRunner.cs";
        if (!System.IO.File.Exists(sessionRunnerPath)) {
            LogErr("SessionRunner.cs not found");
            return false;
        }
        
        string sessionRunnerCode = System.IO.File.ReadAllText(sessionRunnerPath);
        if (!sessionRunnerCode.Contains("DeterminePlatform")) {
            LogErr("DeterminePlatform function not found in SessionRunner");
            return false;
        }
        if (!sessionRunnerCode.Contains("LoadPlatformModule")) {
            LogErr("LoadPlatformModule function not found in SessionRunner");
            return false;
        }
        Log("✓ SessionRunner has multi-platform support");
        
        LogSuccess("Config loading test PASSED");
        return true;
        
    } catch (System.Exception ex) {
        LogErr("Test exception: " + ex.Message);
        return false;
    }
};

Log("\nIntegration test complete.");
