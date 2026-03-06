// =====================================================
// NavigationResolver.cs - BFS 最短路径导航算法
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================
// 核心职责：
//   根据 Manifest 中的 navigation.edges 计算从当前页面到目标页面的最短路径
//   使用广度优先搜索 (BFS) 算法
//
// Manifest navigation 结构：
//   "navigation": {
//     "edges": [
//       { "from": "feed", "to": "profile", "action": "tap", "selector": "nav_profile_btn" },
//       { "from": "profile", "to": "feed", "action": "back" }
//     ]
//   }
// =====================================================

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 导边定义
/// </summary>
public class NavEdge
{
    public string From;
    public string To;
    public string Action;      // "tap", "back", "swipe"
    public string Selector;    // 用于 tap/swipe 的选择器
    public string Params;      // 可选参数（如 swipe 方向）
}

/// <summary>
/// 导航路径（一系列边的序列）
/// </summary>
public class NavPath
{
    public List<NavEdge> Edges;
    public int TotalCost;

    public NavPath()
    {
        Edges = new List<NavEdge>();
        TotalCost = 0;
    }

    public void AddEdge(NavEdge edge)
    {
        Edges.Add(edge);
        TotalCost += 1;  // 每条边的成本为 1（可扩展为加权）
    }
}

/// <summary>
/// 导航解析器 - 计算页面间最短路径
/// </summary>
public class NavigationResolver
{
    private const string TAG = "NavigationResolver";

    // 图结构：from -> List<edge>
    private Dictionary<string, List<NavEdge>> _graph;

    // 所有已知的页面
    private HashSet<string> _nodes;

    public NavigationResolver()
    {
        _graph = new Dictionary<string, List<NavEdge>>();
        _nodes = new HashSet<string>();
    }

    /// <summary>
    /// 从 Manifest 加载导航定义
    /// </summary>
    public bool LoadFromManifest(string manifestJson)
    {
        if (string.IsNullOrEmpty(manifestJson))
        {
            CoreHelper.LogErr(TAG, "Manifest JSON 为空");
            return false;
        }

        // 提取 navigation 对象
        string navJson = JsonHelper.ExtractObject(manifestJson, "navigation");
        if (string.IsNullOrEmpty(navJson))
        {
            CoreHelper.LogWarn(TAG, "Manifest 中无 navigation 定义");
            return false;
        }

        // 提取 edges 数组
        string edgesArray = JsonHelper.Get(navJson, "edges");
        if (string.IsNullOrEmpty(edgesArray) || !edgesArray.TrimStart().StartsWith("["))
        {
            CoreHelper.LogWarn(TAG, "navigation.edges 不是数组或为空");
            return false;
        }

        // 解析边
        // 使用 GetArray 直接获取数组元素
        string[] edgeStrings = JsonHelper.GetArray(navJson, "edges");
        if (edgeStrings == null || edgeStrings.Length == 0)
        {
            CoreHelper.LogWarn(TAG, "edges 数组为空");
            return false;
        }

        int loaded = 0;
        foreach (string edgeJson in edgeStrings)
        {
            NavEdge edge = ParseEdge(edgeJson);
            if (edge != null)
            {
                AddEdge(edge);
                loaded++;
            }
        }

        CoreHelper.Log(TAG, string.Format("加载了 {0} 条导航边，覆盖 {1} 个页面", loaded, _nodes.Count));
        return loaded > 0;
    }

    /// <summary>
    /// 解析单条边
    /// </summary>
    private NavEdge ParseEdge(string edgeJson)
    {
        if (string.IsNullOrEmpty(edgeJson))
        {
            return null;
        }

        string from = JsonHelper.Get(edgeJson, "from");
        string to = JsonHelper.Get(edgeJson, "to");
        string action = JsonHelper.Get(edgeJson, "action");

        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
        {
            CoreHelper.LogWarn(TAG, "边缺少 from 或 to 字段: " + edgeJson);
            return null;
        }

        NavEdge edge = new NavEdge();
        edge.From = from;
        edge.To = to;
        edge.Action = action ?? "tap";  // 默认为 tap
        edge.Selector = JsonHelper.Get(edgeJson, "selector");
        edge.Params = JsonHelper.Get(edgeJson, "params");

        return edge;
    }

