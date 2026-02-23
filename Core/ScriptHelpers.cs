// =====================================================
// ScriptHelpers.cs - ZD 脚本公共 Helper 模板
// ⚠️ C# 5.0 语法（兼容 ZennoDroid）
// ⚠️ 此文件由 ModuleLoader 自动加载，ZD 脚本无需手动引入
// ⚠️ ZD 脚本在调用前设置 _logTag 变量即可自定义日志前缀
// =====================================================

// ========== 公共 Helper Functions ==========
// 以下函数在所有 ZD 脚本中可用（通过 ModuleLoader 编译注入）

// 日志标签（ZD 脚本在使用 Log/LogErr 前设置此变量即可自定义前缀）
string _logTag = "Script";

// 日志
Action<string> Log = (m) => project.SendInfoToLog("[" + _logTag + "] " + m, true);
Action<string> LogErr = (m) => project.SendErrorToLog("[" + _logTag + "] " + m, true);

// 变量读写
Func<string, string, string> GetVar = (name, def) => {
    try {
        string v = project.Variables[name].Value;
        return string.IsNullOrEmpty(v) ? def : v;
    } catch { return def; }
};

Action<string, string> SetVar = (name, val) => {
    try {
        project.Variables[name].Value = val ?? "";
    } catch (System.Exception ex) {
        try { project.SendWarningToLog("[ScriptHelpers] SetVar 失败 (" + name + "): " + ex.Message); } catch { }
    }
};

// ========== Humanization Helpers ==========

// 获取行为画像配置参数
Func<string, System.Collections.Generic.Dictionary<string, double>> GetProfileConfig = (profileName) => {
    var config = new System.Collections.Generic.Dictionary<string, double>();
    
    if (profileName == "speed_demon") {
        config.Add("base_delay_mult", 0.6);
        config.Add("delay_variance", 0.15);
        config.Add("tap_offset_max", 5.0);
        config.Add("swipe_bending_range", 10.0);
        config.Add("prob_accidental_back", 0.01);
        config.Add("prob_scroll_back", 0.01);
        config.Add("prob_longer_pause", 0.02);
        config.Add("prob_double_tap", 0.005);
    } else if (profileName == "deep_reader") {
        config.Add("base_delay_mult", 1.5);
        config.Add("delay_variance", 0.30);
        config.Add("tap_offset_max", 10.0);
        config.Add("swipe_bending_range", 20.0);
        config.Add("prob_accidental_back", 0.02);
        config.Add("prob_scroll_back", 0.03);
        config.Add("prob_longer_pause", 0.08);
        config.Add("prob_double_tap", 0.01);
    } else if (profileName == "distracted") {
        config.Add("base_delay_mult", 1.2);
        config.Add("delay_variance", 0.40);
        config.Add("tap_offset_max", 20.0);
        config.Add("swipe_bending_range", 40.0);
        config.Add("prob_accidental_back", 0.05);
        config.Add("prob_scroll_back", 0.04);
        config.Add("prob_longer_pause", 0.10);
        config.Add("prob_double_tap", 0.02);
    } else { 
        // 默认 "casual" 画像
        config.Add("base_delay_mult", 1.0);
        config.Add("delay_variance", 0.25);
        config.Add("tap_offset_max", 15.0);
        config.Add("swipe_bending_range", 30.0);
        config.Add("prob_accidental_back", 0.03);
        config.Add("prob_scroll_back", 0.02);
        config.Add("prob_longer_pause", 0.05);
        config.Add("prob_double_tap", 0.01);
    }
    
    return config;
};

// 基于画像方差的随机延迟
Func<int, System.Collections.Generic.Dictionary<string, double>, System.Random, int> HumanizedDelay = (baseDelayMs, profile, rnd) => {
    double mult = profile["base_delay_mult"];
    double variance = profile["delay_variance"];
    
    double targetBase = baseDelayMs * mult;
    double min = targetBase * (1.0 - variance);
    double max = targetBase * (1.0 + variance);
    
    return (int)System.Math.Round(min + (rnd.NextDouble() * (max - min)));
};

// 带随机偏移的点击
Action<int[], System.Collections.Generic.Dictionary<string, double>, System.Random> HumanizedTap = (bounds, profile, rnd) => {
    int centerX = (bounds[0] + bounds[2]) / 2;
    int centerY = (bounds[1] + bounds[3]) / 2;
    double offsetMax = profile["tap_offset_max"];
    
    int offsetX = (int)((rnd.NextDouble() * 2.0 - 1.0) * offsetMax);
    int offsetY = (int)((rnd.NextDouble() * 2.0 - 1.0) * offsetMax);
    
    int finalX = centerX + offsetX;
    int finalY = centerY + offsetY;
    
    if (finalX < bounds[0]) finalX = bounds[0];
    if (finalX > bounds[2]) finalX = bounds[2];
    if (finalY < bounds[1]) finalY = bounds[1];
    if (finalY > bounds[3]) finalY = bounds[3];
    
    instance.DroidInstance.Input.Tap(finalX, finalY);
};

// 带随机弯曲的滑动
Action<int, int, int, int, System.Collections.Generic.Dictionary<string, double>, System.Random> HumanizedSwipe = (x1, y1, x2, y2, profile, rnd) => {
    double bendingRange = profile["swipe_bending_range"];
    double bending = (rnd.NextDouble() * bendingRange) / 100.0;
    
    instance.DroidInstance.Input.SwipeCurved(x1, y1, bending, x2, y2);
};

// 概率触发判断
Func<string, System.Collections.Generic.Dictionary<string, double>, System.Random, bool> ShouldTriggerProbabilistic = (actionType, profile, rnd) => {
    string key = "prob_" + actionType;
    if (profile.ContainsKey(key)) {
        return rnd.NextDouble() < profile[key];
    }
    return false;
};

