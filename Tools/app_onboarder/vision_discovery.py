# -*- coding: utf-8 -*-
# pyright: reportGeneralTypeIssues=false, reportArgumentType=false, reportAttributeAccessIssue=false, reportMissingImports=false, reportOptionalMemberAccess=false
"""
Vision Discovery - Vision AI 辅助的 APP 功能发现引擎
DPS v4.5 App Onboarder 工具

利用 Vision AI 模型分析 APP 截图，自动发现启发式方法无法覆盖的 APP 特有功能。
"""

import os
import re
import json
import time
import base64
import hashlib

try:
    # Python 3
    import urllib.request
    import urllib.error
    _urlopen = urllib.request.urlopen
    _Request = urllib.request.Request
    _URLError = urllib.error.URLError
    _HTTPError = urllib.error.HTTPError
except ImportError:
    # Python 2
    import urllib2
    _urlopen = urllib2.urlopen
    _Request = urllib2.Request
    _URLError = urllib2.URLError
    _HTTPError = urllib2.HTTPError


def default_vision_config():
    """返回默认的 VisionDiscoveryClient 配置"""
    return {
        "enabled": False,
        "provider": "openai",
        "model": "gpt-4o-mini",
        "api_url": "https://api.openai.com/v1/chat/completions",
        "api_key_env": "OPENAI_API_KEY",
        "max_pages": 10,
        "max_calls": 15,
        "confidence_threshold": 0.6,
        "request_timeout_sec": 30,
        "max_retries": 2,
        "retry_backoff_sec": 2.0,
        "dedupe_by_artifact_hash": True,
        "include_ui_catalog": True,
        "max_ui_elements": 30,
        "max_features_per_page": 15,
        "analyze_page_types": ["home", "feed", "post_detail", "webview", "sub_page"],
        "fail_open": True,
    }


