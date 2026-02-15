// =====================================================
// Comment_Structure_Explore.cs - 探索评论区 XML 结构
// 功能：进入评论区，输出 XML 片段，找出评论节点特征
// =====================================================

Action<string> Log = (m) => project.SendInfoToLog("[Explore] " + m, true);

var droid = instance.DroidInstance;
var input = droid.Input;
var app = droid.App;
var hierarchy = droid.Hierarchy;

// 打开 Reddit
Log("Opening Reddit...");
app.Open("com.reddit.frontpage");
System.Threading.Thread.Sleep(3000);

// 获取首页 XML
string feedLayout = hierarchy.GetLayout();

// 找评论按钮
int commentBtnPos = feedLayout.IndexOf("post_comment_button");
if (commentBtnPos < 0) {
    Log("ERROR: post_comment_button not found");
    return "ERROR";
}

// 提取评论按钮的 bounds
int nodeStart = feedLayout.LastIndexOf('<', commentBtnPos);
int nodeEnd = feedLayout.IndexOf('>', commentBtnPos);
string nodeStr = feedLayout.Substring(nodeStart, nodeEnd - nodeStart + 1);

int boundsStart = nodeStr.IndexOf("bounds=\"") + 8;
int boundsEnd = nodeStr.IndexOf("\"", boundsStart);
string boundsStr = nodeStr.Substring(boundsStart, boundsEnd - boundsStart);

// 解析 bounds
boundsStr = boundsStr.Replace("[", "").Replace("]", ",");
string[] parts = boundsStr.Split(new char[] {','}, System.StringSplitOptions.RemoveEmptyEntries);
int x = (int.Parse(parts[0]) + int.Parse(parts[2])) / 2;
int y = (int.Parse(parts[1]) + int.Parse(parts[3])) / 2;

Log("Clicking comment button at: (" + x.ToString() + ", " + y.ToString() + ")");
input.Tap(x, y);
System.Threading.Thread.Sleep(3000);

// 获取评论区 XML
string commentLayout = hierarchy.GetLayout();

// 输出 XML 的前 5000 字符
Log("=== Comment Section XML (first 5000 chars) ===");
string preview = commentLayout.Length > 5000 ? commentLayout.Substring(0, 5000) : commentLayout;

// 分段输出（每段 500 字符）
for (int i = 0; i < preview.Length; i += 500) {
    int len = System.Math.Min(500, preview.Length - i);
    Log("XML[" + i.ToString() + "]: " + preview.Substring(i, len));
}

// 查找所有 resource-id
Log("=== All resource-id values ===");
var resourceIds = new System.Collections.Generic.HashSet<string>();
int pos = 0;
while (pos < commentLayout.Length) {
    int foundPos = commentLayout.IndexOf("resource-id=\"", pos);
    if (foundPos < 0) break;
    
    int idStart = foundPos + 13;
    int idEnd = commentLayout.IndexOf("\"", idStart);
    if (idEnd > idStart) {
        string id = commentLayout.Substring(idStart, idEnd - idStart);
        if (!resourceIds.Contains(id)) {
            resourceIds.Add(id);
            Log("  resource-id: " + id);
        }
    }
    pos = idEnd + 1;
}

// 查找所有 class 值
Log("=== All class values ===");
var classes = new System.Collections.Generic.HashSet<string>();
pos = 0;
while (pos < commentLayout.Length) {
    int foundPos = commentLayout.IndexOf("class=\"", pos);
    if (foundPos < 0) break;
    
    int classStart = foundPos + 7;
    int classEnd = commentLayout.IndexOf("\"", classStart);
    if (classEnd > classStart) {
        string cls = commentLayout.Substring(classStart, classEnd - classStart);
        if (!classes.Contains(cls)) {
            classes.Add(cls);
            Log("  class: " + cls);
        }
    }
    pos = classEnd + 1;
}

// 查找所有有 text 属性的节点
Log("=== Nodes with text attribute ===");
pos = 0;
int textCount = 0;
while (pos < commentLayout.Length && textCount < 20) {
    int foundPos = commentLayout.IndexOf("text=\"", pos);
    if (foundPos < 0) break;
    
    int textStart = foundPos + 6;
    int textEnd = commentLayout.IndexOf("\"", textStart);
    if (textEnd > textStart) {
        string text = commentLayout.Substring(textStart, textEnd - textStart);
        if (!string.IsNullOrEmpty(text) && text.Length > 3) {
            // 找到这个节点的 resource-id
            int nodeS = commentLayout.LastIndexOf('<', foundPos);
            int nodeE = commentLayout.IndexOf('>', foundPos);
            string node = commentLayout.Substring(nodeS, nodeE - nodeS + 1);
            
            int ridPos = node.IndexOf("resource-id=\"");
            string rid = "";
            if (ridPos >= 0) {
                int ridStart = ridPos + 13;
                int ridEnd = node.IndexOf("\"", ridStart);
                rid = node.Substring(ridStart, ridEnd - ridStart);
            }
            
            string textPreview = text.Length > 50 ? text.Substring(0, 50) + "..." : text;
            Log("  [" + rid + "] text: " + textPreview);
            textCount++;
        }
    }
    pos = textEnd + 1;
}

// 返回 feed
Log("Returning to feed...");
input.Shell("input keyevent 4");
System.Threading.Thread.Sleep(1500);

Log("=== Exploration Complete ===");
return "SUCCESS";
