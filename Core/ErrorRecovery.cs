// =====================================================
// ErrorRecovery.cs - Error Recovery Framework
// =====================================================
// Purpose: Provides automatic error recovery with retry logic
//          Handles common failures: app crashes, UI not found, network errors
//
// Usage:
//   1. Retry an action:
//      bool success = TryWithRetry(() => {
//          instance.DroidInstance.Input.Tap(x, y);
//      }, 3, 2000);
//
//   2. Recover from specific errors:
//      RecoverFromError("app_crash", () => {
//          instance.DroidInstance.App.Open("com.reddit.frontpage");
//      });
//
//   3. Log errors for tracking:
//      LogError("ui_not_found", "Could not find post_unit element");
//
// Error Types:
//   - app_crash: App stopped unexpectedly
//   - ui_not_found: UI element not found in hierarchy
//   - network_error: Network request failed
//   - timeout: Operation timed out
//
// Retry Strategy:
//   - Max 3 retries per action (from Metis review)
//   - Exponential backoff: 2s, 4s, 8s
//   - Skip action after max retries
//
// Dependencies:
//   - instance.DroidInstance (ZennoDroid API)
//   - project.Variables (for error tracking)
//   - System.Threading.Thread (for delays)
// =====================================================

// Log, LogErr, GetVar, SetVar defined in ScriptHelpers.cs

Func<System.Exception, bool> IsRecoverable = (ex) => {
    string typeName = ex.GetType().Name;
    // Fatal exceptions - never retry
    if (typeName == "OutOfMemoryException") return false;
    if (typeName == "StackOverflowException") return false;
    if (typeName == "ThreadAbortException") return false;
    // Recoverable exceptions
    if (typeName == "TimeoutException") return true;
    if (typeName == "IOException") return true;
    if (typeName == "WebException") return true;
    if (typeName == "InvalidOperationException") return true;
    // Default: recoverable
    return true;
};

// ========== Error Recovery Functions ==========

// Try executing action with retry logic
Func<System.Action, int, int, bool> TryWithRetry = (action, maxRetries, delayMs) => {
    for (int i = 0; i < maxRetries; i++) {
        try {
            action();
            if (i > 0) {
                Log("Action succeeded on attempt " + (i + 1).ToString());
            }
            return true;
        } catch (System.Exception ex) {
            if (!IsRecoverable(ex)) {
                LogErr("Fatal exception, not retrying: " + ex.GetType().Name);
                throw;
            }
            LogErr("Attempt " + (i + 1).ToString() + "/" + maxRetries.ToString() + " failed: " + ex.Message);
            if (i < maxRetries - 1) {
                int backoffDelay = delayMs * (int)System.Math.Pow(2, i);
                Log("Retrying in " + backoffDelay.ToString() + "ms...");
                System.Threading.Thread.Sleep(backoffDelay);
            }
        }
    }
    LogErr("Action failed after " + maxRetries.ToString() + " attempts");
    return false;
};

// Try executing function with retry logic and return result
Func<System.Func<string>, int, int, string, string> TryWithRetryFunc = (func, maxRetries, delayMs, defaultValue) => {
    for (int i = 0; i < maxRetries; i++) {
        try {
            string result = func();
            if (i > 0) {
                Log("Function succeeded on attempt " + (i + 1).ToString());
            }
            return result;
        } catch (System.Exception ex) {
            if (!IsRecoverable(ex)) {
                LogErr("Fatal exception, not retrying: " + ex.GetType().Name);
                throw;
            }
            LogErr("Attempt " + (i + 1).ToString() + "/" + maxRetries.ToString() + " failed: " + ex.Message);
            if (i < maxRetries - 1) {
                int backoffDelay = delayMs * (int)System.Math.Pow(2, i);
                Log("Retrying in " + backoffDelay.ToString() + "ms...");
                System.Threading.Thread.Sleep(backoffDelay);
            }
        }
    }
    LogErr("Function failed after " + maxRetries.ToString() + " attempts, returning default value");
    return defaultValue;
};

// Recover from specific error types
Func<string, System.Action, bool> RecoverFromError = (errorType, recoveryAction) => {
    Log("Recovering from error: " + errorType);
    bool success = false;
    
    if (errorType == "app_crash") {
        Log("App crash detected, waiting 3s before recovery...");
        System.Threading.Thread.Sleep(3000);
        success = TryWithRetry(recoveryAction, 3, 2000);
    } else if (errorType == "ui_not_found") {
        success = TryWithRetry(recoveryAction, 2, 1000);
    } else if (errorType == "network_error") {
        success = TryWithRetry(recoveryAction, 5, 3000);
    } else {
        Log("Unknown error type, attempting generic recovery");
        success = TryWithRetry(recoveryAction, 2, 1000);
    }
    
    if (!success) {
        LogErr("Recovery failed for error type: " + errorType);
    }
    return success;
};

// Log error to project variables for tracking
Action<string, string> LogError = (errorType, errorMessage) => {
    LogErr(errorType + ": " + errorMessage);
    
    // Track error count
    string countKey = "error_count_" + errorType;
    string currentCount = GetVar(countKey, "0");
    int count = 0;
    int.TryParse(currentCount, out count);
    count++;
    SetVar(countKey, count.ToString());
    
    // Store last error message
    SetVar("last_error_type", errorType);
    SetVar("last_error_message", errorMessage);
    SetVar("last_error_time", System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
};

// Check if error threshold exceeded
Func<string, int, bool> IsErrorThresholdExceeded = (errorType, threshold) => {
    string countKey = "error_count_" + errorType;
    string currentCount = GetVar(countKey, "0");
    int count = 0;
    int.TryParse(currentCount, out count);
    return count >= threshold;
};

// Reset error counters
Action ResetErrorCounters = () => {
    Log("Resetting all error counters");
    SetVar("error_count_app_crash", "0");
    SetVar("error_count_ui_not_found", "0");
    SetVar("error_count_network_error", "0");
    SetVar("error_count_timeout", "0");
    SetVar("last_error_type", "");
    SetVar("last_error_message", "");
    SetVar("last_error_time", "");
};

// ========== Initialization Pattern ==========
// Example usage in platform modules:
//
// bool success = TryWithRetry(() => {
//     var xml = instance.DroidInstance.Hierarchy.GetLayout();
//     var bounds = FindBoundsByResourceId(xml, "post_unit");
//     if (bounds.Count == 0) throw new System.Exception("No posts found");
// }, 3, 2000);
//
// if (!success) {
//     LogError("ui_not_found", "Could not find post_unit after 3 retries");
//     RecoverFromError("ui_not_found", () => {
//         instance.DroidInstance.Input.Shell("input keyevent 4");
//         System.Threading.Thread.Sleep(1000);
//     });
// }