    /// <summary>
    /// 添加边到图中
    /// </summary>
    private void AddEdge(NavEdge edge)
    {
        if (!_graph.ContainsKey(edge.From))
        {
            _graph[edge.From] = new List<NavEdge>();
        }
        _graph[edge.From].Add(edge);

        _nodes.Add(edge.From);
        _nodes.Add(edge.To);
    }

    /// <summary>
    /// 计算从 startPage 到 endPage 的最短路径（BFS）
    /// </summary>
    public NavPath FindPath(string startPage, string endPage)
    {
        if (string.IsNullOrEmpty(startPage) || string.IsNullOrEmpty(endPage))
        {
            CoreHelper.LogErr(TAG, "startPage 或 endPage 为空");
            return null;
        }

        if (startPage == endPage)
        {
            // 已在目标页面
            return new NavPath();
        }

        if (!_nodes.Contains(startPage))
        {
            CoreHelper.LogErr(TAG, "起始页面不存在于导航图中: " + startPage);
            return null;
        }

        if (!_nodes.Contains(endPage))
        {
            CoreHelper.LogErr(TAG, "目标页面不存在于导航图中: " + endPage);
            return null;
        }

        // BFS 搜索
        Queue<PathNode> queue = new Queue<PathNode>();
        HashSet<string> visited = new HashSet<string>();

        PathNode startNode = new PathNode();
        startNode.Page = startPage;
        startNode.Path = new NavPath();

        queue.Enqueue(startNode);
        visited.Add(startPage);

        while (queue.Count > 0)
        {
            PathNode current = queue.Dequeue();

            // 检查是否有从当前页面出发的边
            if (!_graph.ContainsKey(current.Page))
            {
                continue;
            }

            foreach (NavEdge edge in _graph[current.Page])
            {
                if (visited.Contains(edge.To))
                {
                    continue;  // 已访问过
                }

                // 创建新路径
                NavPath newPath = new NavPath();
                newPath.Edges.AddRange(current.Path.Edges);
                newPath.AddEdge(edge);

                // 检查是否到达目标
                if (edge.To == endPage)
                {
                    CoreHelper.Log(TAG, string.Format("找到路径: {0} -> {1}, 步数: {2}", startPage, endPage, newPath.Edges.Count));
                    return newPath;
                }

                // 继续搜索
                PathNode nextNode = new PathNode();
                nextNode.Page = edge.To;
                nextNode.Path = newPath;

                visited.Add(edge.To);
                queue.Enqueue(nextNode);
            }
        }

        CoreHelper.LogErr(TAG, string.Format("无法找到从 {0} 到 {1} 的路径", startPage, endPage));
        return null;
    }

    /// <summary>
    /// 获取从指定页面可以直接到达的所有页面
    /// </summary>
    public List<string> GetReachablePages(string fromPage)
    {
        List<string> pages = new List<string>();

        if (_graph.ContainsKey(fromPage))
        {
            foreach (NavEdge edge in _graph[fromPage])
            {
                pages.Add(edge.To);
            }
        }

        return pages;
    }

    /// <summary>
    /// 检查是否可以直接从一个页面到达另一个页面（单步）
    /// </summary>
    public bool CanReachDirectly(string fromPage, string toPage)
    {
        if (!_graph.ContainsKey(fromPage))
        {
            return false;
        }

        foreach (NavEdge edge in _graph[fromPage])
        {
            if (edge.To == toPage)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 获取从 fromPage 到 toPage 的直接边（如果存在）
    /// </summary>
    public NavEdge GetDirectEdge(string fromPage, string toPage)
    {
        if (!_graph.ContainsKey(fromPage))
        {
            return null;
        }

        foreach (NavEdge edge in _graph[fromPage])
        {
            if (edge.To == toPage)
            {
                return edge;
            }
        }

        return null;
    }
}

/// <summary>
/// BFS 搜索节点
/// </summary>
internal class PathNode
{
    public string Page;
    public NavPath Path;
}
