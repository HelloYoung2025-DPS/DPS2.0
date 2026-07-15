# -*- coding: utf-8 -*-
# pyright: reportGeneralTypeIssues=false, reportArgumentType=false, reportAttributeAccessIssue=false, reportIndexIssue=false, reportOptionalSubscript=false, reportCallIssue=false, reportOperatorIssue=false, reportMissingTypeArgument=false
"""
App Onboarder — 主入口程序
DPS v4.5 新平台自动接入工具

用法: python main.py
      python main.py --package com.example.app --key example
      python main.py --package com.example.app --key example --skip-test
      python main.py --package com.example.app --enable-vision
"""

import os
import sys
import argparse

# 强制 UTF-8 输出（Windows 中文环境 cp936 兼容）
try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

# 确保当前目录在 Python path 中
CURRENT_DIR = os.path.dirname(os.path.abspath(__file__))
if CURRENT_DIR not in sys.path:
    sys.path.insert(0, CURRENT_DIR)

try:
    from .adb_controller import ADBController
    from .app_explorer import AppExplorer
    from .config_generator import ConfigGenerator
    from .test_runner import TestRunner
except Exception:
    ADBController = __import__("adb_controller").ADBController
    AppExplorer = __import__("app_explorer").AppExplorer
    ConfigGenerator = __import__("config_generator").ConfigGenerator
    TestRunner = __import__("test_runner").TestRunner


# DPS v4.5 项目根目录
DPS_ROOT = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", ".."
))

EXIT_SUCCESS = 0
EXIT_REQUIRED_TEST_FAILED = 2


def _required_test_failure_report(status, summary):
    """Build an explicit failed report for a required test that did not run."""
    return {
        "total_attempts": 0,
        "final_results": {
            "total": 0,
            "pass_count": 0,
            "phases": {},
            "process_returncode": None,
            "execution_status": status,
            "evidence_complete": False,
            "verification_level": "TEST_EXECUTION_ONLY",
        },
        "fixes_applied": [],
        "success": False,
        "required": True,
        "failure_reasons": [str(status).lower()],
        "summary": summary,
        "verification_level": "TEST_EXECUTION_ONLY",
    }


def print_banner():
    """打印工具横幅"""
    print("")
    print("=" * 56)
    print("  DPS v4.5 App Onboarder — 新平台自动接入工具")
    print("  版本: 2.0  (2026-03)  [Vision AI 支持]")
    print("=" * 56)
    print("")


def print_section(title):
    """打印分节标题"""
    print("")
    print("--- {} ---".format(title))
    print("")


def get_user_input(prompt, default=None):
    """
    获取用户输入，支持默认值

    Args:
        prompt: 提示文本
        default: 默认值（None 则为必填）

    Returns:
        str: 用户输入
    """
    while True:
        if default:
            display = "{} [{}]: ".format(prompt, default)
        else:
            display = "{}: ".format(prompt)

        try:
            value = input(display).strip()
            if not value and default:
                return default
            if not value and default is None:
                print("  [错误] 此项为必填")
                continue
            return value
        except (EOFError, KeyboardInterrupt):
            print("\n\n[中断] 用户取消操作")
            sys.exit(0)


# 全局标志: 是否自动确认所有提示
_auto_confirm = False


def confirm(message, default_yes=True):
    """
    确认提示

    Args:
        message: 确认信息
        default_yes: 默认是否为 Yes

    Returns:
        bool
    """
    if _auto_confirm:
        return True
    suffix = "[Y/n]" if default_yes else "[y/N]"
    try:
        answer = input("{} {} ".format(message, suffix)).strip().lower()
        if not answer:
            return default_yes
        return answer in ("y", "yes")
    except (EOFError, KeyboardInterrupt):
        print("\n\n[中断] 用户取消操作")
        sys.exit(0)


