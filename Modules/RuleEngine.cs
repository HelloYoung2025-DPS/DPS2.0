// =====================================================
// RuleEngine.cs - 规则评分引擎
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================

using System;
using System.Text;
using System.Collections.Generic;

public static class RuleEngine
{
    private static bool _configLoaded = false;

    // Hot score config
    private static double _hotWeightUpvotes = 0.4;
    private static double _hotWeightComments = 0.3;
    private static double _hotWeightTimeDecay = 0.3;
    private static double _hotHalfLifeMinutes = 120.0;
    private static int _hotMinUpvotesThreshold = 5;

    // Activity score config
    private static double _activityWeightRecentRatio = 0.5;
    private static double _activityWeightFrequency = 0.3;
    private static double _activityWeightUniqueAuthors = 0.2;
    private static double _activityRecentWindowMinutes = 60.0;

    // Relevance score config
    private static double _relevanceWeightKeywordMatch = 0.5;
    private static double _relevanceWeightSubredditMatch = 0.3;
    private static double _relevanceWeightTopicSimilarity = 0.2;

    // Composite score config
    private static double _compositeWeightHot = 0.3;
    private static double _compositeWeightActivity = 0.2;
    private static double _compositeWeightRelevance = 0.5;

    // Decision thresholds
    private static Dictionary<string, double> _decisionThresholds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 加载并缓存规则引擎配置（DecisionConfig.json）。
    /// </summary>
    public static void LoadConfig(string configJson)
    {
        if (string.IsNullOrEmpty(configJson))
        {
            _configLoaded = false;
            return;
        }

        try
        {
            string ruleEngine = JsonHelper.ExtractObject(configJson, "rule_engine");
            if (!string.IsNullOrEmpty(ruleEngine))
            {
                string hotScore = JsonHelper.ExtractObject(ruleEngine, "hot_score");
                if (!string.IsNullOrEmpty(hotScore))
                {
                    string hotWeights = JsonHelper.ExtractObject(hotScore, "weights");
                    if (!string.IsNullOrEmpty(hotWeights))
                    {
                        _hotWeightUpvotes = JsonHelper.GetDouble(hotWeights, "upvotes", _hotWeightUpvotes);
                        _hotWeightComments = JsonHelper.GetDouble(hotWeights, "comments", _hotWeightComments);
                        _hotWeightTimeDecay = JsonHelper.GetDouble(hotWeights, "time_decay", _hotWeightTimeDecay);
                    }

                    _hotHalfLifeMinutes = JsonHelper.GetDouble(hotScore, "time_decay_half_life_minutes", _hotHalfLifeMinutes);
                    _hotMinUpvotesThreshold = JsonHelper.GetInt(hotScore, "min_upvotes_threshold", _hotMinUpvotesThreshold);
                }

                string activityScore = JsonHelper.ExtractObject(ruleEngine, "activity_score");
                if (!string.IsNullOrEmpty(activityScore))
                {
                    string activityWeights = JsonHelper.ExtractObject(activityScore, "weights");
                    if (!string.IsNullOrEmpty(activityWeights))
                    {
                        _activityWeightRecentRatio = JsonHelper.GetDouble(activityWeights, "recent_comments_ratio", _activityWeightRecentRatio);
                        _activityWeightFrequency = JsonHelper.GetDouble(activityWeights, "comment_frequency", _activityWeightFrequency);
                        _activityWeightUniqueAuthors = JsonHelper.GetDouble(activityWeights, "unique_authors", _activityWeightUniqueAuthors);
                    }

                    _activityRecentWindowMinutes = JsonHelper.GetDouble(activityScore, "recent_window_minutes", _activityRecentWindowMinutes);
                }

                string relevanceScore = JsonHelper.ExtractObject(ruleEngine, "relevance_score");
                if (!string.IsNullOrEmpty(relevanceScore))
                {
                    string relevanceWeights = JsonHelper.ExtractObject(relevanceScore, "weights");
                    if (!string.IsNullOrEmpty(relevanceWeights))
                    {
                        _relevanceWeightKeywordMatch = JsonHelper.GetDouble(relevanceWeights, "keyword_match", _relevanceWeightKeywordMatch);
                        _relevanceWeightSubredditMatch = JsonHelper.GetDouble(relevanceWeights, "subreddit_match", _relevanceWeightSubredditMatch);
                        _relevanceWeightTopicSimilarity = JsonHelper.GetDouble(relevanceWeights, "topic_similarity", _relevanceWeightTopicSimilarity);
                    }
                }

                // Optional composite score weights if present
                string compositeScore = JsonHelper.ExtractObject(ruleEngine, "composite_score");
                if (!string.IsNullOrEmpty(compositeScore))
                {
                    string compositeWeights = JsonHelper.ExtractObject(compositeScore, "weights");
                    if (!string.IsNullOrEmpty(compositeWeights))
                    {
                        _compositeWeightHot = JsonHelper.GetDouble(compositeWeights, "hot", _compositeWeightHot);
                        _compositeWeightActivity = JsonHelper.GetDouble(compositeWeights, "activity", _compositeWeightActivity);
                        _compositeWeightRelevance = JsonHelper.GetDouble(compositeWeights, "relevance", _compositeWeightRelevance);
                    }
                }
            }

            string decisionThresholds = JsonHelper.ExtractObject(configJson, "decision_thresholds");
            if (!string.IsNullOrEmpty(decisionThresholds))
            {
                _decisionThresholds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                string clickPost = JsonHelper.ExtractObject(decisionThresholds, "click_post");
                if (!string.IsNullOrEmpty(clickPost)) _decisionThresholds["click_post"] = JsonHelper.GetDouble(clickPost, "min_score", 0.3);

                string likePost = JsonHelper.ExtractObject(decisionThresholds, "like_post");
                if (!string.IsNullOrEmpty(likePost)) _decisionThresholds["like_post"] = JsonHelper.GetDouble(likePost, "min_score", 0.4);

                string commentPost = JsonHelper.ExtractObject(decisionThresholds, "comment_post");
                if (!string.IsNullOrEmpty(commentPost)) _decisionThresholds["comment_post"] = JsonHelper.GetDouble(commentPost, "min_score", 0.6);

                string replyComment = JsonHelper.ExtractObject(decisionThresholds, "reply_comment");
                if (!string.IsNullOrEmpty(replyComment)) _decisionThresholds["reply_comment"] = JsonHelper.GetDouble(replyComment, "min_score", 0.7);

                string continueBrowsing = JsonHelper.ExtractObject(decisionThresholds, "continue_browsing");
                if (!string.IsNullOrEmpty(continueBrowsing)) _decisionThresholds["continue_browsing"] = JsonHelper.GetDouble(continueBrowsing, "min_score", 0.2);
            }

            _configLoaded = true;
        }
        catch (Exception ex)
        {
            // BUG-07 fix: 记录异常而不是静默吞掉
            CoreHelper.LogErr("RuleEngine", "LoadConfig 解析异常: " + ex.Message);
            _configLoaded = false;
        }
    }