// ========== 纯字符串 XML 解析 ==========

// 解析 bounds 属性: "[x1,y1][x2,y2]" -> int[4]
Func<string, int[]> ParseBounds = (boundsStr) => {
    boundsStr = boundsStr.Replace("[", "").Replace("]", ",");
    string[] parts = boundsStr.Split(new char[] {','}, System.StringSplitOptions.RemoveEmptyEntries);
    return new int[] { int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]) };
};

// 获取 bounds 中心点
Func<int[], System.Tuple<int, int>> GetCenter = (bounds) => {
    return new System.Tuple<int, int>((bounds[0] + bounds[2]) / 2, (bounds[1] + bounds[3]) / 2);
};

// 查找所有包含指定 resource-id 的节点的 bounds
Func<string, string, System.Collections.Generic.List<string>> FindBoundsByResourceId = (xml, resourceId) => {
    var results = new System.Collections.Generic.List<string>();
    string searchPattern = "resource-id=\"" + resourceId + "\"";
    
    int pos = 0;
    while (pos < xml.Length) {
        int foundPos = xml.IndexOf(searchPattern, pos);
        if (foundPos < 0) break;
        
        int nodeStart = xml.LastIndexOf('<', foundPos);
        if (nodeStart < 0) { pos = foundPos + 1; continue; }
        
        int nodeEnd = xml.IndexOf('>', foundPos);
        if (nodeEnd < 0) { pos = foundPos + 1; continue; }
        
        string nodeStr = xml.Substring(nodeStart, nodeEnd - nodeStart + 1);
        
        int boundsStart = nodeStr.IndexOf("bounds=\"");
        if (boundsStart >= 0) {
            boundsStart += 8;
            int boundsEnd = nodeStr.IndexOf("\"", boundsStart);
            if (boundsEnd > boundsStart) {
                string bounds = nodeStr.Substring(boundsStart, boundsEnd - boundsStart);
                results.Add(bounds);
            }
        }
        
        pos = nodeEnd + 1;
    }
    
    return results;
};

// 提取所有 text 属性值
Func<string, System.Collections.Generic.List<string>> ExtractAllText = (xml) => {
    var results = new System.Collections.Generic.List<string>();
    string searchPattern = "text=\"";
    
    int pos = 0;
    while (pos < xml.Length) {
        int foundPos = xml.IndexOf(searchPattern, pos);
        if (foundPos < 0) break;
        
        int textStart = foundPos + 6;
        int textEnd = xml.IndexOf("\"", textStart);
        if (textEnd <= textStart) { pos = foundPos + 1; continue; }
        
        string text = xml.Substring(textStart, textEnd - textStart);
        if (!string.IsNullOrEmpty(text) && text.Length > 0) {
            results.Add(text);
        }
        
        pos = textEnd + 1;
    }
    
    return results;
};

// 查找所有包含指定 resource-id 的完整节点字符串
Func<string, string, System.Collections.Generic.List<string>> FindNodesByResourceId = (xml, resourceId) => {
    var results = new System.Collections.Generic.List<string>();
    string searchPattern = "resource-id=\"" + resourceId + "\"";
    
    int pos = 0;
    while (pos < xml.Length) {
        int foundPos = xml.IndexOf(searchPattern, pos);
        if (foundPos < 0) break;
        
        int nodeStart = xml.LastIndexOf('<', foundPos);
        if (nodeStart < 0) { pos = foundPos + 1; continue; }
        
        int nodeEnd = xml.IndexOf('>', foundPos);
        if (nodeEnd < 0) { pos = foundPos + 1; continue; }
        
        string nodeStr = xml.Substring(nodeStart, nodeEnd - nodeStart + 1);
        results.Add(nodeStr);
        
        pos = nodeEnd + 1;
    }
    
    return results;
};

// 从节点字符串中提取 bounds
Func<string, string> ExtractBounds = (nodeStr) => {
    int boundsStart = nodeStr.IndexOf("bounds=\"");
    if (boundsStart < 0) return "";
    boundsStart += 8;
    int boundsEnd = nodeStr.IndexOf("\"", boundsStart);
    if (boundsEnd <= boundsStart) return "";
    return nodeStr.Substring(boundsStart, boundsEnd - boundsStart);
};

// 查找 EditText 节点的 bounds
Func<string, System.Collections.Generic.List<string>> FindEditTextBounds = (xml) => {
    var results = new System.Collections.Generic.List<string>();
    string searchPattern = "class=\"android.widget.EditText\"";
    
    int pos = 0;
    while (pos < xml.Length) {
        int foundPos = xml.IndexOf(searchPattern, pos);
        if (foundPos < 0) break;
        
        int nodeStart = xml.LastIndexOf('<', foundPos);
        if (nodeStart < 0) { pos = foundPos + 1; continue; }
        
        int nodeEnd = xml.IndexOf('>', foundPos);
        if (nodeEnd < 0) { pos = foundPos + 1; continue; }
        
        string nodeStr = xml.Substring(nodeStart, nodeEnd - nodeStart + 1);
        
        int boundsStart = nodeStr.IndexOf("bounds=\"");
        if (boundsStart >= 0) {
            boundsStart += 8;
            int boundsEnd = nodeStr.IndexOf("\"", boundsStart);
            if (boundsEnd > boundsStart) {
                string bounds = nodeStr.Substring(boundsStart, boundsEnd - boundsStart);
                results.Add(bounds);
            }
        }
        
        pos = nodeEnd + 1;
    }
    
    return results;
};
