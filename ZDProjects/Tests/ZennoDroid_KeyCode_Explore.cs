// =====================================================
// KeyCode Type Exploration - ZennoDroid Own Code
// =====================================================

Action<string> Log = (m) => project.SendInfoToLog("[KeyCode] " + m, true);

try {
    var droid = instance.DroidInstance;
    var input = droid.Input;
    
    // 获取所有 SendKeyCode 方法重载
    var methods = input.GetType().GetMethods();
    int count = 0;
    foreach (var method in methods) {
        if (method.Name == "SendKeyCode") {
            count++;
            Log("=== Overload " + count.ToString() + " ===");
            var parameters = method.GetParameters();
            foreach (var param in parameters) {
                Log("  Parameter: " + param.Name);
                Log("  Type: " + param.ParameterType.FullName);
            }
        }
    }
    
    Log("Total SendKeyCode overloads: " + count.ToString());
    
    // 测试使用 Shell 命令（已知可用）
    Log("Testing Shell command for BACK key...");
    input.Shell("input keyevent 4");
    Log("Shell command executed successfully");
    
    return "SUCCESS";
}
catch (System.Exception ex) {
    Log("Exception: " + ex.Message);
    return "ERROR: " + ex.Message;
}
