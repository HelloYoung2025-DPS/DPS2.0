// =====================================================
// Comment_Deep_Explore.cs - 深度探索评论区结构
// 滚动后查看评论节点
// =====================================================

Action<string> Log = (m) => project.SendInfoToLog("[DeepExplore] " + m, true);

Func<string, string, string> GetVar = (name, def) => {
    try {
        string v = project.Variables[name].Value;
        return string.IsNullOrEmpty(v) ? def : v;
    } catch { return def; }
};

var droid = instance.DroidInstance;
var input = droid.Input;
var app = droid.App;
var hierarchy = droid.Hierarchy;

int screenWidth = int.Parse(GetVar("screen_width", "1080"));
int screenHeight = int.Parse(GetVar("screen_height", "2400"));

// 打开 Reddit
Log("Opening Reddit...");
app.Open("com.reddit.frontpage");
System.Threading.Thread.Sleep(3000);

// 获取首页 XML，找评论按钮
string feedLayout = hierarchy.GetLayout();
int commentBtnPos = feedLayout.IndexOf("post_comment_button");
if (commentBtnPos < 0) {
    Log("ERROR: post_comment_button not found");
    return "ERROR";
}

int nodeStart = feedLayout.LastIndexOf('<', commentBtnPos);
int nodeEnd = feedLayout.IndexOf('>', commentBtnPos);
string nodeStr = feedLayout.Substring(nodeStart, nodeEnd - nodeStart + 1);

int boundsStart = nodeStr.IndexOf("bounds=\"") + 8;
int boundsEnd = nodeStr.IndexOf("\"", boundsStart);
string boundsStr = nodeStr.Substring(boundsStart, boundsEnd - boundsStart);

boundsStr = boundsStr.Replace("[", "").Replace("]", ",");
string[] parts = boundsStr.Split(new char[] {','}, System.StringSplitOptions.RemoveEmptyEntries);
int x = (int.Parse(parts[0]) + int.Parse(parts[2])) / 2;
int y = (int.Parse(parts[1]) + int.Parse(parts[3])) / 2;

Log("Clicking comment button at: (" + x.ToString() + ", " + y.ToString() + ")");
input.Tap(x, y);
Log("Waiting 5 seconds for page to load...");
System.Threading.Thread.Sleep(5000);

// 滚动 3 次，每次都检查 resource-id
for (int scroll = 0; scroll <= 3; scroll++) {
    Log("=== After scroll " + scroll.ToString() + " ===");
    
    string layout = hierarchy.GetLayout();
    
    // 查找所有 resource-id
    var resourceIds = new System.Collections.Generic.HashSet<string>();
    int pos = 0;
    while (pos < layout.Length) {
        int foundPos = layout.IndexOf("resource-id=\"", pos);
        if (foundPos < 0) break;
        
        int idStart = foundPos + 13;
        int idEnd = layout.IndexOf("\"", idStart);
        if (idEnd > idStart) {
            string id = layout.Substring(idStart, idEnd - idStart);
            resourceIds.Add(id);
        }
        pos = idEnd + 1;
    }
    
    // 输出包含 comment 的 resource-id
    Log("Resource-ids containing 'comment':");
    foreach (var id in resourceIds) {
        if (id.ToLower().Contains("comment")) {
            Log("  " + id);
        }
    }
    
    // 输出所有有 text 的节点（前 15 个）
    Log("Text nodes (first 15):");
    pos = 0;
    int textCount = 0;
    while (pos < layout.Length && textCount < 15) {
        int foundPos = layout.IndexOf("text=\"", pos);
        if (foundPos < 0) break;
        
        int textStart = foundPos + 6;
        int textEnd = layout.IndexOf("\"", textStart);
        if (textEnd > textStart) {
            string text = layout.Substring(textStart, textEnd - textStart);
            if (!string.IsNullOrEmpty(text) && text.Length > 3) {
                // 找 resource-id
                int ns = layout.LastIndexOf('<', foundPos);
                int ne = layout.IndexOf('>', foundPos);
                string node = layout.Substring(ns, ne - ns + 1);
                
                int ridPos = node.IndexOf("resource-id=\"");
                string rid = "(no id)";
                if (ridPos >= 0) {
                    int ridStart = ridPos + 13;
                    int ridEnd = node.IndexOf("\"", ridStart);
                    rid = node.Substring(ridStart, ridEnd - ridStart);
                }
                
                string textPreview = text.Length > 60 ? text.Substring(0, 60) + "..." : text;
                Log("  [" + rid + "] " + textPreview);
                textCount++;
            }
        }
        pos = textEnd + 1;
    }
    
    // 滚动
    if (scroll < 3) {
        Log("Scrolling down... (waiting 3 seconds)");
        int startX = screenWidth / 2;
        int startY = screenHeight * 2 / 3;
        int endY = screenHeight / 3;
        input.Swipe(startX, startY, startX, endY, 500);
        System.Threading.Thread.Sleep(3000);
    }
}

// 返回
Log("Returning to feed... (waiting 3 seconds)");
input.Shell("input keyevent 4");
System.Threading.Thread.Sleep(3000);

Log("=== Deep Exploration Complete ===");
return "SUCCESS";