    /// <summary>
    /// 计算帖子热度分（0.0-1.0）。
    /// </summary>
    public static double CalculateHotScore(string postJson)
    {
        if (string.IsNullOrEmpty(postJson))
        {
            return 0.0;
        }

        double upvotes = CoreHelper.SafeParseDouble(CoreHelper.JGet(postJson, "upvotes"), 0.0);
        double comments = CoreHelper.SafeParseDouble(CoreHelper.JGet(postJson, "comment_count"), 0.0);
        string timestamp = CoreHelper.JGet(postJson, "timestamp");

        double upvotesNorm = 0.0;
        if (upvotes >= _hotMinUpvotesThreshold)
        {
            upvotesNorm = NormalizeScore(upvotes, 1000.0);
        }

        double commentsNorm = NormalizeScore(comments, 100.0);

        double minutesAge = ParseTimestampToMinutes(timestamp);
        double halfLife = _hotHalfLifeMinutes <= 0 ? 120.0 : _hotHalfLifeMinutes;
        double timeDecay = Math.Pow(0.5, minutesAge / halfLife);

        double hot = (upvotesNorm * _hotWeightUpvotes) +
                     (commentsNorm * _hotWeightComments) +
                     (timeDecay * _hotWeightTimeDecay);

        return Clamp01(hot);
    }

