// =====================================================
// UILocator.cs - Multi-Strategy UI Element Locator
// =====================================================
// Purpose: Provides flexible UI element location with fallback strategies
//          Uses pure string parsing (no System.Xml dependency)
//          Supports relative coordinates for multi-resolution compatibility
//
// Usage:
//   1. Get UI hierarchy XML:
//      string xml = instance.DroidInstance.Hierarchy.GetLayout();
//
//   2. Find clickable elements by resource-id:
//      var bounds = FindClickableBoundsByResourceId(xml, "post_unit");
//      foreach (string b in bounds) {
//          int[] coords = ParseBounds(b);
//          var center = GetCenter(coords);
//      }
//
//   3. Find full nodes:
//      var nodes = FindNodesByResourceId(xml, "comment_button");
//      foreach (string node in nodes) {
//          string boundsStr = ExtractBounds(node);
//          int[] coords = ParseBounds(boundsStr);
//      }
//
//   4. Use relative coordinates:
//      var relCoords = ConvertToRelative(540, 1200, 1080, 2400); // (50%, 50%)
//      var absCoords = ConvertToAbsolute(50.0, 50.0, 1080, 2400); // (540, 1200)
//
// Multi-Strategy Fallback Chain:
//   1. Resource-ID (fastest, most reliable) - Use for known elements
//   2. XPath (fallback for dynamic layouts) - Simple pattern matching
//   3. Image Recognition (last resort) - Stub for future implementation
//
// Why Pure String Parsing:
//   - ZennoDroid C# 5.0 doesn't support System.Xml reliably
//   - String parsing is faster for simple queries
//   - No external dependencies
//
// Dependencies:
//   - instance.DroidInstance.Hierarchy (ZennoDroid API)
//   - project.Variables (for GetVar/SetVar)
//   - System.Collections.Generic.List, System.Tuple
// =====================================================

// Log, LogErr, GetVar, SetVar, ParseBounds, GetCenter, FindNodesByResourceId, ExtractBounds defined in ScriptHelpers.cs

// ========== UILocator-Specific Functions ==========

// Check if element is clickable, enabled and has valid bounds
Func<string, bool> IsElementClickable = (nodeStr) => {
    // Check clickable attribute
    int clickablePos = nodeStr.IndexOf("clickable=\"");
    if (clickablePos >= 0) {
        int valueStart = clickablePos + 11;
        int valueEnd = nodeStr.IndexOf("\"", valueStart);
        string clickable = nodeStr.Substring(valueStart, valueEnd - valueStart);
        if (clickable == "false") return false;
    }
    
    // Check enabled attribute
    int enabledPos = nodeStr.IndexOf("enabled=\"");
    if (enabledPos >= 0) {
        int valueStart = enabledPos + 9;
        int valueEnd = nodeStr.IndexOf("\"", valueStart);
        string enabled = nodeStr.Substring(valueStart, valueEnd - valueStart);
        if (enabled == "false") return false;
    }
    
    // Check bounds validity
    int boundsPos = nodeStr.IndexOf("bounds=\"");
    if (boundsPos < 0) return false;
    
    int boundsStart = boundsPos + 8;
    int boundsEnd = nodeStr.IndexOf("\"", boundsStart);
    string boundsStr = nodeStr.Substring(boundsStart, boundsEnd - boundsStart);
    
    try {
        int[] coords = ParseBounds(boundsStr);
        // Check if has valid size
        if (coords[2] <= coords[0] || coords[3] <= coords[1]) return false;
    } catch {
        return false;
    }
    
    return true;
};

