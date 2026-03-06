# -*- coding: utf-8 -*-
"""
App Explorer — 自主 APP 探索引擎
DPS v4.5 App Onboarder 工具

自动探索未知 Android APP 的 UI 结构，发现导航、feed、帖子、交互按钮等。
当启发式失败时，截图并向用户提问。
"""

import time
import os


class AppExplorer:
    """自主 APP 探索引擎"""

    # 社区/Feed tab 的启发式关键词（按优先级排序）
    FEED_TAB_KEYWORDS = [
        "community", "birth club", "forum", "social",
        "feed", "home", "explore", "discover",
        "groups", "discussions", "trending",
        "社区", "论坛", "发现", "关注", "动态",
    ]

    def __init__(self, adb, package_name, platform_key=None, work_dir=None):
        """
        初始化探索引擎

        Args:
            adb: ADBController 实例
            package_name: APP 包名
            platform_key: 平台简称（如 babycenter），None 则从包名推断
            work_dir: 工作目录
        """
        self.adb = adb
        self.package_name = package_name
        self.platform_key = platform_key or package_name.split(".")[-1]
        self.work_dir = work_dir or os.path.expanduser("~")

        # 延迟导入，避免循环依赖
        from ui_analyzer import UIAnalyzer
        self.UIAnalyzer = UIAnalyzer

        # 探索结果
        self.app_map = {
            "package_name": package_name,
            "platform_key": self.platform_key,
            "app_name": "",
            "screen_size": self.adb.get_screen_size(),
            "bottom_nav_tabs": {},
            "feed_tab_key": None,
            "feed_type": "unknown",
            "feed_container_id": "",
            "post_container_id": "",
            "post_elements": {},
            "post_detail_is_webview": False,
            "webview_container_id": "",
            "webview_has_accessibility": False,
            "action_buttons": {},
            "pages": {},
        }

        self.log_entries = []
        self._dump_counter = 0
        self._had_popup = False

    # === 主入口 ===

    def run(self):
        """
        执行完整探索流程

        Returns:
            dict: app_map 完整数据
        """
        self._log("START", "开始探索 APP: {}".format(self.package_name))

        # Phase 1: 首页扫描（致命：失败则无法继续）
        try:
            self._log("PHASE_1", "=== Phase 1: 首页扫描 ===")
            self.explore_home()
        except Exception as e:
            self._log("ERROR", "Phase 1 首页扫描失败（致命）: {}".format(str(e)))
            return self.app_map

        # Phase 2: 导航探索（可恢复：失败则跳过后续 tab）
        try:
            self._log("PHASE_2", "=== Phase 2: 导航探索 ===")
            self.explore_navigation()
        except Exception as e:
            self._log("ERROR", "Phase 2 导航探索异常: {}".format(str(e)))

        # Phase 3: Feed 分析（可恢复）
        try:
            self._log("PHASE_3", "=== Phase 3: Feed 分析 ===")
            self.explore_feed()
        except Exception as e:
            self._log("ERROR", "Phase 3 Feed 分析异常: {}".format(str(e)))

        # Phase 4: 帖子详情分析（可恢复）
        try:
            self._log("PHASE_4", "=== Phase 4: 帖子详情分析 ===")
            self.explore_post_detail()
        except Exception as e:
            self._log("ERROR", "Phase 4 帖子详情分析异常: {}".format(str(e)))

        # Phase 5: 交互按钮发现（可恢复）
        try:
            self._log("PHASE_5", "=== Phase 5: 交互按钮发现 ===")
            self.explore_action_buttons()
        except Exception as e:
            self._log("ERROR", "Phase 5 交互按钮发现异常: {}".format(str(e)))

        self._log("END", "探索完成。收集到 {} 个页面，{} 个操作按钮".format(
            len(self.app_map["pages"]),
            len(self.app_map["action_buttons"])
        ))

        return self.app_map

    # === Phase 1: 首页扫描 ===

    def explore_home(self):
        """扫描 APP 首页，识别底部导航和基本结构"""
        xml = self._dump("home_initial")
        analyzer = self.UIAnalyzer(xml)

        # 检查并关闭可能的弹窗
        self._dismiss_popups(analyzer)

        # 重新 dump（弹窗关闭后）
        xml = self._dump("home_clean")
        analyzer = self.UIAnalyzer(xml)

        # 获取 APP 名
        activity = self.adb.get_current_activity()
        self._log("INFO", "当前 Activity: {}".format(activity))

        # 识别底部导航
        bottom_nav = analyzer.detect_bottom_nav()
        if bottom_nav:
            self._log("SUCCESS", "发现底部导航栏: {} 个 tab".format(len(bottom_nav)))
            for key, el in bottom_nav.items():
                self._log("INFO", "  Tab: {} (id={}, text='{}', desc='{}')".format(
                    key, el.short_id, el.text, el.content_desc
                ))
                self.app_map["bottom_nav_tabs"][key] = {
                    "short_id": el.short_id,
                    "text": el.text,
                    "content_desc": el.content_desc,
                    "cx": el.cx,
                    "cy": el.cy,
                }
        else:
            self._log("WARN", "未检测到底部导航栏")
            # 截图请求用户帮助
            screenshot = self._screenshot("home_no_nav")
            self._ask_user(
                "未检测到底部导航栏。请查看截图确认 APP 当前状态。\n"
                "截图: {}\n"
                "APP 是否有底部导航栏？如果有，请描述各 tab 的位置。".format(screenshot)
            )

        # 记录首页状态
        summary = analyzer.get_element_summary()
        self.app_map["pages"]["home"] = {
            "activity": activity,
            "summary": summary,
            "key_elements": self._extract_key_elements(analyzer),
        }

        self._log("INFO", "首页摘要: {} 个元素, {} 可见, {} 可点击".format(
            summary["total_elements"], summary["visible"], summary["clickable"]
        ))

    # === Phase 2: 导航探索 ===

    def explore_navigation(self):
        """逐个点击导航 tab，发现不同页面"""
        if not self.app_map["bottom_nav_tabs"]:
            self._log("SKIP", "无底部导航，跳过导航探索")
            return

        feed_found = False

        for tab_key, tab_info in self.app_map["bottom_nav_tabs"].items():
            self._log("INFO", "探索 Tab: {} (text='{}')".format(tab_key, tab_info.get("text", "")))

            # 点击 tab
            self.adb.tap(tab_info["cx"], tab_info["cy"])
            self.adb.human_delay(3000, 5000)

            # dump 分析
            xml = self._dump("nav_{}".format(tab_key))
            analyzer = self.UIAnalyzer(xml)

            # 关闭可能的弹窗
            self._dismiss_popups(analyzer)
            if self._had_popup:
                xml = self._dump("nav_{}_clean".format(tab_key))
                analyzer = self.UIAnalyzer(xml)

            # 页面分类
            page_type = analyzer.classify_page()
            activity = self.adb.get_current_activity()
            summary = analyzer.get_element_summary()

            self._log("INFO", "  页面类型: {}, Activity: {}".format(page_type, activity))
            self._log("INFO", "  摘要: feed_type={}, posts={}, webview={}".format(
                summary["feed_type"], summary["post_containers"], summary["has_webview"]
            ))

            # 记录页面
            self.app_map["pages"][tab_key] = {
                "activity": activity,
                "page_type": page_type,
                "summary": summary,
                "key_elements": self._extract_key_elements(analyzer),
            }

            # 检查是否是 feed 页面
            if page_type == "feed" and not feed_found:
                self.app_map["feed_tab_key"] = tab_key
                self._log("SUCCESS", "  ★ 发现 Feed 页面: {}".format(tab_key))
                feed_found = True

                # 记录 feed 详情
                self.app_map["feed_type"] = summary["feed_type"]
                posts = analyzer.detect_post_containers()
                if posts:
                    self.app_map["post_container_id"] = posts[0].short_id
                    self._log("INFO", "  帖子容器 ID: {}".format(posts[0].short_id))

                # 查找 feed 容器
                feed_type = analyzer.detect_feed_type()
                if feed_type == "viewpager_horizontal":
                    vp = analyzer.find_by_class("ViewPager")
                    if vp:
                        self.app_map["feed_container_id"] = vp[0].short_id
                elif feed_type == "recycler_vertical":
                    rv = analyzer.find_by_class("RecyclerView")
                    if rv:
                        self.app_map["feed_container_id"] = rv[0].short_id

            # 检查社交关键词匹配
            if not feed_found:
                social = analyzer.find_social_elements()
                if social:
                    for keyword, el in social[:3]:
                        self._log("INFO", "  社交关键词 '{}' → {} (id={})".format(
                            keyword, el.text or el.content_desc, el.short_id
                        ))

        if not feed_found:
            self._log("WARN", "未自动发现 Feed 页面，尝试启发式搜索...")
            self._heuristic_find_feed()

    def _heuristic_find_feed(self):
        """启发式搜索 feed 页面"""
        # 策略1: 在所有已发现页面中，找包含最多社交关键词的
        best_tab = None
        best_score = 0

        for tab_key, page in self.app_map["pages"].items():
            if tab_key == "home":
                continue
            score = page.get("summary", {}).get("post_containers", 0)
            # 加分：tab 名包含社交关键词
            tab_info = self.app_map["bottom_nav_tabs"].get(tab_key, {})
            tab_text = (tab_info.get("text", "") + " " + tab_info.get("content_desc", "")).lower()
            for kw in self.FEED_TAB_KEYWORDS:
                if kw in tab_text:
                    score += 5
            if score > best_score:
                best_score = score
                best_tab = tab_key

        if best_tab and best_score > 0:
            self.app_map["feed_tab_key"] = best_tab
            self._log("INFO", "启发式选择 feed tab: {} (score={})".format(best_tab, best_score))
        else:
            # 最后手段：截图问用户
            screenshot = self._screenshot("no_feed_found")
            answer = self._ask_user(
                "未能自动识别 Feed/社区页面。\n"
                "截图: {}\n"
                "请告诉我哪个 tab 是社区/feed 页面？可用 tab: {}".format(
                    screenshot,
                    ", ".join(self.app_map["bottom_nav_tabs"].keys())
                )
            )
            if answer:
                self.app_map["feed_tab_key"] = answer.strip()

    # === Phase 3: Feed 分析 ===

    def explore_feed(self):
        """深入分析 feed 页面结构"""
        feed_tab = self.app_map.get("feed_tab_key")
        if not feed_tab:
            self._log("SKIP", "未确定 feed tab，跳过 feed 分析")
            return

        # 导航到 feed
        tab_info = self.app_map["bottom_nav_tabs"].get(feed_tab)
        if tab_info:
            self.adb.tap(tab_info["cx"], tab_info["cy"])
            self.adb.human_delay(3000, 5000)

        xml = self._dump("feed_detail")
        analyzer = self.UIAnalyzer(xml)

        # 分析帖子内的元素
        posts = analyzer.detect_post_containers()
        if posts and len(posts) >= 1:
            # 取一个可见帖子的区域
            post = None
            for p in posts:
                if p.is_visible and p.has_reasonable_size:
                    post = p
                    break
            if not post:
                post = posts[0]

            # 在帖子区域内查找子元素
            children = analyzer._find_children_in_region(
                post.x1, post.y1, post.x2, post.y2
            )

            self._log("INFO", "帖子区域 [{}] 内发现 {} 个子元素".format(
                post.short_id, len(children)
            ))

            # 识别帖子子元素
            for child in children:
                role = self._guess_post_element_role(child)
                if role:
                    if child.short_id:
                        self.app_map["post_elements"][role] = {
                            "strategy": "resource-id",
                            "value": child.short_id,
                        }
                    elif child.text:
                        self.app_map["post_elements"][role] = {
                            "strategy": "text",
                            "value": child.text,
                        }
                    self._log("INFO", "  帖子元素 [{}]: id={}, text='{}'".format(
                        role, child.short_id, child.text[:30] if child.text else ""
                    ))

        # 尝试滑动验证 feed 类型
        feed_type = self.app_map.get("feed_type", "unknown")
        if feed_type == "viewpager_horizontal":
            self._log("INFO", "验证水平滑动 feed...")
            self.adb.swipe_left()
            self.adb.human_delay(1500, 2500)
            xml2 = self._dump("feed_after_swipe")
            analyzer2 = self.UIAnalyzer(xml2)
            if analyzer2.detect_post_containers():
                self._log("SUCCESS", "水平滑动后仍有帖子，确认 ViewPager feed")
        elif feed_type == "recycler_vertical":
            self._log("INFO", "验证垂直滚动 feed...")
            self.adb.scroll_down(600)
            self.adb.human_delay(1500, 2500)
            xml2 = self._dump("feed_after_scroll")
            analyzer2 = self.UIAnalyzer(xml2)
            if analyzer2.detect_post_containers():
                self._log("SUCCESS", "垂直滚动后仍有帖子，确认 RecyclerView feed")

    def _guess_post_element_role(self, element):
        """猜测帖子内元素的角色"""
        sid = element.short_id.lower() if element.short_id else ""
        text = element.text.lower() if element.text else ""
        cls = element.class_name

        # Title
        if any(k in sid for k in ["title", "headline"]):
            return "title"
        # Body / Text
        if any(k in sid for k in ["text", "body", "content", "description", "caption"]):
            return "body"
        # Author
        if any(k in sid for k in ["author", "user", "subtitle", "name"]):
            return "author"
        # Avatar
        if any(k in sid for k in ["avatar", "photo", "profile_image"]):
            return "avatar"
        # Group reference
        if any(k in sid for k in ["group", "community", "subreddit", "club"]):
            return "group"
        # Comment count
        if "comment" in sid and ("count" in sid or "num" in sid):
            return "comment_count"
        # Reactions count
        if any(k in sid for k in ["reaction", "like"]) and ("count" in sid or "num" in sid):
            return "reactions_count"
        # Timestamp
        if any(k in sid for k in ["time", "date", "timestamp", "posted"]):
            return "timestamp"

        # 基于 class 的猜测
        if "ImageView" in cls and element.width < 100 and element.height < 100:
            if not any(k in sid for k in ["icon", "indicator"]):
                return "avatar"  # 小图片通常是头像

        return None

    # === Phase 4: 帖子详情分析 ===

    def explore_post_detail(self):
        """打开一个帖子，分析详情页"""
        # 确保在 feed 页面
        feed_tab = self.app_map.get("feed_tab_key")
        if feed_tab:
            tab_info = self.app_map["bottom_nav_tabs"].get(feed_tab)
            if tab_info:
                self.adb.tap(tab_info["cx"], tab_info["cy"])
                self.adb.human_delay(2000, 3000)

        xml = self._dump("pre_post_tap")
        analyzer = self.UIAnalyzer(xml)

        # 找到一个可点击的帖子
        posts = analyzer.detect_post_containers()
        target = None
        for p in posts:
            if p.is_visible and p.has_reasonable_size and p.clickable:
                target = p
                break

        if not target:
            self._log("WARN", "未找到可点击的帖子容器")
            # 尝试点击帖子标题
            titles = analyzer.find_by_resource_id("title")
            for t in titles:
                if t.is_visible and t.has_reasonable_size:
                    target = t
                    break

        if not target:
            self._log("ERROR", "无法找到帖子入口")
            return

        target_label = target.short_id or (target.text[:20] if target.text else "unknown")
        self._log("INFO", "点击帖子: {} at ({}, {})".format(
            target_label, target.cx, target.cy
        ))
        self.adb.tap(target.cx, target.cy)
        self.adb.human_delay(5000, 8000)  # WebView 加载慢

        # 分析详情页
        xml = self._dump("post_detail")
        analyzer = self.UIAnalyzer(xml)

        activity = self.adb.get_current_activity()
        self._log("INFO", "详情页 Activity: {}".format(activity))

        # 判断是否 WebView
        webview = analyzer.detect_webview()
        if webview:
            self.app_map["post_detail_is_webview"] = True
            self._log("SUCCESS", "检测到 WebView: {} (bounds={})".format(
                webview.class_name, "[{},{} {}x{}]".format(
                    webview.x1, webview.y1, webview.width, webview.height
                )
            ))

            # 查找 WebView 容器 ID
            wv_containers = analyzer.find_by_class("WebView")
            for wv in wv_containers:
                # 找到包含 WebView 的父容器
                parent_ids = analyzer.find_by_resource_id("webView", partial=True)
                if parent_ids:
                    self.app_map["webview_container_id"] = parent_ids[0].short_id
                    break
            # 兜底
            if not self.app_map["webview_container_id"]:
                # 查找常见 WebView 容器 ID
                for cid in ["webViewLayout", "web_view", "webview", "web_container"]:
                    found = analyzer.find_by_resource_id(cid)
                    if found:
                        self.app_map["webview_container_id"] = found[0].short_id
                        break

            # 检查 WebView 内是否有 accessibility nodes
            self._explore_webview_accessibility(analyzer)
        else:
            self.app_map["post_detail_is_webview"] = False
            self._log("INFO", "帖子详情使用原生 UI（非 WebView）")

            # 直接查找 action buttons
            actions = analyzer.find_action_buttons()
            for action_name, el in actions.items():
                if el.short_id:
                    self.app_map["action_buttons"][action_name] = {
                        "strategy": "resource-id",
                        "value": el.short_id,
                    }
                elif el.content_desc:
                    self.app_map["action_buttons"][action_name] = {
                        "strategy": "content-desc",
                        "value": el.content_desc,
                    }
                elif el.text:
                    self.app_map["action_buttons"][action_name] = {
                        "strategy": "text",
                        "value": el.text,
                    }

        # 记录详情页
        self.app_map["pages"]["post_detail"] = {
            "activity": activity,
            "page_type": "webview" if webview else "native",
            "summary": analyzer.get_element_summary(),
            "key_elements": self._extract_key_elements(analyzer),
        }

        # 返回 feed
        self.adb.press_back()
        self.adb.human_delay(1500, 2500)

    def _explore_webview_accessibility(self, analyzer):
        """深度分析 WebView 的 accessibility nodes"""
        self._log("INFO", "检查 WebView accessibility nodes...")

        # 首先检查当前 dump 中是否有 WebView 内部文本节点
        all_text_elements = [
            el for el in analyzer.all_elements()
            if el.text and el.clickable and "Button" in el.class_name
        ]

        # 搜索反应/点赞相关的文本
        reaction_keywords = [
            "select for list of reactions", "like", "love",
            "reaction", "upvote", "thumbs up",
        ]
        comment_keywords = [
            "ADD A COMMENT", "Comment", "comment",
            "Add a comment", "reply", "Write a comment",
        ]
        bookmark_keywords = [
            "bookmark", "save", "Bookmark", "Save",
        ]

        found_buttons = {}

        for el in all_text_elements:
            text_lower = el.text.lower()

            # Like / Reaction
            for kw in reaction_keywords:
                if kw.lower() in text_lower:
                    if "like" not in found_buttons:
                        found_buttons["like"] = el
                        self._log("SUCCESS", "  发现 Like 按钮: text='{}' (bounds={})".format(
                            el.text, "[{},{} {}x{}]".format(el.x1, el.y1, el.width, el.height)
                        ))
                    break

            # Comment
            for kw in comment_keywords:
                if kw.lower() in text_lower or kw == el.text:
                    if "comment" not in found_buttons:
                        found_buttons["comment"] = el
                        self._log("SUCCESS", "  发现 Comment 按钮: text='{}' (bounds={})".format(
                            el.text, "[{},{} {}x{}]".format(el.x1, el.y1, el.width, el.height)
                        ))
                    break

            # Bookmark
            for kw in bookmark_keywords:
                if kw.lower() in text_lower:
                    if "bookmark" not in found_buttons:
                        found_buttons["bookmark"] = el
                        self._log("SUCCESS", "  发现 Bookmark 按钮: text='{}'".format(el.text))
                    break

        # 如果在当前可视区域没找到，尝试滚动后再找
        latest_analyzer = analyzer
        if not found_buttons:
            self._log("INFO", "当前区域未发现交互按钮，尝试滚动查找...")
            for i in range(6):
                self.adb.scroll_down(600)
                self.adb.human_delay(1500, 2500)

                xml = self._dump("wv_scroll_{}".format(i))
                scroll_analyzer = self.UIAnalyzer(xml)
                latest_analyzer = scroll_analyzer

                for el in scroll_analyzer.all_elements():
                    if not el.clickable or "Button" not in el.class_name:
                        continue
                    text_lower = el.text.lower() if el.text else ""

                    for kw in reaction_keywords:
                        if kw.lower() in text_lower and "like" not in found_buttons:
                            found_buttons["like"] = el
                            self._log("SUCCESS", "  滚动后发现 Like: text='{}'".format(el.text))

                    for kw in comment_keywords:
                        if (kw.lower() in text_lower or kw == el.text) and "comment" not in found_buttons:
                            found_buttons["comment"] = el
                            self._log("SUCCESS", "  滚动后发现 Comment: text='{}'".format(el.text))

                    for kw in bookmark_keywords:
                        if kw.lower() in text_lower and "bookmark" not in found_buttons:
                            found_buttons["bookmark"] = el
                            self._log("SUCCESS", "  滚动后发现 Bookmark: text='{}'".format(el.text))

                # 如果主要按钮都找到了，停止滚动
                if "like" in found_buttons and "comment" in found_buttons:
                    break

        # 同时查找评论输入区域（使用最新的 analyzer，避免 stale 数据）
        for el in latest_analyzer.all_elements():
            if "add a comment" in (el.text or "").lower():
                found_buttons["comment_input"] = el
                self._log("SUCCESS", "  发现 Comment Input: text='{}'".format(el.text))
                break

        # 查找原生 action buttons（WebView 外部）
        native_actions = latest_analyzer.find_action_buttons()
        for action_name, el in native_actions.items():
            if action_name not in found_buttons:
                found_buttons[action_name] = el

        # 写入 app_map
        if found_buttons:
            self.app_map["webview_has_accessibility"] = True
            for action_name, el in found_buttons.items():
                if el.text:
                    self.app_map["action_buttons"][action_name] = {
                        "strategy": "text",
                        "value": el.text,
                    }
                elif el.content_desc:
                    self.app_map["action_buttons"][action_name] = {
                        "strategy": "content-desc",
                        "value": el.content_desc,
                    }
                elif el.short_id:
                    self.app_map["action_buttons"][action_name] = {
                        "strategy": "resource-id",
                        "value": el.short_id,
                    }
        else:
            self.app_map["webview_has_accessibility"] = False
            self._log("WARN", "WebView 未暴露 accessibility nodes")

        # 也查找 Comment 按钮的 fallback
        comment_btn = self.app_map["action_buttons"].get("comment")
        if comment_btn:
            # 查找备选评论按钮
            all_comment_els = []
            for el in latest_analyzer.all_elements():
                text_lower = (el.text or "").lower()
                for kw in comment_keywords:
                    if kw.lower() in text_lower and el.text != comment_btn.get("value"):
                        all_comment_els.append(el)
                        break
            if all_comment_els:
                comment_btn["fallback_value"] = all_comment_els[0].text

    # === Phase 5: 交互按钮发现 ===

    def explore_action_buttons(self):
        """汇总所有发现的交互按钮"""
        self._log("INFO", "交互按钮汇总:")
        for action, info in self.app_map["action_buttons"].items():
            self._log("INFO", "  {}: strategy={}, value='{}'".format(
                action, info.get("strategy"), info.get("value")
            ))

        # 检查是否有缺失的关键操作
        required = ["like", "comment", "back"]
        missing = [r for r in required if r not in self.app_map["action_buttons"]]
        if missing:
            self._log("WARN", "缺失操作按钮: {}".format(", ".join(missing)))

        # 确保 back 按钮存在
        if "back" not in self.app_map["action_buttons"]:
            self.app_map["action_buttons"]["back"] = {
                "strategy": "content-desc",
                "value": "Navigate up",
                "fallback_strategy": "content-desc",
                "fallback_value": "Back",
            }

    # === 辅助方法 ===

    def _dump(self, name):
        """执行 UI dump 并返回 XML"""
        self._dump_counter += 1
        full_name = "{}_{}_{:03d}".format(self.platform_key, name, self._dump_counter)
        return self.adb.dump_ui(full_name)

    def _screenshot(self, name):
        """截图并返回路径"""
        full_name = "{}_{}".format(self.platform_key, name)
        return self.adb.screenshot(full_name)

    def _dismiss_popups(self, analyzer):
        """检测并关闭常见弹窗（支持连锁弹窗，最多关闭 5 个）"""
        self._had_popup = False

        # 常见弹窗关闭按钮
        popup_dismiss_ids = [
            "closeBtn", "close_button", "dismiss", "cancel",
            "not_now", "no_thanks", "skip", "later",
            "close", "btn_close", "iv_close",
        ]
        popup_dismiss_texts = [
            "Close", "Not now", "No thanks", "Skip", "Later",
            "Maybe later", "Dismiss", "Cancel", "OK", "Got it",
            "Not Now", "NO THANKS",
        ]

        max_popup_attempts = 5
        for attempt in range(max_popup_attempts):
            dismissed = False

            for rid in popup_dismiss_ids:
                elements = analyzer.find_by_resource_id(rid)
                for el in elements:
                    if el.is_visible and el.clickable:
                        self._log("INFO", "关闭弹窗 (#{}/{}): id={}".format(
                            attempt + 1, max_popup_attempts, el.short_id
                        ))
                        self.adb.tap(el.cx, el.cy)
                        self.adb.human_delay(800, 1500)
                        self._had_popup = True
                        dismissed = True
                        break
                if dismissed:
                    break

            if not dismissed:
                for text in popup_dismiss_texts:
                    elements = analyzer.find_by_text(text)
                    for el in elements:
                        if el.is_visible and el.clickable:
                            self._log("INFO", "关闭弹窗 (#{}/{}): text='{}'".format(
                                attempt + 1, max_popup_attempts, el.text
                            ))
                            self.adb.tap(el.cx, el.cy)
                            self.adb.human_delay(800, 1500)
                            self._had_popup = True
                            dismissed = True
                            break
                    if dismissed:
                        break

            if not dismissed:
                # 没有更多弹窗了
                break

            # 重新 dump 以检查下一个弹窗
            try:
                xml = self._dump("popup_{}".format(attempt))
                analyzer = self.UIAnalyzer(xml)
            except Exception:
                break

    def _extract_key_elements(self, analyzer):
        """从 analyzer 提取关键元素信息（用于 page_signatures）"""
        key_elements = []
        for el in analyzer.find_visible():
            if el.short_id and el.has_reasonable_size:
                key_elements.append({
                    "strategy": "resource-id",
                    "value": el.short_id,
                    "class": el.class_name,
                })
        # 限制数量
        return key_elements[:20]

    def _ask_user(self, question):
        """向用户提问（当启发式失败时）"""
        self._log("ASK", question)
        print("\n" + "=" * 50)
        print("[需要人工确认]")
        print(question)
        print("=" * 50)
        try:
            answer = input("\n请输入回答（直接回车跳过）: ").strip()
            if answer:
                self._log("ANSWER", "用户回答: {}".format(answer))
            return answer
        except (EOFError, KeyboardInterrupt):
            return ""

    def _log(self, level, message):
        """记录探索日志"""
        entry = "[{}] {}".format(level, message)
        self.log_entries.append(entry)

        # 同时输出到控制台
        color_map = {
            "SUCCESS": "\033[92m",  # 绿
            "WARN": "\033[93m",     # 黄
            "ERROR": "\033[91m",    # 红
            "ASK": "\033[96m",      # 青
        }
        reset = "\033[0m"
        color = color_map.get(level, "")
        if color:
            print("{}{}{}".format(color, entry, reset))
        else:
            print(entry)