def parse_args():
    """解析命令行参数"""
    parser = argparse.ArgumentParser(
        description="DPS v4.5 App Onboarder — 新平台自动接入工具",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
示例:
  python main.py                                     # 交互模式
  python main.py --package com.example.app --key ex  # 指定包名和 key
  python main.py --package com.example.app --skip-test  # 跳过测试
  python main.py --package com.example.app --enable-vision  # 启用 Vision AI 发现
        """
    )
    parser.add_argument(
        "--package", "-p",
        help="APP 包名 (如 com.babycenter.pregnancytracker)"
    )
    parser.add_argument(
        "--key", "-k",
        help="平台简称 (如 babycenter)，默认从包名推断"
    )
    parser.add_argument(
        "--device", "-d",
        help="ADB 设备 ID (多设备时使用)"
    )
    parser.add_argument(
        "--skip-explore", action="store_true",
        help="跳过探索阶段（仅在 app_map 已存在时使用）"
    )
    parser.add_argument(
        "--skip-test", action="store_true",
        help="跳过 E2E 测试阶段"
    )
    parser.add_argument(
        "--output", "-o",
        help="输出目录 (默认: DPS_v4.5 项目根目录)"
    )
    parser.add_argument(
        "--enable-vision", action="store_true",
        help="启用 Vision AI 功能发现（需要 OPENAI_API_KEY 环境变量）"
    )
    parser.add_argument(
        "--vision-model",
        default="gpt-4o-mini",
        help="Vision AI 模型名称 (默认: gpt-4o-mini)"
    )
    parser.add_argument(
        "--vision-max-pages",
        type=int, default=10,
        help="Vision AI 最大分析页面数 (默认: 10)"
    )
    parser.add_argument(
        "--ai-config",
        help="AIConfig.json 文件路径 (从中读取 API Key，默认: DPS_v4.5/Config/AIConfig.json)"
    )
    parser.add_argument(
        "--yes", "-y", action="store_true",
        help="自动确认所有提示（非交互模式）"
    )
    return parser.parse_args()


def step_check_adb(device_id=None):
    """Step 1: 检查 ADB 连接"""
    print_section("Step 1: 检查 ADB 连接")

    adb = ADBController(device_id=device_id)

    if not adb.is_connected():
        print("[错误] ADB 设备未连接!")
        print("  请确保:")
        print("  1. USB 调试已开启")
        print("  2. 手机已通过 USB/WiFi 连接到电脑")
        print("  3. 在手机上已授权调试")
        print("")
        print("  运行 'adb devices' 检查连接状态")
        sys.exit(1)

    screen_w, screen_h = adb.get_screen_size()
    print("[OK] ADB 设备已连接")
    print("  屏幕分辨率: {}x{}".format(screen_w, screen_h))

    # 获取完整设备信息
    try:
        device_info = adb.get_device_info()
        if device_info.get("model"):
            print("  设备型号: {} ({})".format(device_info["model"], device_info["brand"]))
        if device_info.get("android_version"):
            print("  Android 版本: {} (SDK {})".format(
                device_info["android_version"], device_info["sdk_version"]
            ))
        if device_info.get("dpi"):
            print("  DPI: {}".format(device_info["dpi"]))
    except Exception:
        pass

    return adb


def step_detect_state(adb, package_name):
    """Step 2.5: 检测 APP 当前页面状态"""
    print_section("Step 2.5: 页面状态检测")

    # 检查 APP 是否在前台
    current = adb.get_current_activity()
    if package_name not in current:
        print("[信息] APP 不在前台，正在启动...")
        adb.launch_app(package_name)
        adb.human_delay(8000, 10000)

    try:
        try:
            from .ui_analyzer import UIAnalyzer
        except Exception:
            UIAnalyzer = __import__("ui_analyzer").UIAnalyzer
        xml = adb.dump_ui("state_check")
        analyzer = UIAnalyzer(xml)
        state_info = analyzer.detect_app_state()

        print("[检测] 当前页面状态: {}".format(state_info["state"]))
        print("  置信度: {}".format(state_info["confidence"]))
        print("  页面类型: {}".format(state_info["page_type"]))
        print("  有底部导航: {}".format(state_info["has_bottom_nav"]))
        print("  有 Feed: {}".format(state_info["has_feed"]))
        print("  有 WebView: {}".format(state_info["has_webview"]))
        if state_info["action_buttons_found"]:
            print("  发现按钮: {}".format(", ".join(state_info["action_buttons_found"])))
        if state_info["signals"]:
            print("  判断依据:")
            for sig in state_info["signals"]:
                print("    - {}".format(sig))
        print("")
        return state_info
    except Exception as e:
        print("[警告] 页面状态检测失败: {}".format(str(e)))
        return {"state": "unknown", "confidence": 0.0}


def step_get_info(args):
    """Step 2: 获取 APP 信息"""
    print_section("Step 2: APP 信息")

    if args.package:
        package_name = args.package
        print("  包名: {} (来自命令行)".format(package_name))
    else:
        package_name = get_user_input("APP 包名 (如 com.babycenter.pregnancytracker)")

    if args.key:
        platform_key = args.key
        print("  平台 Key: {} (来自命令行)".format(platform_key))
    else:
        default_key = package_name.split(".")[-1]
        platform_key = get_user_input("平台 Key (如 babycenter)", default=default_key)

    return package_name, platform_key


def step_explore(adb, package_name, platform_key, enable_vision=False, vision_config=None):
    """Step 3: 自动探索 APP"""
    print_section("Step 3: 自动探索 APP")

    print("[信息] 请确保 APP '{}' 已打开到首页".format(package_name))
    print("  如果 APP 未运行，工具将尝试自动启动")
    if enable_vision:
        print("  [Vision AI] 已启用，将在探索过程中分析截图发现 APP 特有功能")
    print("")

    if not confirm("准备好了吗？开始探索"):
        print("[跳过] 用户取消探索")
        return None

    # 检查 APP 是否运行中
    current = adb.get_current_activity()
    if package_name not in current:
        print("[信息] APP 不在前台，正在启动...")
        adb.launch_app(package_name)
        adb.human_delay(8000, 10000)

    # 开始探索
    print("[开始] 启动自动探索引擎...")
    print("")

    explorer = AppExplorer(
        adb, package_name, platform_key,
        enable_vision=enable_vision,
        vision_config=vision_config,
    )
    app_map = explorer.run()

    # 打印探索结果摘要
    print("")
    print_section("探索结果摘要")
    print("  包名: {}".format(app_map["package_name"]))
    print("  平台 Key: {}".format(app_map["platform_key"]))
    print("  底部导航 Tab 数: {}".format(len(app_map["bottom_nav_tabs"])))
    print("  Feed Tab: {}".format(app_map.get("feed_tab_key", "未发现")))
    print("  Feed 类型: {}".format(app_map.get("feed_type", "未知")))
    print("  帖子容器 ID: {}".format(app_map.get("post_container_id", "未发现")))
    print("  帖子详情是 WebView: {}".format(app_map.get("post_detail_is_webview", False)))
    print("  WebView 容器 ID: {}".format(app_map.get("webview_container_id", "无")))
    print("  WebView Accessibility: {}".format(app_map.get("webview_has_accessibility", False)))
    print("  发现的操作按钮: {}".format(", ".join(app_map["action_buttons"].keys()) or "无"))
    print("  帖子元素: {}".format(", ".join(app_map["post_elements"].keys()) or "无"))
    print("  探索的页面数: {}".format(len(app_map["pages"])))

    # Vision AI 统计
    vision_stats = app_map.get("vision_stats", {})
    if vision_stats and vision_stats.get("pages_analyzed", 0) > 0:
        print("")
        print("  --- Vision AI 发现 ---")
        print("  分析页面数: {}".format(vision_stats.get("pages_analyzed", 0)))
        print("  API 调用次数: {}".format(vision_stats.get("calls_made", 0)))
        print("  发现功能数: {}".format(vision_stats.get("features_found", 0)))
        vision_discoveries = app_map.get("vision_discoveries", [])
        matched_count = 0
        for disc in vision_discoveries:
            for feat in disc.get("features", []):
                if feat.get("resolution", {}).get("matched", False):
                    matched_count += 1
        print("  已匹配到 XML 元素: {}".format(matched_count))
        if vision_stats.get("errors"):
            print("  错误数: {}".format(len(vision_stats["errors"])))

    print("")

    # 保存探索日志
    log_path = os.path.join(
        os.path.expanduser("~"),
        "onboarder_{}_log.txt".format(platform_key)
    )
    try:
        with open(log_path, "w", encoding="utf-8") as f:
            f.write("App Onboarder 探索日志 — {}\n".format(platform_key))
            f.write("=" * 50 + "\n\n")
            for entry in explorer.log_entries:
                f.write(entry + "\n")
        print("[保存] 探索日志已保存到: {}".format(log_path))
    except IOError as e:
        print("[警告] 无法保存探索日志: {}".format(str(e)))

    return app_map


def step_generate(app_map, output_dir=None):
    """Step 4: 生成配置文件"""
    print_section("Step 4: 生成配置文件")

    generator = ConfigGenerator(
        app_map=app_map,
        dps_root=DPS_ROOT,
        output_dir=output_dir
    )

    results = generator.generate_all()

    print("[完成] 配置文件生成完成:")
    for name, path in results.items():
        if path:
            print("  {} → {}".format(name, path))
        else:
            print("  {} → [跳过]".format(name))

    return results


def step_test(adb, platform_key, generated_paths, skip=False):
    """Step 5: 运行 E2E 测试"""
    print_section("Step 5: E2E 测试验证")

    if skip:
        print("[跳过] 用户请求跳过测试")
        return _required_test_failure_report(
            "SKIP", "必需 E2E 测试被跳过，不能通过发布门禁"
        )

    test_path = generated_paths.get("e2e_test")
    config_path = os.path.join(DPS_ROOT, "Config", "PlatformsConfig.json")
    ops_path = generated_paths.get("operations")

    if not test_path or not os.path.exists(test_path):
        print("[跳过] E2E 测试脚本不存在")
        return _required_test_failure_report(
            "NOT_RUN", "必需 E2E 测试脚本缺失，不能通过发布门禁"
        )

    if not confirm("是否运行 E2E 测试？"):
        print("[跳过] 用户取消测试")
        return _required_test_failure_report(
            "SKIP", "必需 E2E 测试被取消，不能通过发布门禁"
        )

    print("[开始] 运行 E2E 测试...")
    print("  测试脚本: {}".format(test_path))
    print("  配置文件: {}".format(config_path))
    print("  操作文件: {}".format(ops_path or "无"))
    print("")

    runner = TestRunner(
        adb=adb,
        test_script_path=test_path,
        config_path=config_path,
        operations_path=ops_path or "",
        platform_key=platform_key,
        evidence_class="device_e2e",
        execution_mode="real",
    )

    report = runner.run_and_fix()
    if not isinstance(report, dict):
        report = _required_test_failure_report(
            "INFRA_ERROR", "测试运行器没有返回报告，不能通过发布门禁"
        )

    # 打印测试报告
    print("")
    print_section("测试报告")
    print("  总尝试次数: {}".format(report.get("total_attempts", 0)))
    print("  最终结果: {}/{}".format(
        report.get("final_results", {}).get("pass_count", 0),
        report.get("final_results", {}).get("total", 7)
    ))
    print("  应用修复数: {}".format(len(report.get("fixes_applied", []))))
    print("  成功: {}".format("是" if report.get("success") else "否"))
    print("")
    if report.get("summary"):
        print("  摘要: {}".format(report["summary"]))

    return report


def step_summary(app_map, generated_paths, test_report=None):
    """Step 6: 最终汇总"""
    test_success = (
        isinstance(test_report, dict)
        and test_report.get("success") is True
    )
    print("")
    print("=" * 56)
    if test_success:
        print("  接入验证通过")
    else:
        print("  接入未通过硬门禁")
    print("=" * 56)
    print("")

    platform_key = app_map["platform_key"]

    print("  平台: {} ({})".format(platform_key, app_map["package_name"]))
    if app_map.get("initial_state"):
        print("  初始状态: {} (置信度: {:.0%})".format(
            app_map["initial_state"].get("state", "?"),
            app_map["initial_state"].get("confidence", 0)
        ))
    if app_map.get("visual_checkpoints"):
        print("  视觉检查点数: {}".format(len(app_map["visual_checkpoints"])))

    # Vision AI 汇总
    vision_stats = app_map.get("vision_stats", {})
    if vision_stats and vision_stats.get("features_found", 0) > 0:
        print("  Vision AI 发现功能: {} 个".format(vision_stats["features_found"]))

    print("")
    print("  生成的文件:")
    for name, path in generated_paths.items():
        if path:
            exists = "[存在]" if os.path.exists(path) else "[不存在]"
            print("    {} {} {}".format(exists, name, path))
    print("")

    if isinstance(test_report, dict):
        pass_count = test_report.get("final_results", {}).get("pass_count", 0)
        total = test_report.get("final_results", {}).get("total", 0)
        if test_success:
            status = "通过"
        else:
            status = "失败 ({}/{})".format(pass_count, total)
        print("  E2E 测试: {}".format(status))
        if test_report.get("summary"):
            print("  门禁摘要: {}".format(test_report["summary"]))
    else:
        print("  E2E 测试: 缺少报告（失败）")

    print("")
    print("  下一步操作:")
    print("  1. 检查生成的配置文件是否正确")
    print("  2. 在 PlatformsConfig.json 中确认新平台配置")
    print("  3. 如需调整，手动编辑配置后重新运行测试")
    print("  4. 将 device_platform_mapping 中的设备绑定到新平台")
    print("")


def _load_api_key_from_aiconfig(ai_config_path=None):
    """
    从 AIConfig.json 读取 OpenAI 兼容的 API Key 和 base_url

    优先级: fallback (openai provider) > backup (openai provider) > primary

    Args:
        ai_config_path: AIConfig.json 路径，None 则使用默认路径

    Returns:
        tuple: (api_key, base_url, model_name) 或 (None, None, None)
    """
    import json as _json

    if not ai_config_path:
        # 默认路径: DPS_v4.5/Config/AIConfig.json
        ai_config_path = os.path.join(DPS_ROOT, "Config", "AIConfig.json")

    if not os.path.exists(ai_config_path):
        return None, None, None

    try:
        with open(ai_config_path, "r", encoding="utf-8") as f:
            config = _json.load(f)
    except (ValueError, IOError):
        return None, None, None

    models = config.get("models", {})

    # 按优先级尝试: fallback > backup > primary（选 openai 兼容的 provider）
    for key in ["fallback", "backup", "primary"]:
        model_cfg = models.get(key, {})
        provider = model_cfg.get("provider", "")
        api_key = model_cfg.get("api_key", "")
        base_url = model_cfg.get("base_url", "")

        if api_key and base_url:
            # openai provider 或有 /v1 结尾的 URL 都视为 OpenAI 兼容
            if provider == "openai" or "/v1" in base_url:
                # 构建 chat completions URL
                chat_url = base_url.rstrip("/")
                if not chat_url.endswith("/chat/completions"):
                    chat_url = chat_url + "/chat/completions"
                model_name = model_cfg.get("model", "gpt-4o-mini")
                return api_key, chat_url, model_name

    return None, None, None


def main():
    """主入口；返回值直接作为进程退出码。"""
    print_banner()

    args = parse_args()

    # 非交互模式
    global _auto_confirm
    if args.yes:
        _auto_confirm = True

    # Step 1: ADB 连接检查
    adb = step_check_adb(device_id=args.device)

    # Step 2: 获取 APP 信息
    package_name, platform_key = step_get_info(args)

    # Step 2.5: 页面状态检测
    initial_state = step_detect_state(adb, package_name)

    # Step 3: 自动探索
    if args.skip_explore:
        print_section("Step 3: 跳过探索 (--skip-explore)")
        print("[信息] 需要提供已有的 app_map，当前版本暂不支持加载已有数据")
        print("[退出] 请移除 --skip-explore 参数后重新运行")
        sys.exit(1)
    else:
        # 构建 Vision 配置
        enable_vision = args.enable_vision
        vision_config = None
        if enable_vision:
            # 优先从环境变量读取，其次从 AIConfig.json 读取
            api_key = os.environ.get("OPENAI_API_KEY", "")
            api_url = "https://api.openai.com/v1/chat/completions"
            vision_model = args.vision_model

            if not api_key:
                # 从 AIConfig.json 读取
                ai_config_path = args.ai_config
                loaded_key, loaded_url, loaded_model = _load_api_key_from_aiconfig(ai_config_path)
                if loaded_key:
                    api_key = loaded_key
                    api_url = loaded_url
                    # 用户未显式指定 model 时，使用 AIConfig.json 中的 model
                    if args.vision_model == "gpt-4o-mini" and loaded_model:
                        vision_model = loaded_model
                    src = ai_config_path or os.path.join(DPS_ROOT, "Config", "AIConfig.json")
                    print("[Vision AI] API Key 来自 AIConfig.json: {}".format(src))
                else:
                    print("[警告] --enable-vision 未找到 API Key")
                    print("  请设置 OPENAI_API_KEY 环境变量，或确保 Config/AIConfig.json 存在")
                    sys.exit(1)

            vision_config = {
                "enabled": True,
                "provider": "openai",
                "model": vision_model,
                "api_url": api_url,
                "api_key": api_key,
                "max_pages": args.vision_max_pages,
                "max_calls": args.vision_max_pages + 5,
                "confidence_threshold": 0.6,
                "request_timeout_sec": 30,
                "max_retries": 2,
                "retry_backoff_sec": 2.0,
                "dedupe_by_artifact_hash": True,
                "include_ui_catalog": True,
                "max_ui_elements": 30,
                "max_features_per_page": 15,
                "analyze_page_types": [
                    "home", "feed", "post_detail", "webview", "sub_page",
                ],
                "fail_open": True,
            }
            print("[Vision AI] 已启用: model={}, max_pages={}, api={}".format(
                vision_model, args.vision_max_pages,
                api_url[:50] + "..." if len(api_url) > 50 else api_url
            ))

        app_map = step_explore(
            adb, package_name, platform_key,
            enable_vision=enable_vision,
            vision_config=vision_config,
        )
        if app_map is None:
            print("[退出] 探索被取消")
            sys.exit(0)

    # Step 4: 生成配置
    generated_paths = step_generate(app_map, output_dir=args.output)

    # Step 5: E2E 测试
    test_report = step_test(adb, platform_key, generated_paths, skip=args.skip_test)

    # Step 6: 汇总
    step_summary(app_map, generated_paths, test_report)

    if not isinstance(test_report, dict):
        print("[失败] 必需 E2E 测试没有返回报告")
        return EXIT_REQUIRED_TEST_FAILED
    if test_report.get("success") is not True:
        print("[失败] 必需 E2E 测试未通过")
        return EXIT_REQUIRED_TEST_FAILED
    return EXIT_SUCCESS


if __name__ == "__main__":
    sys.exit(main())