    /// <summary>
    /// 计算互动活跃度分（0.0-1.0）。
    /// </summary>
    public static double CalculateActivityScore(string postJson, string commentsJson)
    {
        string commentsArray = commentsJson;
        if (!string.IsNullOrEmpty(commentsJson))
        {
            string extracted = JsonHelper.ExtractArray(commentsJson, "comments");
            if (!string.IsNullOrEmpty(extracted))
            {
                commentsArray = extracted;
            }
        }

        int totalComments = GetArrayCount(commentsArray);
        if (totalComments <= 0)
        {
            return 0.0;
        }

        int recentCount = 0;
        Dictionary<string, bool> authorSet = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        int i = 0;
        while (i < totalComments)
        {
            string item = JsonHelper.GetArrayElement(commentsArray, i);
            if (!string.IsNullOrEmpty(item))
            {
                string ts = CoreHelper.JGet(item, "timestamp");
                double mins = ParseTimestampToMinutes(ts);
                if (mins < _activityRecentWindowMinutes)
                {
                    recentCount++;
                }

                string author = CoreHelper.JGet(item, "author");
                if (!string.IsNullOrEmpty(author) && !authorSet.ContainsKey(author))
                {
                    authorSet[author] = true;
                }
            }
            i++;
        }

        double recentRatio = totalComments > 0 ? (double)recentCount / (double)totalComments : 0.0;
        double frequency = NormalizeScore((double)totalComments, 50.0);
        double uniqueAuthors = NormalizeScore((double)authorSet.Count, 20.0);

        double activity = (recentRatio * _activityWeightRecentRatio) +
                          (frequency * _activityWeightFrequency) +
                          (uniqueAuthors * _activityWeightUniqueAuthors);

        return Clamp01(activity);
    }

    /// <summary>
    /// 计算内容相关性分（0.0-1.0）。
    /// </summary>
    public static double CalculateRelevanceScore(string postJson, string interestsJson, string triggersJson)
    {
        if (string.IsNullOrEmpty(postJson))
        {
            return 0.0;
        }

        string title = CoreHelper.JGet(postJson, "title");
        string body = CoreHelper.JGet(postJson, "body");
        string subreddit = CoreHelper.JGet(postJson, "subreddit");

        StringBuilder textBuilder = new StringBuilder();
        if (!string.IsNullOrEmpty(title)) textBuilder.Append(title).Append(" ");
        if (!string.IsNullOrEmpty(body)) textBuilder.Append(body);
        string fullText = textBuilder.ToString();

        List<string> interestKeywords = GetInterestKeywords(interestsJson);
        int keywordMatches = 0;
        int k = 0;
        while (k < interestKeywords.Count)
        {
            if (ContainsKeyword(fullText, interestKeywords[k]))
            {
                keywordMatches++;
            }
            k++;
        }

        double keywordMatch = NormalizeScore((double)keywordMatches, 3.0);

        List<string> interestSubs = GetInterestSubreddits(interestsJson);
        double subredditMatch = 0.0;
        int s = 0;
        while (s < interestSubs.Count)
        {
            if (!string.IsNullOrEmpty(subreddit) &&
                string.Equals(subreddit, interestSubs[s], StringComparison.OrdinalIgnoreCase))
            {
                subredditMatch = 1.0;
                break;
            }
            s++;
        }

        List<string> boosters = GetTriggerKeywords(triggersJson, "engagement_boosters");
        List<string> sensitive = GetTriggerKeywords(triggersJson, "sensitive_topics");
        List<string> avoid = GetTriggerKeywords(triggersJson, "avoid_topics");

        double topicSim = 0.0;
        if (ContainsAnyKeyword(fullText, boosters))
        {
            topicSim = 1.0;
        }
        else if (ContainsAnyKeyword(fullText, sensitive))
        {
            topicSim = 0.3;
        }
        else if (ContainsAnyKeyword(fullText, avoid))
        {
            topicSim = 0.0;
        }

        double relevance = (keywordMatch * _relevanceWeightKeywordMatch) +
                           (subredditMatch * _relevanceWeightSubredditMatch) +
                           (topicSim * _relevanceWeightTopicSimilarity);

        return Clamp01(relevance);
    }

    /// <summary>
    /// 计算综合决策分（0.0-1.0）。
    /// </summary>
    public static double CalculateCompositeScore(double hotScore, double activityScore, double relevanceScore)
    {
        double composite = (Clamp01(hotScore) * _compositeWeightHot) +
                           (Clamp01(activityScore) * _compositeWeightActivity) +
                           (Clamp01(relevanceScore) * _compositeWeightRelevance);

        return Clamp01(composite);
    }

