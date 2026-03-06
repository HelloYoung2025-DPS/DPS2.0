# -*- coding: utf-8 -*-
"""
App Onboarder — 主入口程序
DPS v4.5 新平台自动接入工具

用法: python main.py
      python main.py --package com.example.app --key example
      python main.py --package com.example.app --key example --skip-test
"""

import os
import sys
import time
import argparse

# 确保当前目录在 Python path 中
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from adb_controller import ADBController
from app_explorer import AppExplorer
from config_generator import ConfigGenerator
from test_runner import TestRunner


# DPS v4.5 项目根目录
DPS_ROOT = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", ".."
))


def print_banner():
    """打印工具横幅"""
    print("")
    print("=" * 56)
    print("  DPS v4.5 App Onboarder — 新平台自动接入工具")
    print("  版本: 1.0  (2026-03)")
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


def confirm(message, default_yes=True):
    """
    确认提示

    Args:
        message: 确认信息
        default_yes: 默认是否为 Yes

    Returns:
        bool
    """
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

    return adb


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


def step_explore(adb, package_name, platform_key):
    """Step 3: 自动探索 APP"""
    print_section("Step 3: 自动探索 APP")

    print("[信息] 请确保 APP '{}' 已打开到首页".format(package_name))
    print("  如果 APP 未运行，工具将尝试自动启动")
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

    explorer = AppExplorer(adb, package_name, platform_key)
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
        return None

    test_path = generated_paths.get("e2e_test")
    config_path = os.path.join(DPS_ROOT, "Config", "PlatformsConfig.json")
    ops_path = generated_paths.get("operations")

    if not test_path or not os.path.exists(test_path):
        print("[跳过] E2E 测试脚本不存在")
        return None

    if not confirm("是否运行 E2E 测试？"):
        print("[跳过] 用户取消测试")
        return None

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
        platform_key=platform_key
    )

    report = runner.run_and_fix()

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
    print("")
    print("=" * 56)
    print("  接入完成！")
    print("=" * 56)
    print("")

    platform_key = app_map["platform_key"]

    print("  平台: {} ({})".format(platform_key, app_map["package_name"]))
    print("")
    print("  生成的文件:")
    for name, path in generated_paths.items():
        if path:
            exists = "[存在]" if os.path.exists(path) else "[不存在]"
            print("    {} {} {}".format(exists, name, path))
    print("")

    if test_report:
        pass_count = test_report.get("final_results", {}).get("pass_count", 0)
        total = test_report.get("final_results", {}).get("total", 7)
        status = "通过" if pass_count == total else "部分通过 ({}/{})".format(pass_count, total)
        print("  E2E 测试: {}".format(status))
    else:
        print("  E2E 测试: 未运行")

    print("")
    print("  下一步操作:")
    print("  1. 检查生成的配置文件是否正确")
    print("  2. 在 PlatformsConfig.json 中确认新平台配置")
    print("  3. 如需调整，手动编辑配置后重新运行测试")
    print("  4. 将 device_platform_mapping 中的设备绑定到新平台")
    print("")


def main():
    """主入口"""
    print_banner()

    args = parse_args()

    # Step 1: ADB 连接检查
    adb = step_check_adb(device_id=args.device)

    # Step 2: 获取 APP 信息
    package_name, platform_key = step_get_info(args)

    # Step 3: 自动探索
    if args.skip_explore:
        print_section("Step 3: 跳过探索 (--skip-explore)")
        print("[信息] 需要提供已有的 app_map，当前版本暂不支持加载已有数据")
        print("[退出] 请移除 --skip-explore 参数后重新运行")
        sys.exit(1)
    else:
        app_map = step_explore(adb, package_name, platform_key)
        if app_map is None:
            print("[退出] 探索被取消")
            sys.exit(0)

    # Step 4: 生成配置
    generated_paths = step_generate(app_map, output_dir=args.output)

    # Step 5: E2E 测试
    test_report = step_test(adb, platform_key, generated_paths, skip=args.skip_test)

    # Step 6: 汇总
    step_summary(app_map, generated_paths, test_report)


if __name__ == "__main__":
    main()
