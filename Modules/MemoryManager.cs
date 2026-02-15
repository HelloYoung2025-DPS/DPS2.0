// =====================================================
// MemoryManager.cs - 交互记忆管理器
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================

using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

public static class MemoryManager
{
    private static string _basePath = "";

    /// <summary>
    /// 初始化基础路径。
    /// </summary>
    /// <param name="basePath">基础目录路径。</param>
    public static void Init(string basePath)
    {
        if (string.IsNullOrEmpty(basePath))
        {
            _basePath = "";
            return;
        }

        _basePath = basePath;
    }

    /// <summary>
    /// 检查指定帖子是否已经有过交互记录。
    /// </summary>
    /// <param name="deviceId">设备ID。</param>
    /// <param name="appName">应用名称。</param>
    /// <param name="postId">帖子ID。</param>
    /// <returns>存在返回true，否则false。</returns>
    public static bool HasInteracted(string deviceId, string appName, string postId)
    {
        if (string.IsNullOrEmpty(postId))
        {
            return false;
        }

        string memoryFilePath = GetMemoryFilePath(deviceId, appName);
        if (string.IsNullOrEmpty(memoryFilePath))
        {
            return false;
        }

        if (!FileHelper.Exists(memoryFilePath))
        {
            return false;
        }

        string json = FileHelper.Read(memoryFilePath);
        if (string.IsNullOrEmpty(json) || !JsonHelper.IsValidJson(json))
        {
            return false;
        }

        string interactions = JsonHelper.GetArray(json, "interactions");
        List<string> list = ParseJsonArray(interactions);

        int i = 0;
        for (i = 0; i < list.Count; i++)
        {
            string item = list[i];
            string itemPostId = JsonHelper.Get(item, "post_id");
            if (itemPostId == postId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 记录一次交互行为，默认score为0.0。
    /// </summary>
    /// <param name="deviceId">设备ID。</param>
    /// <param name="appName">应用名称。</param>
    /// <param name="postId">帖子ID。</param>
    /// <param name="actionType">交互类型。</param>
    public static void RecordInteraction(string deviceId, string appName, string postId, string actionType)
    {
        RecordInteractionWithScore(deviceId, appName, postId, actionType, 0.0);
    }

    /// <summary>
    /// 记录一次带分数的交互行为。
    /// </summary>
    /// <param name="deviceId">设备ID。</param>
    /// <param name="appName">应用名称。</param>
    /// <param name="postId">帖子ID。</param>
    /// <param name="actionType">交互类型。</param>
    /// <param name="score">决策分数。</param>
    public static void RecordInteractionWithScore(string deviceId, string appName, string postId, string actionType, double score)
    {
        if (!IsValidActionType(actionType))
        {
            CoreHelper.LogWarn("MemoryManager", "Invalid actionType: " + actionType);
            return;
        }

        if (string.IsNullOrEmpty(postId))
        {
            CoreHelper.LogWarn("MemoryManager", "postId is empty.");
            return;
        }

        string memoryFilePath = GetMemoryFilePath(deviceId, appName);
        if (string.IsNullOrEmpty(memoryFilePath))
        {
            CoreHelper.LogWarn("MemoryManager", "Invalid path params.");
            return;
        }

        string dir = Path.GetDirectoryName(memoryFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            FileHelper.EnsureDir(dir);
        }

        string rootJson = LoadOrCreateRootJson(deviceId, appName, memoryFilePath);
        List<string> interactions = ParseJsonArray(JsonHelper.GetArray(rootJson, "interactions"));

        string nowIso = CoreHelper.GetNowISO();
        string entry = BuildInteractionEntry(postId, actionType, nowIso, score);
        interactions.Add(entry);

        string outJson = BuildRootJson(deviceId, appName, nowIso, interactions);
        FileHelper.WriteAtomic(memoryFilePath, outJson);
    }

    /// <summary>
    /// 获取最近交互历史（按时间新到旧）。
    /// </summary>
    /// <param name="deviceId">设备ID。</param>
    /// <param name="appName">应用名称。</param>
    /// <param name="limit">返回条数上限。</param>
    /// <returns>JSON数组字符串。</returns>
    public static string GetInteractionHistory(string deviceId, string appName, int limit)
    {
        if (limit <= 0)
        {
            return "[]";
        }

        string memoryFilePath = GetMemoryFilePath(deviceId, appName);
        if (string.IsNullOrEmpty(memoryFilePath) || !FileHelper.Exists(memoryFilePath))
        {
            return "[]";
        }

        string json = FileHelper.Read(memoryFilePath);
        if (string.IsNullOrEmpty(json) || !JsonHelper.IsValidJson(json))
        {
            return "[]";
        }

        List<string> interactions = ParseJsonArray(JsonHelper.GetArray(json, "interactions"));
        SortByTimestampDesc(interactions);

        if (interactions.Count > limit)
        {
            interactions = interactions.GetRange(0, limit);
        }

        return BuildArrayJson(interactions);
    }

    /// <summary>
    /// 获取指定动作类型的交互次数。
    /// </summary>
    /// <param name="deviceId">设备ID。</param>
    /// <param name="appName">应用名称。</param>
    /// <param name="actionType">动作类型。</param>
    /// <returns>交互次数。</returns>
    public static int GetInteractionCount(string deviceId, string appName, string actionType)
    {
        if (string.IsNullOrEmpty(actionType))
        {
            return 0;
        }

        List<string> interactions = LoadInteractions(deviceId, appName);
        int count = 0;
        int i = 0;
        for (i = 0; i < interactions.Count; i++)
        {
            string action = JsonHelper.Get(interactions[i], "action");
            if (action == actionType)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 获取今日指定动作类型的交互次数。
    /// </summary>
    /// <param name="deviceId">设备ID。</param>
    /// <param name="appName">应用名称。</param>
    /// <param name="actionType">动作类型。</param>
    /// <returns>今日交互次数。</returns>
    public static int GetTodayInteractionCount(string deviceId, string appName, string actionType)
    {
        if (string.IsNullOrEmpty(actionType))
        {
            return 0;
        }

        string today = CoreHelper.GetToday();
        List<string> interactions = LoadInteractions(deviceId, appName);
        int count = 0;
        int i = 0;
        for (i = 0; i < interactions.Count; i++)
        {
            string item = interactions[i];
            string action = JsonHelper.Get(item, "action");
            if (action != actionType)
            {
                continue;
            }

            string ts = JsonHelper.Get(item, "timestamp");
            if (!string.IsNullOrEmpty(ts) && ts.Length >= 10)
            {
                string datePart = ts.Substring(0, 10);
                if (datePart == today)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// 清理超过最大保留天数的旧交互。
    /// </summary>
    /// <param name="deviceId">设备ID。</param>
    /// <param name="appName">应用名称。</param>
    /// <param name="maxAgeDays">最大保留天数（若<=0则从配置读取，默认30）。</param>
    public static void CleanupOldInteractions(string deviceId, string appName, int maxAgeDays)
    {
        string memoryFilePath = GetMemoryFilePath(deviceId, appName);
        if (string.IsNullOrEmpty(memoryFilePath) || !FileHelper.Exists(memoryFilePath))
        {
            return;
        }

        string json = FileHelper.Read(memoryFilePath);
        if (string.IsNullOrEmpty(json) || !JsonHelper.IsValidJson(json))
        {
            return;
        }

        int effectiveDays = maxAgeDays;
        if (effectiveDays <= 0)
        {
            string cfg = LoadDecisionConfig();
            string mem = JsonHelper.GetNested(cfg, "memory");
            effectiveDays = JsonHelper.GetInt(mem, "cleanup_after_days", 30);
        }
        if (effectiveDays <= 0)
        {
            effectiveDays = 30;
        }

        DateTime now = DateTime.Now;
        List<string> source = ParseJsonArray(JsonHelper.GetArray(json, "interactions"));
        List<string> kept = new List<string>();

        int i = 0;
        for (i = 0; i < source.Count; i++)
        {
            string item = source[i];
            string ts = JsonHelper.Get(item, "timestamp");
            DateTime dt;
            if (!TryParseDateTime(ts, out dt))
            {
                kept.Add(item);
                continue;
            }

            TimeSpan age = now - dt;
            if (age.TotalDays <= (double)effectiveDays)
            {
                kept.Add(item);
            }
        }

        string outJson = BuildRootJson(deviceId, appName, CoreHelper.GetNowISO(), kept);
        FileHelper.WriteAtomic(memoryFilePath, outJson);
    }

    /// <summary>
    /// 按配置强制内存上限，超限时按类型删除最旧记录。
    /// </summary>
    /// <param name="deviceId">设备ID。</param>
    /// <param name="appName">应用名称。</param>
    /// <param name="configJson">DecisionConfig.json内容。</param>
    /// <returns>删除的记录条数。</returns>
    public static int EnforceMemoryLimits(string deviceId, string appName, string configJson)
    {
        string memoryFilePath = GetMemoryFilePath(deviceId, appName);
        if (string.IsNullOrEmpty(memoryFilePath) || !FileHelper.Exists(memoryFilePath))
        {
            return 0;
        }

        string json = FileHelper.Read(memoryFilePath);
        if (string.IsNullOrEmpty(json) || !JsonHelper.IsValidJson(json))
        {
            return 0;
        }

        string memoryCfg = JsonHelper.GetNested(configJson, "memory");
        int maxBrowsed = JsonHelper.GetInt(memoryCfg, "max_browsed_posts", 500);
        int maxLiked = JsonHelper.GetInt(memoryCfg, "max_liked_posts", 200);
        int maxCommented = JsonHelper.GetInt(memoryCfg, "max_commented_posts", 100);

        List<string> interactions = ParseJsonArray(JsonHelper.GetArray(json, "interactions"));
        int removed = 0;

        // BUG-01 fix: SessionRunner records "browse"/"like"/"comment",
        // 需要同时匹配两种命名风格（兼容历史数据）
        removed += TrimByActionLimit(interactions, "browse", maxBrowsed);
        removed += TrimByActionLimit(interactions, "browsed", maxBrowsed);
        removed += TrimByActionLimit(interactions, "like", maxLiked);
        removed += TrimByActionLimit(interactions, "liked", maxLiked);
        removed += TrimByActionLimit(interactions, "comment", maxCommented);
        removed += TrimByActionLimit(interactions, "commented", maxCommented);

        if (removed > 0)
        {
            string outJson = BuildRootJson(deviceId, appName, CoreHelper.GetNowISO(), interactions);
            FileHelper.WriteAtomic(memoryFilePath, outJson);
        }

        return removed;
    }

    /// <summary>
    /// 检查帖子在指定窗口小时内是否重复交互。
    /// </summary>
    /// <param name="deviceId">设备ID。</param>
    /// <param name="appName">应用名称。</param>
    /// <param name="postId">帖子ID。</param>
    /// <param name="windowHours">窗口小时（若<=0则从配置读取，默认24）。</param>
    /// <returns>重复返回true，否则false。</returns>
    public static bool IsDuplicate(string deviceId, string appName, string postId, int windowHours)
    {
        if (string.IsNullOrEmpty(postId))
        {
            return false;
        }

        int effectiveHours = windowHours;
        if (effectiveHours <= 0)
        {
            string cfg = LoadDecisionConfig();
            string mem = JsonHelper.GetNested(cfg, "memory");
            effectiveHours = JsonHelper.GetInt(mem, "dedup_window_hours", 24);
        }
        if (effectiveHours <= 0)
        {
            effectiveHours = 24;
        }

        List<string> interactions = LoadInteractions(deviceId, appName);
        if (interactions.Count == 0)
        {
            return false;
        }

        DateTime now = DateTime.Now;
        int i = 0;
        for (i = 0; i < interactions.Count; i++)
        {
            string item = interactions[i];
            string pid = JsonHelper.Get(item, "post_id");
            if (pid != postId)
            {
                continue;
            }

            string ts = JsonHelper.Get(item, "timestamp");
            DateTime dt;
            if (!TryParseDateTime(ts, out dt))
            {
                continue;
            }

            TimeSpan diff = now - dt;
            if (diff.TotalHours <= (double)effectiveHours && diff.TotalHours >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetMemoryFilePath(string deviceId, string appName)
    {
        if (!CoreHelper.ValidateDeviceId(deviceId))
        {
            CoreHelper.LogWarn("MemoryManager", "Invalid deviceId");
            return null;
        }

        if (!CoreHelper.ValidateDeviceId(appName))
        {
            CoreHelper.LogWarn("MemoryManager", "Invalid appName");
            return null;
        }

        string root = _basePath;
        if (string.IsNullOrEmpty(root))
        {
            root = ".";
        }

        string p = FileHelper.Combine(root, deviceId);
        p = FileHelper.Combine(p, appName);
        p = FileHelper.Combine(p, "interactions.json");
        return p;
    }

    private static string LoadOrCreateRootJson(string deviceId, string appName, string memoryFilePath)
    {
        if (FileHelper.Exists(memoryFilePath))
        {
            string oldJson = FileHelper.Read(memoryFilePath);
            if (!string.IsNullOrEmpty(oldJson) && JsonHelper.IsValidJson(oldJson))
            {
                return oldJson;
            }
        }

        List<string> empty = new List<string>();
        return BuildRootJson(deviceId, appName, CoreHelper.GetNowISO(), empty);
    }

    private static List<string> LoadInteractions(string deviceId, string appName)
    {
        List<string> result = new List<string>();

        string path = GetMemoryFilePath(deviceId, appName);
        if (string.IsNullOrEmpty(path) || !FileHelper.Exists(path))
        {
            return result;
        }

        string json = FileHelper.Read(path);
        if (string.IsNullOrEmpty(json) || !JsonHelper.IsValidJson(json))
        {
            return result;
        }

        return ParseJsonArray(JsonHelper.GetArray(json, "interactions"));
    }

    private static string BuildRootJson(string deviceId, string appName, string lastUpdatedIso, List<string> interactions)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{");
        sb.Append("\"version\":\"1.0\",");
        sb.Append("\"device_id\":\"").Append(JsonHelper.Escape(deviceId)).Append("\",");
        sb.Append("\"app_name\":\"").Append(JsonHelper.Escape(appName)).Append("\",");
        sb.Append("\"last_updated\":\"").Append(JsonHelper.Escape(lastUpdatedIso)).Append("\",");
        sb.Append("\"interactions\":").Append(BuildArrayJson(interactions));
        sb.Append("}");
        return sb.ToString();
    }

    private static string BuildInteractionEntry(string postId, string actionType, string timestamp, double score)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{");
        sb.Append("\"post_id\":\"").Append(JsonHelper.Escape(postId)).Append("\",");
        sb.Append("\"action\":\"").Append(JsonHelper.Escape(actionType)).Append("\",");
        sb.Append("\"timestamp\":\"").Append(JsonHelper.Escape(timestamp)).Append("\",");
        sb.Append("\"score\":").Append(score.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append("}");
        return sb.ToString();
    }

    private static string BuildArrayJson(List<string> items)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("[");
        int i = 0;
        for (i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(",");
            }
            sb.Append(items[i]);
        }
        sb.Append("]");
        return sb.ToString();
    }

    private static List<string> ParseJsonArray(string arrayJson)
    {
        List<string> list = new List<string>();
        if (string.IsNullOrEmpty(arrayJson))
        {
            return list;
        }

        string s = arrayJson.Trim();
        if (s.Length < 2 || s[0] != '[' || s[s.Length - 1] != ']')
        {
            return list;
        }

        int i = 1;
        int end = s.Length - 1;
        while (i < end)
        {
            while (i < end && (s[i] == ' ' || s[i] == '\r' || s[i] == '\n' || s[i] == '\t' || s[i] == ','))
            {
                i++;
            }
            if (i >= end)
            {
                break;
            }

            if (s[i] != '{')
            {
                i++;
                continue;
            }

            int start = i;
            int depth = 0;
            bool inString = false;
            bool escape = false;

            while (i < end)
            {
                char c = s[i];
                if (inString)
                {
                    if (escape)
                    {
                        escape = false;
                    }
                    else if (c == '\\')
                    {
                        escape = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inString = true;
                    }
                    else if (c == '{')
                    {
                        depth++;
                    }
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            i++;
                            break;
                        }
                    }
                }
                i++;
            }

            int len = i - start;
            if (len > 0)
            {
                string obj = s.Substring(start, len);
                if (JsonHelper.IsValidJson(obj))
                {
                    list.Add(obj);
                }
            }
        }

        return list;
    }

    private static bool IsValidActionType(string actionType)
    {
        return actionType == "browsed" ||
               actionType == "liked" ||
               actionType == "commented" ||
               actionType == "read" ||
               actionType == "skipped" ||
               actionType == "browse" ||
               actionType == "like" ||
               actionType == "comment" ||
               actionType == "read_post" ||
               actionType == "follow" ||
               actionType == "share" ||
               actionType == "post";
    }

    private static bool TryParseDateTime(string isoOrNormal, out DateTime dt)
    {
        dt = DateTime.MinValue;
        if (string.IsNullOrEmpty(isoOrNormal))
        {
            return false;
        }

        string s = isoOrNormal.Replace("T", " ");
        return DateTime.TryParse(s, out dt);
    }

    private static void SortByTimestampDesc(List<string> interactions)
    {
        interactions.Sort(delegate(string a, string b)
        {
            string tsa = JsonHelper.Get(a, "timestamp");
            string tsb = JsonHelper.Get(b, "timestamp");

            DateTime da;
            DateTime db;
            bool oka = TryParseDateTime(tsa, out da);
            bool okb = TryParseDateTime(tsb, out db);

            if (!oka && !okb)
            {
                return 0;
            }
            if (!oka)
            {
                return 1;
            }
            if (!okb)
            {
                return -1;
            }

            return db.CompareTo(da);
        });
    }

    private static int TrimByActionLimit(List<string> interactions, string actionType, int limit)
    {
        if (limit < 0)
        {
            limit = 0;
        }

        List<int> indexList = new List<int>();
        int i = 0;
        for (i = 0; i < interactions.Count; i++)
        {
            string action = JsonHelper.Get(interactions[i], "action");
            if (action == actionType)
            {
                indexList.Add(i);
            }
        }

        int count = indexList.Count;
        if (count <= limit)
        {
            return 0;
        }

        List<KeyValuePair<int, DateTime>> sortable = new List<KeyValuePair<int, DateTime>>();
        for (i = 0; i < indexList.Count; i++)
        {
            int idx = indexList[i];
            string ts = JsonHelper.Get(interactions[idx], "timestamp");
            DateTime dt;
            if (!TryParseDateTime(ts, out dt))
            {
                dt = DateTime.MinValue;
            }
            sortable.Add(new KeyValuePair<int, DateTime>(idx, dt));
        }

        sortable.Sort(delegate(KeyValuePair<int, DateTime> x, KeyValuePair<int, DateTime> y)
        {
            return x.Value.CompareTo(y.Value);
        });

        int removeCount = count - limit;
        HashSet<int> removeSet = new HashSet<int>();
        for (i = 0; i < removeCount && i < sortable.Count; i++)
        {
            removeSet.Add(sortable[i].Key);
        }

        List<string> kept = new List<string>();
        for (i = 0; i < interactions.Count; i++)
        {
            if (!removeSet.Contains(i))
            {
                kept.Add(interactions[i]);
            }
        }

        interactions.Clear();
        interactions.AddRange(kept);
        return removeCount;
    }

    private static string LoadDecisionConfig()
    {
        // _basePath 是 projectRoot + "Memory"，需要回到上级 Config 目录
        string root = _basePath;
        if (string.IsNullOrEmpty(root))
        {
            root = ".";
        }

        // 从 Memory/ 回到项目根 → Config/DecisionConfig.json
        string parentDir = Path.GetDirectoryName(root.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(parentDir))
        {
            parentDir = root;
        }
        string cfgPath = FileHelper.Combine(parentDir, "Config");
        cfgPath = FileHelper.Combine(cfgPath, "DecisionConfig.json");

        if (!FileHelper.Exists(cfgPath))
        {
            return "{}";
        }

        string cfg = FileHelper.Read(cfgPath);
        if (string.IsNullOrEmpty(cfg) || !JsonHelper.IsValidJson(cfg))
        {
            return "{}";
        }

        return cfg;
    }
}