    /// <summary>
    /// 根据动作类型与阈值判断是否执行互动。
    /// </summary>
    public static bool ShouldInteract(double compositeScore, string actionType, string configJson)
    {
        if (!_configLoaded && !string.IsNullOrEmpty(configJson))
        {
            LoadConfig(configJson);
        }

        double score = Clamp01(compositeScore);
        double threshold = GetDefaultThreshold(actionType);

        if (!string.IsNullOrEmpty(actionType) && _decisionThresholds.ContainsKey(actionType))
        {
            threshold = _decisionThresholds[actionType];
        }

        return score >= threshold;
    }

    /// <summary>
    /// 检查帖子是否命中避免话题（true=应避免）。
    /// </summary>
    public static bool CheckAvoidTopics(string postJson, string triggersJson)
    {
        if (string.IsNullOrEmpty(postJson) || string.IsNullOrEmpty(triggersJson))
        {
            return false;
        }

        string text = BuildPostText(postJson);
        List<string> avoid = GetTriggerKeywords(triggersJson, "avoid_topics");
        return ContainsAnyKeyword(text, avoid);
    }

    /// <summary>
    /// 检查帖子是否命中敏感话题（true=敏感）。
    /// </summary>
    public static bool CheckSensitiveTopics(string postJson, string triggersJson)
    {
        if (string.IsNullOrEmpty(postJson) || string.IsNullOrEmpty(triggersJson))
        {
            return false;
        }

        string text = BuildPostText(postJson);
        List<string> sensitive = GetTriggerKeywords(triggersJson, "sensitive_topics");
        return ContainsAnyKeyword(text, sensitive);
    }

    /// <summary>
    /// 将时间字符串（30m/2h/5d/1w/3mo）转换为分钟。
    /// </summary>
    public static double ParseTimestampToMinutes(string timestamp)
    {
        if (string.IsNullOrEmpty(timestamp))
        {
            return 0.0;
        }

        string t = timestamp.Trim().ToLowerInvariant();
        if (t.Length == 0)
        {
            return 0.0;
        }

        double value = 0.0;

        if (t.EndsWith("mo"))
        {
            string numberPartMo = t.Substring(0, t.Length - 2);
            value = CoreHelper.SafeParseDouble(numberPartMo, 0.0);
            return Math.Max(0.0, value * 43200.0);
        }

        char unit = t[t.Length - 1];
        string numberPart = t.Substring(0, t.Length - 1);
        value = CoreHelper.SafeParseDouble(numberPart, 0.0);

        if (unit == 'm') return Math.Max(0.0, value);
        if (unit == 'h') return Math.Max(0.0, value * 60.0);
        if (unit == 'd') return Math.Max(0.0, value * 1440.0);
        if (unit == 'w') return Math.Max(0.0, value * 10080.0);

        // fallback: plain numeric string as minutes
        return Math.Max(0.0, CoreHelper.SafeParseDouble(t, 0.0));
    }

    /// <summary>
    /// 标准归一化：Min(value / maxValue, 1.0)。
    /// </summary>
    public static double NormalizeScore(double value, double maxValue)
    {
        if (maxValue <= 0.0) return 0.0;
        if (value <= 0.0) return 0.0;
        return Math.Min(value / maxValue, 1.0);
    }