class VisionDiscoveryClient(object):
    """Vision AI 功能发现客户端

    通过调用 Vision AI 模型分析 APP 截图，自动发现 UI 中的可交互元素和 APP 特有功能。
    支持成本控制（页面去重、调用次数限制）和优雅的错误降级（fail-open）。
    """

    def __init__(self, config, logger=None):
        """
        初始化 Vision 功能发现客户端

        Args:
            config: 配置字典，键值说明见 default_vision_config()
            logger: 日志回调函数，签名 (level, message)，默认使用 print 输出
        """
        self._config = config or {}
        self._logger = logger or self._default_logger

        # 核心配置
        self._enabled = self._config.get("enabled", False)
        self._provider = self._config.get("provider", "openai")
        self._model = self._config.get("model", "gpt-4o-mini")
        self._api_url = self._config.get("api_url", "https://api.openai.com/v1/chat/completions")
        self._api_key_env = self._config.get("api_key_env", "OPENAI_API_KEY")
        # 优先使用 config 中直接传入的 api_key，其次从环境变量读取
        self._api_key = self._config.get("api_key", "") or os.environ.get(self._api_key_env, "")

        # 成本控制
        self._max_pages = self._config.get("max_pages", 10)
        self._max_calls = self._config.get("max_calls", 15)
        self._confidence_threshold = self._config.get("confidence_threshold", 0.6)

        # 网络
        self._request_timeout_sec = self._config.get("request_timeout_sec", 30)
        self._max_retries = self._config.get("max_retries", 2)
        self._retry_backoff_sec = self._config.get("retry_backoff_sec", 2.0)

        # 功能开关
        self._dedupe_by_hash = self._config.get("dedupe_by_artifact_hash", True)
        self._include_ui_catalog = self._config.get("include_ui_catalog", True)
        self._max_ui_elements = self._config.get("max_ui_elements", 30)
        self._max_features_per_page = self._config.get("max_features_per_page", 15)
        self._analyze_page_types = self._config.get(
            "analyze_page_types",
            ["home", "feed", "post_detail", "webview", "sub_page"]
        )
        self._fail_open = self._config.get("fail_open", True)

        # 运行时状态
        self._call_count = 0
        self._pages_analyzed = 0
        self._features_found = 0
        self._artifact_hashes_seen = set()
        self._errors = []

    # ------------------------------------------------------------------
    # 公共接口
    # ------------------------------------------------------------------

    def is_enabled(self):
        """
        判断 Vision 发现功能是否可用

        Returns:
            bool: 仅当 enabled=True 且 API Key 非空时返回 True
        """
        return bool(self._enabled and self._api_key)

    def analyze_page(self, page_artifact):
        """
        分析单个页面截图，发现可交互 UI 元素

        Args:
            page_artifact: 页面信息字典，包含以下键:
                - page_key (str): 页面标识，如 "home"
                - phase_name (str): 所属探索阶段，如 "phase_1"
                - page_type (str): 页面类型，如 "home", "feed"
                - activity (str): Android Activity 名称
                - screenshot_path (str): 截图文件路径
                - xml_content (str): uiautomator XML dump 内容
                - ui_catalog (list): 可点击元素列表

        Returns:
            dict: VisionPageDiscoveryResult，结构如下:
                - page_key (str)
                - phase_name (str)
                - page_type (str)
                - features (list): VisionDiscoveredFeature 列表
                - feature_count (int)
                - errors (list): 错误信息列表
                - skipped (bool): 是否被跳过
                - skip_reason (str|None): 跳过原因
        """
        page_key = page_artifact.get("page_key", "unknown")
        phase_name = page_artifact.get("phase_name", "unknown")
        page_type = page_artifact.get("page_type", "unknown")

        # 构建空结果模板
        result = {
            "page_key": page_key,
            "phase_name": phase_name,
            "page_type": page_type,
            "features": [],
            "feature_count": 0,
            "errors": [],
            "skipped": False,
            "skip_reason": None,
        }

        # 检查: 功能是否启用
        if not self.is_enabled():
            result["skipped"] = True
            result["skip_reason"] = "Vision 发现功能未启用或 API Key 为空"
            return result

        # 检查: API 调用次数限制
        if self._call_count >= self._max_calls:
            result["skipped"] = True
            result["skip_reason"] = "已达 API 最大调用次数限制 ({})".format(self._max_calls)
            self._logger("WARN", "[VisionDiscovery] 页面 {} 被跳过: {}".format(
                page_key, result["skip_reason"]
            ))
            return result

        # 检查: 已分析页面数限制
        if self._pages_analyzed >= self._max_pages:
            result["skipped"] = True
            result["skip_reason"] = "已达最大页面分析数限制 ({})".format(self._max_pages)
            self._logger("WARN", "[VisionDiscovery] 页面 {} 被跳过: {}".format(
                page_key, result["skip_reason"]
            ))
            return result

        # 检查: 页面类型是否在分析范围内
        if page_type not in self._analyze_page_types:
            result["skipped"] = True
            result["skip_reason"] = "页面类型 '{}' 不在分析范围内".format(page_type)
            self._logger("INFO", "[VisionDiscovery] 页面 {} 被跳过: {}".format(
                page_key, result["skip_reason"]
            ))
            return result

        # 检查: 截图去重
        if self._dedupe_by_hash:
            artifact_hash = self._hash_artifact(page_artifact)
            if artifact_hash in self._artifact_hashes_seen:
                result["skipped"] = True
                result["skip_reason"] = "截图指纹重复，跳过重复分析"
                self._logger("INFO", "[VisionDiscovery] 页面 {} 被跳过: {}".format(
                    page_key, result["skip_reason"]
                ))
                return result
            self._artifact_hashes_seen.add(artifact_hash)

        # 加载截图
        screenshot_path = page_artifact.get("screenshot_path", "")
        image_b64 = self._load_image_base64(screenshot_path)
        if not image_b64:
            error_msg = "无法加载截图文件: {}".format(screenshot_path)
            result["errors"].append(error_msg)
            self._errors.append(error_msg)
            self._logger("ERROR", "[VisionDiscovery] {}".format(error_msg))
            return result

        # 构建提示词和请求载荷
        try:
            prompt = self._build_prompt(page_artifact)
            payload = self._build_openai_payload(prompt, image_b64)
        except Exception as e:
            error_msg = "构建请求失败: {}".format(str(e))
            result["errors"].append(error_msg)
            self._errors.append(error_msg)
            self._logger("ERROR", "[VisionDiscovery] {}".format(error_msg))
            return result

        # 发送 API 请求
        headers = {
            "Content-Type": "application/json",
            "Authorization": "Bearer {}".format(self._api_key),
        }

        self._logger("INFO", "[VisionDiscovery] 正在分析页面 {} ({})...".format(page_key, page_type))
        raw_response = self._post_json(self._api_url, headers, payload)
        self._call_count += 1

        if raw_response is None:
            error_msg = "API 请求失败，页面 {}".format(page_key)
            result["errors"].append(error_msg)
            self._errors.append(error_msg)
            self._logger("ERROR", "[VisionDiscovery] {}".format(error_msg))
            if self._fail_open:
                return result
            return result

        # 解析响应
        try:
            raw_features = self._parse_vision_response(raw_response)
        except Exception as e:
            error_msg = "解析 API 响应失败: {}".format(str(e))
            result["errors"].append(error_msg)
            self._errors.append(error_msg)
            self._logger("ERROR", "[VisionDiscovery] {}".format(error_msg))
            return result

        # 标准化每个发现的功能
        features = []
        for idx, raw_feat in enumerate(raw_features):
            normalized = self._normalize_feature(raw_feat, page_artifact, idx)
            if normalized is not None:
                features.append(normalized)

        # 限制每页最大功能数
        if len(features) > self._max_features_per_page:
            features = features[:self._max_features_per_page]

        result["features"] = features
        result["feature_count"] = len(features)
        self._pages_analyzed += 1
        self._features_found += len(features)

        self._logger("SUCCESS", "[VisionDiscovery] 页面 {} 发现 {} 个功能".format(
            page_key, len(features)
        ))

        return result

    def analyze_pages(self, page_artifacts):
        """
        批量分析多个页面截图

        先应用成本控制策略过滤页面，再逐个调用 analyze_page。

        Args:
            page_artifacts: 页面信息字典列表

        Returns:
            list: VisionPageDiscoveryResult 列表
        """
        if not page_artifacts:
            return []

        filtered = self._apply_cost_controls(page_artifacts)
        self._logger("INFO", "[VisionDiscovery] 批量分析: 输入 {} 页, 过滤后 {} 页".format(
            len(page_artifacts), len(filtered)
        ))

        results = []
        for artifact in filtered:
            result = self.analyze_page(artifact)
            results.append(result)

        return results

    def get_stats(self):
        """
        获取运行统计信息

        Returns:
            dict: 包含 pages_analyzed, calls_made, errors, features_found
        """
        return {
            "pages_analyzed": self._pages_analyzed,
            "calls_made": self._call_count,
            "errors": list(self._errors),
            "features_found": self._features_found,
        }

    # ------------------------------------------------------------------
    # 内部方法: 提示词与载荷构建
    # ------------------------------------------------------------------

    def _build_prompt(self, page_artifact):
        """
        构建发送给 Vision AI 的结构化提示词

        这是整个模块最关键的方法。提示词指导模型:
        1. 识别截图中所有可交互的 UI 元素
        2. 为每个元素生成结构化描述（标签、动作类型、归一化坐标等）
        3. 聚焦 APP 特有功能，忽略系统级通用元素

        Args:
            page_artifact: 页面信息字典

        Returns:
            str: 完整的提示词文本
        """
        page_type = page_artifact.get("page_type", "unknown")
        activity = page_artifact.get("activity", "unknown")

        prompt_parts = []

        # 主指令
        prompt_parts.append(
            "你是一个 Android APP UI 分析专家。"
            "分析以下 APP 截图，找出所有可交互的 UI 元素和 APP 特有功能。\n"
        )

        # 页面上下文
        prompt_parts.append(
            "当前页面: {page_type} ({activity})\n"
            "APP 类型: 根据截图自行判断\n".format(
                page_type=page_type,
                activity=activity,
            )
        )

        # JSON 输出格式说明
        prompt_parts.append(
            "请为每个发现的交互元素返回以下 JSON 结构:\n"
            "[\n"
            "  {{\n"
            '    "feature_label": "元素的中文描述（如：开始训练按钮）",\n'
            '    "feature_key": "snake_case 英文标识（如：start_workout）",\n'
            '    "action_type": "tap|long_press|input|scroll|toggle|select|open|submit",\n'
            '    "intent_type": "primary_action|secondary_action|navigation|'
            'content_action|transactional_action|filter_action",\n'
            '    "confidence": 0.85,\n'
            '    "target_text": "按钮上可见的文字（如果有）",\n'
            '    "target_content_desc": "无障碍描述（如果能推断）",\n'
            '    "target_bbox_norm": [x1, y1, x2, y2]\n'
            "  }}\n"
            "]\n"
        )

        # 规则
        prompt_parts.append(
            "规则:\n"
            "1. target_bbox_norm 使用 0.0-1.0 归一化坐标，左上角为 [0,0]，右下角为 [1,1]\n"
            "2. 只返回 APP 内容区域的元素，忽略系统状态栏和导航栏\n"
            "3. confidence 取值 0.0-1.0，越高表示越确定该元素可交互\n"
            "4. 优先发现 APP 特有的功能（如健身追踪、食谱收藏），"
            "而非通用社交功能（like/comment/share 已有覆盖）\n"
            "5. 只返回 JSON 数组，不要其他文字\n"
        )

        # 可选: 附加 ui_catalog 信息供交叉参考
        if self._include_ui_catalog:
            ui_catalog = page_artifact.get("ui_catalog", [])
            if ui_catalog:
                catalog_summary = self._format_ui_catalog(ui_catalog)
                prompt_parts.append(
                    "\n以下是 uiautomator dump 中检测到的可点击元素，供交叉参考:\n"
                    "{catalog_summary}\n".format(catalog_summary=catalog_summary)
                )

        return "\n".join(prompt_parts)

    def _format_ui_catalog(self, ui_catalog):
        """
        将 ui_catalog 列表格式化为简洁的文本摘要

        Args:
            ui_catalog: 可点击元素列表

        Returns:
            str: 格式化的文本摘要
        """
        lines = []
        limit = min(len(ui_catalog), self._max_ui_elements)
        for i in range(limit):
            elem = ui_catalog[i]
            parts = []
            text = elem.get("text", "")
            desc = elem.get("desc", "")
            res_id = elem.get("id", "")
            cls = elem.get("class", "")
            bounds = elem.get("bounds", "")
            if text:
                parts.append("text='{}'".format(text))
            if desc:
                parts.append("desc='{}'".format(desc))
            if res_id:
                # 只保留 resource-id 的短名
                short_id = res_id.split("/")[-1] if "/" in res_id else res_id
                parts.append("id={}".format(short_id))
            if cls:
                short_cls = cls.split(".")[-1] if "." in cls else cls
                parts.append("cls={}".format(short_cls))
            if bounds:
                parts.append("bounds={}".format(bounds))
            if parts:
                lines.append("  [{}] {}".format(i + 1, ", ".join(parts)))

        if len(ui_catalog) > limit:
            lines.append("  ... 共 {} 个元素，仅显示前 {}".format(len(ui_catalog), limit))

        return "\n".join(lines)

    def _build_openai_payload(self, prompt, image_b64):
        """
        构建 OpenAI Chat Completions API 的请求载荷（含图片）

        Args:
            prompt: 提示词文本
            image_b64: Base64 编码的截图

        Returns:
            dict: API 请求载荷
        """
        return {
            "model": self._model,
            "messages": [
                {
                    "role": "user",
                    "content": [
                        {"type": "text", "text": prompt},
                        {
                            "type": "image_url",
                            "image_url": {
                                "url": "data:image/png;base64,{b64}".format(b64=image_b64),
                            },
                        },
                    ],
                }
            ],
            "max_tokens": 2000,
            "temperature": 0.1,
            "response_format": {"type": "json_object"},
        }

    # ------------------------------------------------------------------
    # 内部方法: 网络请求
    # ------------------------------------------------------------------

    def _post_json(self, url, headers, payload):
        """
        发送 JSON POST 请求（使用 urllib，支持重试和超时）

        Args:
            url: API 端点 URL
            headers: HTTP 请求头字典
            payload: 请求体字典

        Returns:
            dict|None: 解析后的 JSON 响应，失败时返回 None
        """
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        last_error = None

        for attempt in range(self._max_retries + 1):
            try:
                req = _Request(url, data=body)
                for key, value in headers.items():
                    req.add_header(key, value)

                resp = _urlopen(req, timeout=self._request_timeout_sec)
                resp_body = resp.read().decode("utf-8")
                return json.loads(resp_body)

            except _HTTPError as e:
                resp_body_err = ""
                try:
                    resp_body_err = e.read().decode("utf-8")
                except Exception:
                    pass
                last_error = "HTTP {} - {}".format(e.code, resp_body_err[:500])
                self._logger("WARN", "[VisionDiscovery] API 请求失败 (尝试 {}/{}): {}".format(
                    attempt + 1, self._max_retries + 1, last_error
                ))

            except _URLError as e:
                last_error = "URL 错误: {}".format(str(e.reason))
                self._logger("WARN", "[VisionDiscovery] API 请求失败 (尝试 {}/{}): {}".format(
                    attempt + 1, self._max_retries + 1, last_error
                ))

            except Exception as e:
                last_error = "未知错误: {}".format(str(e))
                self._logger("WARN", "[VisionDiscovery] API 请求失败 (尝试 {}/{}): {}".format(
                    attempt + 1, self._max_retries + 1, last_error
                ))

            # 重试前等待（最后一次不等待）
            if attempt < self._max_retries:
                backoff = self._retry_backoff_sec * (attempt + 1)
                self._logger("INFO", "[VisionDiscovery] 等待 {:.1f} 秒后重试...".format(backoff))
                time.sleep(backoff)

        # 所有重试均失败
        error_msg = "API 请求最终失败 (共 {} 次尝试): {}".format(
            self._max_retries + 1, last_error
        )
        self._errors.append(error_msg)
        self._logger("ERROR", "[VisionDiscovery] {}".format(error_msg))
        return None

    # ------------------------------------------------------------------
    # 内部方法: 响应解析
    # ------------------------------------------------------------------

    def _parse_vision_response(self, raw_response):
        """
        从 OpenAI API 响应中提取功能列表

        处理多种可能的响应格式:
        - 直接 JSON 数组: [...]
        - 带 features 键的对象: {"features": [...]}
        - Markdown 代码块包裹的 JSON

        Args:
            raw_response: API 返回的完整响应字典

        Returns:
            list: 原始功能字典列表

        Raises:
            ValueError: 无法从响应中提取有效的功能列表
        """
        # 提取 content 文本
        try:
            content = raw_response["choices"][0]["message"]["content"]
        except (KeyError, IndexError, TypeError) as e:
            raise ValueError("响应结构异常，无法提取 content: {}".format(str(e)))

        if not content or not content.strip():
            raise ValueError("响应 content 为空")

        content = content.strip()

        # 尝试解析: 去除 Markdown 代码块包裹
        cleaned = self._strip_markdown_code_block(content)

        # 尝试直接解析为 JSON
        try:
            parsed = json.loads(cleaned)
        except (ValueError, TypeError) as e:
            raise ValueError("JSON 解析失败: {} (原始内容前 200 字符: {})".format(
                str(e), cleaned[:200]
            ))

        # 判断解析结果类型
        if isinstance(parsed, list):
            return parsed

        if isinstance(parsed, dict):
            # 尝试常见的包装键
            for key in ("features", "elements", "items", "results", "data"):
                if key in parsed and isinstance(parsed[key], list):
                    return parsed[key]
            # 如果字典本身看起来像单个 feature，包装成列表
            if "feature_key" in parsed or "feature_label" in parsed:
                return [parsed]
            raise ValueError("JSON 对象中未找到功能列表 (可用键: {})".format(
                list(parsed.keys())
            ))

        raise ValueError("不支持的 JSON 类型: {}".format(type(parsed).__name__))

    def _strip_markdown_code_block(self, text):
        """
        去除 Markdown 代码块标记（如果存在）

        Args:
            text: 可能包含 ```json ... ``` 包裹的文本

        Returns:
            str: 去除包裹后的纯 JSON 文本
        """
        # 匹配 ```json ... ``` 或 ``` ... ```
        pattern = r"```(?:json)?\s*\n?(.*?)\n?\s*```"
        match = re.search(pattern, text, re.DOTALL)
        if match:
            return match.group(1).strip()
        return text

    # ------------------------------------------------------------------
    # 内部方法: 功能标准化
    # ------------------------------------------------------------------

    def _normalize_feature(self, raw_feature, page_artifact, index):
        """
        将 API 返回的原始功能字典标准化为 VisionDiscoveredFeature 格式

        Args:
            raw_feature: API 返回的单个功能字典
            page_artifact: 所属页面信息
            index: 功能在列表中的序号

        Returns:
            dict|None: 标准化后的功能字典，置信度不足时返回 None
        """
        if not isinstance(raw_feature, dict):
            return None

        page_key = page_artifact.get("page_key", "unknown")
        page_type = page_artifact.get("page_type", "unknown")

        # 提取基本字段
        feature_key = raw_feature.get("feature_key", "unknown_{}".format(index))
        feature_label = raw_feature.get("feature_label", "")
        action_type = raw_feature.get("action_type", "tap")
        intent_type = raw_feature.get("intent_type", "secondary_action")
        confidence = raw_feature.get("confidence", 0.5)
        target_text = raw_feature.get("target_text", "")
        target_content_desc = raw_feature.get("target_content_desc", "")
        target_bbox_norm = raw_feature.get("target_bbox_norm", [])

        # 类型安全: confidence 必须是数字
        try:
            confidence = float(confidence)
        except (ValueError, TypeError):
            confidence = 0.5

        # 置信度过滤
        if confidence < self._confidence_threshold:
            return None

        # 校验 bbox 格式
        if isinstance(target_bbox_norm, list) and len(target_bbox_norm) == 4:
            try:
                target_bbox_norm = [float(v) for v in target_bbox_norm]
            except (ValueError, TypeError):
                target_bbox_norm = []
        else:
            target_bbox_norm = []

        # 生成标识符
        feature_id = "vision_{page_key}_{feature_key}".format(
            page_key=page_key,
            feature_key=feature_key,
        )
        operation_name = "v_{feature_key}".format(feature_key=feature_key)
        verification_checkpoint = "verify_{feature_key}".format(feature_key=feature_key)

        # 推断恢复策略
        recovery_hint, fallback_op, overlay_risk = self._infer_recovery(
            page_type, action_type
        )

        return {
            # 基本信息
            "feature_id": feature_id,
            "feature_key": feature_key,
            "feature_label": feature_label,
            "operation_name": operation_name,
            "verification_checkpoint": verification_checkpoint,
            "source": "vision_ai",

            # 动作描述
            "action_type": action_type,
            "intent_type": intent_type,
            "confidence": confidence,

            # 目标元素
            "target_text": target_text,
            "target_content_desc": target_content_desc,
            "target_bbox_norm": target_bbox_norm,

            # 来源页面
            "page_key": page_key,
            "page_type": page_type,

            # 分辨率（初始为未解析状态，后续由 resolver 填充）
            "resolution": {
                "matched": False,
                "resource_id": None,
                "xpath": None,
                "strategy": None,
                "value": None,
                "matched_element_bounds": None,
            },

            # 恢复策略
            "recovery": {
                "recovery_hint": recovery_hint,
                "fallback_op": fallback_op,
                "overlay_risk": overlay_risk,
            },
        }

    def _infer_recovery(self, page_type, action_type):
        """
        根据页面类型和动作类型推断恢复策略

        Args:
            page_type: 页面类型
            action_type: 动作类型

        Returns:
            tuple: (recovery_hint, fallback_op, overlay_risk)
        """
        # 默认值
        recovery_hint = "back"
        fallback_op = "press_back"
        overlay_risk = False

        # 按页面类型调整
        if page_type in ("home", "feed"):
            recovery_hint = "nav_tab"
            fallback_op = "nav_home"
        elif page_type == "post_detail":
            recovery_hint = "back"
            fallback_op = "back_to_feed"

        # 按动作类型调整覆盖层风险
        if action_type in ("open", "submit"):
            overlay_risk = True

        return recovery_hint, fallback_op, overlay_risk

    # ------------------------------------------------------------------
    # 内部方法: 成本控制
    # ------------------------------------------------------------------

    def _apply_cost_controls(self, page_artifacts):
        """
        应用成本控制策略过滤页面列表

        策略:
        1. 限制最大页面数（max_pages）
        2. 按截图指纹去重（dedupe_by_artifact_hash）

        Args:
            page_artifacts: 原始页面信息列表

        Returns:
            list: 过滤后的页面信息列表
        """
        filtered = []
        seen_hashes = set()

        for artifact in page_artifacts:
            # 页面数限制
            if len(filtered) >= self._max_pages:
                break

            # 去重
            if self._dedupe_by_hash:
                h = self._hash_artifact(artifact)
                if h in seen_hashes:
                    self._logger("INFO", "[VisionDiscovery] 批量去重: 跳过页面 {}".format(
                        artifact.get("page_key", "unknown")
                    ))
                    continue
                seen_hashes.add(h)

            filtered.append(artifact)

        return filtered

    def _hash_artifact(self, page_artifact):
        """
        计算页面工件的指纹哈希（用于去重）

        哈希基于截图文件大小 + XML 内容前 1000 字符。

        Args:
            page_artifact: 页面信息字典

        Returns:
            str: 十六进制哈希摘要
        """
        hasher = hashlib.md5()

        # 截图文件大小
        screenshot_path = page_artifact.get("screenshot_path", "")
        try:
            file_size = os.path.getsize(screenshot_path)
            hasher.update(str(file_size).encode("utf-8"))
        except (OSError, IOError):
            hasher.update(b"no_file")

        # XML 内容前 1000 字符
        xml_content = page_artifact.get("xml_content", "")
        if xml_content:
            snippet = xml_content[:1000]
            if isinstance(snippet, bytes):
                hasher.update(snippet)
            else:
                hasher.update(snippet.encode("utf-8"))

        return hasher.hexdigest()

    # ------------------------------------------------------------------
    # 内部方法: 文件与编码
    # ------------------------------------------------------------------

    def _load_image_base64(self, screenshot_path):
        """
        将截图文件加载为 Base64 编码字符串

        Args:
            screenshot_path: 截图文件路径

        Returns:
            str|None: Base64 编码字符串，文件不存在或读取失败时返回 None
        """
        if not screenshot_path:
            return None

        if not os.path.isfile(screenshot_path):
            self._logger("WARN", "[VisionDiscovery] 截图文件不存在: {}".format(screenshot_path))
            return None

        try:
            with open(screenshot_path, "rb") as f:
                raw = f.read()
            return base64.b64encode(raw).decode("ascii")
        except (IOError, OSError) as e:
            self._logger("ERROR", "[VisionDiscovery] 读取截图失败: {}".format(str(e)))
            return None

    # ------------------------------------------------------------------
    # 内部方法: 日志
    # ------------------------------------------------------------------

    @staticmethod
    def _default_logger(level, message):
        """
        默认日志输出函数（使用 print）

        Args:
            level: 日志级别字符串
            message: 日志消息
        """
        print("[{}] {}".format(level, message))
