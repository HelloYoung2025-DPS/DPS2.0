# -*- coding: utf-8 -*-
"""
UI Analyzer — UI Dump 分析引擎
DPS v4.5 App Onboarder 工具

解析 uiautomator XML dump，提供元素查找、页面分类、结构检测等功能。
"""

import re
import xml.etree.ElementTree as ET


class UIElement:
    """UI 元素数据结构"""

    def __init__(self, node):
        """从 XML node 构建 UIElement"""
        self.resource_id = node.get("resource-id", "")
        self.text = node.get("text", "")
        self.content_desc = node.get("content-desc", "")
        self.class_name = node.get("class", "")
        self.package = node.get("package", "")
        self.clickable = node.get("clickable", "false") == "true"
        self.scrollable = node.get("scrollable", "false") == "true"
        self.focusable = node.get("focusable", "false") == "true"
        self.enabled = node.get("enabled", "true") == "true"
        self.checked = node.get("checked", "false") == "true"
        self.index = int(node.get("index", "0"))

        # 解析 bounds
        bounds_str = node.get("bounds", "[0,0][0,0]")
        self.x1, self.y1, self.x2, self.y2 = self._parse_bounds(bounds_str)
        self.cx = (self.x1 + self.x2) // 2
        self.cy = (self.y1 + self.y2) // 2
        self.width = self.x2 - self.x1
        self.height = self.y2 - self.y1

        # 短 resource-id（去掉包名前缀）
        self.short_id = self.resource_id.split("/")[-1] if "/" in self.resource_id else self.resource_id

    def _parse_bounds(self, bounds_str):
        m = re.match(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", bounds_str)
        if m:
            return int(m.group(1)), int(m.group(2)), int(m.group(3)), int(m.group(4))
        return 0, 0, 0, 0

    @property
    def is_visible(self):
        """bounds 非零 = 可见"""
        return not (self.x1 == 0 and self.y1 == 0 and self.x2 == 0 and self.y2 == 0)

    @property
    def is_meaningful(self):
        """有有意义的标识信息"""
        return bool(self.short_id or self.text or self.content_desc)

    @property
    def has_reasonable_size(self):
        """尺寸合理（宽度>30px 且高度>30px）"""
        return self.width > 30 and self.height > 30

    def __repr__(self):
        parts = []
        if self.short_id:
            parts.append("id={}".format(self.short_id))
        if self.text:
            parts.append("text='{}'".format(self.text[:40]))
        if self.content_desc:
            parts.append("desc='{}'".format(self.content_desc[:40]))
        parts.append("cls={}".format(self.class_name.split(".")[-1]))
        if self.clickable:
            parts.append("clickable")
        parts.append("[{},{} {}x{}]".format(self.x1, self.y1, self.width, self.height))
        return "UIElement({})".format(", ".join(parts))


class UIAnalyzer:
    """UI Dump 分析器"""

    # 社交/社区相关关键词（用于识别 feed 页面）
    SOCIAL_KEYWORDS = [
        "community", "feed", "social", "forum", "groups", "club",
        "explore", "discover", "trending", "popular", "home",
        "birth club", "my groups", "posts", "discussions",
        "chat", "messages", "notifications",
        # 中文
        "社区", "论坛", "发现", "推荐", "关注", "首页", "动态",
    ]

    # 帖子相关关键词
    POST_KEYWORDS = [
        "post", "article", "story", "thread", "topic",
        "title", "body", "content", "text", "caption",
        "author", "avatar", "username", "timestamp",
        "comment", "reply", "like", "reaction", "share",
        "upvote", "downvote", "vote", "bookmark", "save",
    ]

    # 底部导航类名特征
    BOTTOM_NAV_CLASSES = [
        "BottomNavigationView",
        "BottomNavigation",
        "bottom_nav",
        "NavigationBarView",
    ]

    def __init__(self, xml_content):
        """
        解析 XML dump

        Args:
            xml_content: uiautomator dump 的 XML 字符串
        """
        self.raw_xml = xml_content
        self._elements = []
        self._parse()

    def _parse(self):
        """解析 XML 为元素列表"""
        try:
            root = ET.fromstring(self.raw_xml)
            self._walk(root)
        except ET.ParseError:
            # 如果 ElementTree 解析失败，用正则兜底
            self._parse_regex()

    def _walk(self, node):
        """递归遍历 XML 树"""
        if node.tag == "node":
            self._elements.append(UIElement(node))
        for child in node:
            self._walk(child)

    def _parse_regex(self):
        """正则解析兜底（处理格式异常的 XML）"""
        for match in re.finditer(r"<node\b([^>]+)/?>", self.raw_xml):
            attrs_str = match.group(1)
            # 构造简易 node 模拟
            attrs = {}
            for attr_match in re.finditer(r'(\w[\w-]*)="([^"]*)"', attrs_str):
                attrs[attr_match.group(1)] = attr_match.group(2)

            class FakeNode:
                def __init__(self, a):
                    self._attrs = a
                def get(self, key, default=""):
                    return self._attrs.get(key, default)

            self._elements.append(UIElement(FakeNode(attrs)))

    # === 基本查找 ===

    def all_elements(self):
        """返回所有元素"""
        return list(self._elements)

    def find_by_resource_id(self, rid, partial=True):
        """
        按 resource-id 查找

        Args:
            rid: 要搜索的 resource-id（或部分匹配）
            partial: 是否允许部分匹配
        """
        results = []
        for el in self._elements:
            if partial:
                if rid in el.resource_id or rid == el.short_id:
                    results.append(el)
            else:
                if el.resource_id == rid or el.short_id == rid:
                    results.append(el)
        return results

    def find_by_text(self, text, exact=True, case_sensitive=True):
        """
        按 text 属性查找

        Args:
            text: 要搜索的文本
            exact: 是否精确匹配
            case_sensitive: 是否区分大小写
        """
        results = []
        for el in self._elements:
            el_text = el.text
            search_text = text
            if not case_sensitive:
                el_text = el_text.lower()
                search_text = search_text.lower()
            if exact:
                if el_text == search_text or el_text.strip() == search_text.strip():
                    results.append(el)
            else:
                if search_text in el_text:
                    results.append(el)
        return results

    def find_by_class(self, class_name, partial=True):
        """按 class 查找"""
        results = []
        for el in self._elements:
            if partial:
                if class_name in el.class_name:
                    results.append(el)
            else:
                if el.class_name == class_name:
                    results.append(el)
        return results

    def find_by_content_desc(self, desc, exact=True):
        """按 content-desc 查找"""
        results = []
        for el in self._elements:
            if exact:
                if el.content_desc == desc:
                    results.append(el)
            else:
                if desc.lower() in el.content_desc.lower():
                    results.append(el)
        return results

    def find_clickable(self):
        """返回所有可点击元素"""
        return [el for el in self._elements if el.clickable]

    def find_visible(self):
        """返回所有可见（bounds 非零）的元素"""
        return [el for el in self._elements if el.is_visible]

    def find_visible_clickable(self):
        """返回可见且可点击的元素"""
        return [el for el in self._elements if el.is_visible and el.clickable and el.has_reasonable_size]

    # === 高级分析 ===

    def detect_bottom_nav(self):
        """
        检测底部导航栏

        Returns:
            dict: {tab_short_id: UIElement} 或 None
        """
        screen_w, screen_h = self._estimate_screen_size()

        # 策略1: 查找 BottomNavigationView 类
        for class_hint in self.BOTTOM_NAV_CLASSES:
            navs = self.find_by_class(class_hint)
            if navs:
                # 找到导航容器，提取其下的 tab 项
                nav = navs[0]
                tabs = self._find_children_in_region(
                    nav.x1, nav.y1, nav.x2, nav.y2,
                    clickable_only=False
                )
                result = {}
                for tab in tabs:
                    key = tab.short_id or tab.text or tab.content_desc
                    if key and tab.is_visible:
                        result[key] = tab
                if result:
                    return result

        # 策略2: 查找 resource-id 包含 "bottom_nav" / "navigation" 的容器
        for keyword in ["bottom_nav", "navigation_bar", "tab_bar"]:
            containers = self.find_by_resource_id(keyword)
            if containers:
                container = containers[0]
                tabs = self._find_children_in_region(
                    container.x1, container.y1, container.x2, container.y2,
                    clickable_only=False
                )
                result = {}
                for tab in tabs:
                    key = tab.short_id or tab.text or tab.content_desc
                    if key and tab.is_visible:
                        result[key] = tab
                if result:
                    return result

        # 策略3: 启发式 — 屏幕底部 15% 区域内的水平排列可点击元素
        bottom_threshold = int(screen_h * 0.85)
        bottom_elements = [
            el for el in self._elements
            if el.y1 >= bottom_threshold and el.is_visible and el.has_reasonable_size
        ]
        if len(bottom_elements) >= 3:
            # 按 x 坐标排序，检查是否水平分布
            sorted_els = sorted(bottom_elements, key=lambda e: e.x1)
            # 检查是否大致等宽等间距
            if self._looks_like_tab_bar(sorted_els):
                result = {}
                for el in sorted_els:
                    key = el.short_id or el.text or el.content_desc
                    if key:
                        result[key] = el
                return result

        return None

    def detect_tabs(self):
        """
        检测顶部 Tab 栏

        Returns:
            list[UIElement]: Tab 元素列表
        """
        # 查找 TabLayout
        tab_layouts = self.find_by_class("TabLayout")
        tab_layouts += self.find_by_resource_id("tabs")
        tab_layouts += self.find_by_resource_id("tab_layout")

        if tab_layouts:
            layout = tab_layouts[0]
            tabs = self._find_children_in_region(
                layout.x1, layout.y1, layout.x2, layout.y2,
                clickable_only=False
            )
            # 过滤出有文本的
            return [t for t in tabs if t.text and t.is_visible]

        return []

    def detect_feed_type(self):
        """
        判断当前页面的 feed 类型

        Returns:
            str: 'recycler_vertical' | 'viewpager_horizontal' | 'scrollview_vertical' | 'unknown'
        """
        # 检查 ViewPager（水平滑动）
        viewpagers = self.find_by_class("ViewPager")
        if viewpagers:
            return "viewpager_horizontal"

        # 检查 RecyclerView（垂直滚动）
        recyclers = self.find_by_class("RecyclerView")
        if recyclers:
            return "recycler_vertical"

        # 检查 ListView
        listviews = self.find_by_class("ListView")
        if listviews:
            return "recycler_vertical"

        # 检查 ScrollView
        scrollviews = self.find_by_class("ScrollView")
        if scrollviews:
            return "scrollview_vertical"

        return "unknown"

    def detect_webview(self):
        """
        检测 WebView 容器

        Returns:
            UIElement | None
        """
        webviews = self.find_by_class("WebView")
        if webviews:
            # 返回最大的（通常是主要内容区域）
            return max(webviews, key=lambda e: e.width * e.height)
        return None

    def detect_post_containers(self):
        """
        查找帖子容器（重复出现的相同结构元素）

        Returns:
            list[UIElement]: 帖子容器元素列表
        """
        # 策略1: 查找 resource-id 包含 post 关键词的容器
        for keyword in ["post", "card", "item", "story", "article", "thread"]:
            containers = self.find_by_resource_id(keyword)
            visible = [c for c in containers if c.is_visible and c.has_reasonable_size]
            if len(visible) >= 2:
                return visible

        # 策略2: 查找同一 resource-id 重复出现 2+ 次的可点击 ViewGroup
        id_counts = {}
        for el in self._elements:
            if el.short_id and el.clickable and el.is_visible and el.has_reasonable_size:
                if "ViewGroup" in el.class_name or "FrameLayout" in el.class_name or "LinearLayout" in el.class_name or "ConstraintLayout" in el.class_name or "RelativeLayout" in el.class_name:
                    id_counts.setdefault(el.short_id, []).append(el)

        for rid, elements in sorted(id_counts.items(), key=lambda x: -len(x[1])):
            if len(elements) >= 2:
                return elements

        return []

    def classify_page(self):
        """
        启发式页面类型分类

        Returns:
            str: 'home' | 'feed' | 'post_detail' | 'profile' | 'settings' | 'webview' | 'unknown'
        """
        # Feed 页面优先检测（WebView 渲染的 feed 也应归类为 feed，如 BabyCenter）
        posts = self.detect_post_containers()
        feed_type = self.detect_feed_type()
        if posts and feed_type != "unknown":
            return "feed"

        # WebView 页面（无 feed 特征的纯 WebView，如帖子详情页）
        if self.detect_webview():
            return "webview"

        # Profile 页面
        profile_hints = self.find_by_resource_id("profile")
        profile_hints += self.find_by_text("followers", exact=False, case_sensitive=False)
        profile_hints += self.find_by_text("following", exact=False, case_sensitive=False)
        if len(profile_hints) >= 2:
            return "profile"

        # Settings 页面
        settings_hints = self.find_by_resource_id("settings")
        settings_hints += self.find_by_text("Settings", exact=False, case_sensitive=False)
        if settings_hints:
            return "settings"

        # Home 页面（有底部导航 + 非 feed 内容）
        if self.detect_bottom_nav():
            return "home"

        return "unknown"

    def find_social_elements(self):
        """
        查找包含社交关键词的元素

        Returns:
            list[tuple[str, UIElement]]: [(keyword, element), ...]
        """
        results = []
        for keyword in self.SOCIAL_KEYWORDS:
            # 按 text 搜索
            found = self.find_by_text(keyword, exact=False, case_sensitive=False)
            for el in found:
                results.append((keyword, el))
            # 按 content-desc 搜索
            found = self.find_by_content_desc(keyword, exact=False)
            for el in found:
                if (keyword, el) not in results:
                    results.append((keyword, el))
        return results

    def find_action_buttons(self):
        """
        查找帖子交互按钮（like, comment, share, bookmark 等）

        Returns:
            dict: {action_type: UIElement}
        """
        actions = {}

        # Like / Reaction 按钮
        like_candidates = (
            self.find_by_resource_id("like") +
            self.find_by_content_desc("Like", exact=False) +
            self.find_by_content_desc("Upvote", exact=False) +
            self.find_by_text("select for list of reactions") +
            self.find_by_resource_id("upvote") +
            self.find_by_resource_id("reaction")
        )
        if like_candidates:
            actions["like"] = like_candidates[0]

        # Comment 按钮
        comment_candidates = (
            self.find_by_resource_id("comment") +
            self.find_by_content_desc("Comment", exact=False) +
            self.find_by_text("ADD A COMMENT") +
            self.find_by_text("Comment") +
            self.find_by_text("Add a comment", exact=False)
        )
        if comment_candidates:
            actions["comment"] = comment_candidates[0]

        # Share 按钮
        share_candidates = (
            self.find_by_resource_id("share") +
            self.find_by_content_desc("Share", exact=False) +
            self.find_by_text("Share")
        )
        if share_candidates:
            actions["share"] = share_candidates[0]

        # Bookmark / Save 按钮
        bookmark_candidates = (
            self.find_by_resource_id("bookmark") +
            self.find_by_resource_id("save") +
            self.find_by_content_desc("Bookmark", exact=False) +
            self.find_by_content_desc("Save", exact=False) +
            self.find_by_text("bookmark post")
        )
        if bookmark_candidates:
            actions["bookmark"] = bookmark_candidates[0]

        # Back 按钮
        back_candidates = (
            self.find_by_content_desc("Navigate up") +
            self.find_by_content_desc("Back") +
            self.find_by_resource_id("back")
        )
        if back_candidates:
            actions["back"] = back_candidates[0]

        return actions

    def get_element_summary(self):
        """
        生成页面元素摘要报告

        Returns:
            dict: 各类元素的统计和关键信息
        """
        total = len(self._elements)
        visible = len([e for e in self._elements if e.is_visible])
        clickable = len([e for e in self._elements if e.clickable])
        with_id = len([e for e in self._elements if e.short_id])
        with_text = len([e for e in self._elements if e.text])

        return {
            "total_elements": total,
            "visible": visible,
            "clickable": clickable,
            "with_resource_id": with_id,
            "with_text": with_text,
            "page_type": self.classify_page(),
            "feed_type": self.detect_feed_type(),
            "has_webview": self.detect_webview() is not None,
            "has_bottom_nav": self.detect_bottom_nav() is not None,
            "post_containers": len(self.detect_post_containers()),
        }

    # === 私有工具方法 ===

    def _estimate_screen_size(self):
        """从元素 bounds 估算屏幕尺寸"""
        max_x = max((el.x2 for el in self._elements if el.is_visible), default=1440)
        max_y = max((el.y2 for el in self._elements if el.is_visible), default=3120)
        return max_x, max_y

    def _find_children_in_region(self, x1, y1, x2, y2, clickable_only=False):
        """查找指定区域内的元素"""
        results = []
        for el in self._elements:
            if el.x1 >= x1 and el.y1 >= y1 and el.x2 <= x2 and el.y2 <= y2:
                if clickable_only and not el.clickable:
                    continue
                if el.is_visible and el.is_meaningful:
                    results.append(el)
        return results

    def _looks_like_tab_bar(self, elements):
        """判断一组元素是否像 tab 栏（水平等间距排列）"""
        if len(elements) < 3:
            return False
        # 检查 y 坐标是否接近
        y_values = [e.cy for e in elements]
        y_spread = max(y_values) - min(y_values)
        if y_spread > 100:
            return False
        return True