    /// <summary>
    /// 关键字包含判断（不区分大小写）。
    /// </summary>
    public static bool ContainsKeyword(string text, string keyword)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword))
        {
            return false;
        }

        return text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string BuildPostText(string postJson)
    {
        string title = CoreHelper.JGet(postJson, "title");
        string body = CoreHelper.JGet(postJson, "body");
        StringBuilder sb = new StringBuilder();
        if (!string.IsNullOrEmpty(title)) sb.Append(title).Append(" ");
        if (!string.IsNullOrEmpty(body)) sb.Append(body);
        return sb.ToString();
    }

    private static bool ContainsAnyKeyword(string text, List<string> keywords)
    {
        if (string.IsNullOrEmpty(text) || keywords == null || keywords.Count == 0)
        {
            return false;
        }

        int i = 0;
        while (i < keywords.Count)
        {
            if (ContainsKeyword(text, keywords[i]))
            {
                return true;
            }
            i++;
        }

        return false;
    }

    private static List<string> GetInterestKeywords(string interestsJson)
    {
        List<string> result = new List<string>();
        if (string.IsNullOrEmpty(interestsJson))
        {
            return result;
        }

        AddArrayValues(result, JsonHelper.GetArray(interestsJson, "persona_extracted.primary_interests"));
        AddArrayValues(result, JsonHelper.GetArray(interestsJson, "persona_extracted.hobby_keywords"));
        AddArrayValues(result, JsonHelper.GetArray(interestsJson, "persona_extracted.life_stage_keywords"));

        return DistinctNonEmpty(result);
    }

    private static List<string> GetInterestSubreddits(string interestsJson)
    {
        List<string> result = new List<string>();
        if (string.IsNullOrEmpty(interestsJson))
        {
            return result;
        }

        AddArrayValues(result, JsonHelper.GetArray(interestsJson, "manual_additions.subreddits"));
        return DistinctNonEmpty(result);
    }

    private static List<string> GetTriggerKeywords(string triggersJson, string sectionKey)
    {
        List<string> result = new List<string>();
        if (string.IsNullOrEmpty(triggersJson) || string.IsNullOrEmpty(sectionKey))
        {
            return result;
        }

        string section = JsonHelper.ExtractObject(triggersJson, sectionKey);
        if (string.IsNullOrEmpty(section))
        {
            return result;
        }

        AddArrayValues(result, JsonHelper.GetArray(section, "keywords"));
        return DistinctNonEmpty(result);
    }

    private static void AddArrayValues(List<string> target, string arrayJson)
    {
        if (target == null || string.IsNullOrEmpty(arrayJson))
        {
            return;
        }

        int count = GetArrayCount(arrayJson);
        int i = 0;
        while (i < count)
        {
            string element = JsonHelper.GetArrayElement(arrayJson, i);
            if (!string.IsNullOrEmpty(element))
            {
                string value = Unquote(element).Trim();
                if (!string.IsNullOrEmpty(value))
                {
                    target.Add(value);
                }
            }
            i++;
        }
    }

    private static List<string> DistinctNonEmpty(List<string> values)
    {
        List<string> result = new List<string>();
        if (values == null || values.Count == 0)
        {
            return result;
        }

        Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < values.Count)
        {
            string v = values[i];
            if (!string.IsNullOrEmpty(v))
            {
                string t = v.Trim();
                if (t.Length > 0 && !seen.ContainsKey(t))
                {
                    seen[t] = true;
                    result.Add(t);
                }
            }
            i++;
        }

        return result;
    }

    private static int GetArrayCount(string arrayJson)
    {
        if (string.IsNullOrEmpty(arrayJson))
        {
            return 0;
        }

        string trimmed = arrayJson.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[trimmed.Length - 1] != ']')
        {
            return 0;
        }

        int depthObj = 0;
        int depthArr = 0;
        bool inString = false;
        bool escape = false;
        bool hasToken = false;
        int count = 0;

        int i = 1; // skip '['
        while (i < trimmed.Length - 1) // skip ']'
        {
            char c = trimmed[i];

            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
            }
            else
            {
                if (c == '"')
                {
                    inString = true;
                    hasToken = true;
                }
                else if (c == '{')
                {
                    depthObj++;
                    hasToken = true;
                }
                else if (c == '}')
                {
                    depthObj--;
                }
                else if (c == '[')
                {
                    depthArr++;
                    hasToken = true;
                }
                else if (c == ']')
                {
                    depthArr--;
                }
                else if (c == ',' && depthObj == 0 && depthArr == 0)
                {
                    if (hasToken) count++;
                    hasToken = false;
                }
                else if (!char.IsWhiteSpace(c))
                {
                    hasToken = true;
                }
            }

            i++;
        }

        if (hasToken) count++;
        return count;
    }

    private static string Unquote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        string t = value.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"')
        {
            t = t.Substring(1, t.Length - 2);
            t = t.Replace("\\\"", "\"");
            t = t.Replace("\\\\", "\\");
        }

        return t;
    }

    private static double GetDefaultThreshold(string actionType)
    {
        if (string.Equals(actionType, "click_post", StringComparison.OrdinalIgnoreCase)) return 0.3;
        if (string.Equals(actionType, "like_post", StringComparison.OrdinalIgnoreCase)) return 0.4;
        if (string.Equals(actionType, "comment_post", StringComparison.OrdinalIgnoreCase)) return 0.6;
        if (string.Equals(actionType, "reply_comment", StringComparison.OrdinalIgnoreCase)) return 0.7;
        if (string.Equals(actionType, "continue_browsing", StringComparison.OrdinalIgnoreCase)) return 0.2;
        return 0.5;
    }

    private static double Clamp01(double v)
    {
        if (v < 0.0) return 0.0;
        if (v > 1.0) return 1.0;
        return v;
    }
}