// Find all bounds strings for clickable nodes with specified resource-id
// NOTE: Unlike FindBoundsByResourceId in ScriptHelpers, this version filters by IsElementClickable
Func<string, string, System.Collections.Generic.List<string>> FindClickableBoundsByResourceId = (xml, resourceId) => {
    var results = new System.Collections.Generic.List<string>();
    string searchPattern = "resource-id=\"" + resourceId + "\"";
    
    int pos = 0;
    while (pos < xml.Length) {
        int foundPos = xml.IndexOf(searchPattern, pos);
        if (foundPos < 0) break;
        
        // 找到这个节点的开始位置（向前找 <）
        int nodeStart = xml.LastIndexOf('<', foundPos);
        if (nodeStart < 0) { pos = foundPos + 1; continue; }
        
        // 找到这个节点的结束位置（向后找 > 或 />）
        int nodeEnd = xml.IndexOf('>', foundPos);
        if (nodeEnd < 0) { pos = foundPos + 1; continue; }
        
        string nodeStr = xml.Substring(nodeStart, nodeEnd - nodeStart + 1);
        
        if (!IsElementClickable(nodeStr)) {
            pos = nodeEnd + 1;
            continue;
        }
        
        // 提取 bounds 属性
        int boundsStart = nodeStr.IndexOf("bounds=\"");
        if (boundsStart >= 0) {
            boundsStart += 8; // 跳过 bounds="
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

// Extract any attribute from node string
Func<string, string, string> ExtractAttribute = (nodeStr, attrName) => {
    string searchPattern = attrName + "=\"";
    int attrStart = nodeStr.IndexOf(searchPattern);
    if (attrStart < 0) return "";
    attrStart += searchPattern.Length;
    int attrEnd = nodeStr.IndexOf("\"", attrStart);
    if (attrEnd <= attrStart) return "";
    return nodeStr.Substring(attrStart, attrEnd - attrStart);
};

// ========== Relative Coordinate System ==========

// Convert absolute coordinates to relative percentages
Func<int, int, int, int, System.Tuple<double, double>> ConvertToRelative = (x, y, screenWidth, screenHeight) => {
    double percentX = ((double)x / (double)screenWidth) * 100.0;
    double percentY = ((double)y / (double)screenHeight) * 100.0;
    return new System.Tuple<double, double>(percentX, percentY);
};

// Convert relative percentages to absolute coordinates
Func<double, double, int, int, System.Tuple<int, int>> ConvertToAbsolute = (percentX, percentY, screenWidth, screenHeight) => {
    int x = (int)System.Math.Round((percentX / 100.0) * (double)screenWidth);
    int y = (int)System.Math.Round((percentY / 100.0) * (double)screenHeight);
    return new System.Tuple<int, int>(x, y);
};

// ========== Multi-Strategy Element Finder ==========

// Find element using fallback chain: resource-id -> XPath -> image
Func<string, string, string> FindElement = (xml, identifier) => {
    // Strategy 1: Try resource-id first (fastest, clickable-only)
    var bounds = FindClickableBoundsByResourceId(xml, identifier);
    if (bounds.Count > 0) {
        Log("Found element by resource-id: " + identifier);
        return bounds[0];
    }
    
    // Strategy 2: Try simple XPath pattern matching (fallback)
    // For now, just log that we would try XPath
    Log("Resource-id not found, XPath fallback not yet implemented");
    
    // Strategy 3: Image recognition (stub for future)
    Log("Image recognition fallback not yet implemented");
    
    return "";
};

// ========== Advanced XML Navigation ==========

// Find child element within parent scope (used for nested structures like post_footer)
Func<string, int, string, string, string> FindChildBoundsInParent = (xml, parentIndex, parentResourceId, childResourceId) => {
    // Find all parent nodes
    var parentNodes = FindNodesByResourceId(xml, parentResourceId);
    if (parentIndex >= parentNodes.Count) return "";
    
    // Find target parent position in XML
    string targetParent = parentNodes[parentIndex];
    int parentPos = xml.IndexOf(targetParent);
    if (parentPos < 0) return "";
    
    // Define search area (from parent to next parent or end)
    int searchStart = parentPos + targetParent.Length;
    int searchEnd = xml.Length;
    if (parentIndex + 1 < parentNodes.Count) {
        int nextParentPos = xml.IndexOf(parentNodes[parentIndex + 1], searchStart);
        if (nextParentPos > 0) searchEnd = nextParentPos;
    }
    
    string searchArea = xml.Substring(searchStart, searchEnd - searchStart);
    
    // Find child within this area
    var childNodes = FindNodesByResourceId(searchArea, childResourceId);
    if (childNodes.Count > 0) {
        return ExtractBounds(childNodes[0]);
    }
    
    return "";
};

// ========== Initialization Pattern ==========
// Example usage in platform modules:
//
// string xml = instance.DroidInstance.Hierarchy.GetLayout();
// var postBounds = FindClickableBoundsByResourceId(xml, "post_unit");
// foreach (string boundsStr in postBounds) {
//     int[] coords = ParseBounds(boundsStr);
//     var center = GetCenter(coords);
//     Log("Post at: " + center.Item1.ToString() + "," + center.Item2.ToString());
// }
