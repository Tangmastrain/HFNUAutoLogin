#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
华为 ePortal (ACSetting) 校园网自动登录 —— 协议验证脚本（S1 阶段交付物）

零第三方依赖，仅使用 Python 标准库。
协议来源：2026-10-12 抓包分析（登录信息.txt）

用法：
    python portal_login.py probe                 # 只探测 Portal 参数，不登录（调试用）
    python portal_login.py check                 # 检测当前是否已联网
    python portal_login.py login                 # 登录（账号密码取 config.json）
    python portal_login.py login -u 学号 -p 密码 # 临时指定账号登录
    python portal_login.py logout                # 退出登录
    python portal_login.py watch                 # 常驻守护：定时检测，掉线自动重登

作者备注：本脚本仅用于个人学习与网络连通性验证，请遵守所在校园网的使用规定。
"""

import argparse
import json
import os
import re
import socket
import sys
import time
import uuid
from urllib import error, parse, request

# ---------------------------------------------------------------- 常量

UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/155.0.0.0 Safari/537.36 Edg/155.0.0.0")

HERE = os.path.dirname(os.path.abspath(__file__))
CONFIG_PATH = os.path.join(HERE, "config.json")

# 认证接口固定落在 801 端口
EPORTAL_PORT = 801

# 用于触发 ACL 劫持跳转的探测地址（目的是拿到 Portal 动态参数）
PROBE_URLS = (
    "http://connect.rom.miui.com/generate_204",
    "http://www.msftconnecttest.com/connecttest.txt",
    "http://www.baidu.com/",
)

# 连通性判定地址（注意：必须校验响应体内容，Portal 劫持时会返回 200 + HTML）
ONLINE_URL_204 = "http://connect.rom.miui.com/generate_204"
ONLINE_URL_TXT = "http://www.msftconnecttest.com/connecttest.txt"
ONLINE_TXT_EXPECT = "Microsoft Connect Test"

# 响应关键字判定（从 Portal 源码 a41.js 中提取的真实文案）
#
# !! 致命陷阱 !!
# Portal 页面里同时存在「注销成功」「密码修改成功」「修改密码失败」这些文案，
# 它们含有「成功」/「失败」但【不是登录结果】。
# 若判定写成 if "成功" in text —— 用户一注销就会被误判成「登录成功」。
# 所以必须匹配【完整词】，不能匹配单个字。
SUCCESS_KEYS = (
    "登录成功", "Login successfully", "Login succeeded", "login success",
    "IP 已经在线", "IP already online",          # 已在线，等同成功
    '"result":1', "'result':1", '"result":"1"', '"result": "1"',
)
# 命中即失败。业务类失败不应重试，否则可能触发账号安全锁定。
FAIL_KEYS = (
    "认证失败", "Authentication failure",
    "AC认证失败", "AC authentication failure",
    "REQ_CHALLENGE失败", "REQ_CHALLENGE failure",
    "参数签名失败", "临时账号上线失败", "网络错误",
    "密码错误", "账号或密码", "用户不存在", "已欠费", "用户被锁定", "Login failed",
)

DEFAULT_CONFIG = {
    "portal_host": "192.168.1.100",
    "portal_program": "201706081423",
    "username": "",
    "password": "",
    "wlanacname": "me60",
    "wlanacip": "",
    "mac": "",
    "check_interval_sec": 60,
    "retry_max": 5,
}


# ---------------------------------------------------------------- HTTP 基础


class _NoRedirect(request.HTTPRedirectHandler):
    """禁止自动跟随 302，用于抓取 AC 跳转 URL 里的 Portal 参数。"""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


_noredirect_opener = request.build_opener(_NoRedirect)
_plain_opener = request.build_opener()


def http(url, data=None, headers=None, follow=True, timeout=5):
    """返回 (status, headers_dict, body_bytes)。status == 0 表示连接层失败。"""
    req = request.Request(url, data=data, headers=headers or {})
    opener = _plain_opener if follow else _noredirect_opener
    try:
        resp = opener.open(req, timeout=timeout)
        return resp.status, dict(resp.headers), resp.read()
    except error.HTTPError as e:
        return e.code, dict(e.headers), e.read()
    except Exception:
        return 0, {}, b""


def hget(headers, name):
    """大小写不敏感的 header 取值。"""
    for k, v in headers.items():
        if k.lower() == name.lower():
            return v
    return ""


def decode_body(headers, body):
    """
    Portal 响应页是 GBK / GB2312 编码（`Content-Type: text/html; charset=gbk`）。
    若一律按 UTF-8 解码，中文文案会变成乱码，导致关键字匹配【永远失败】。

    这是实测踩到的坑：必须先看 Content-Type，再按 gbk 解码。
    """
    ctype = hget(headers, "Content-Type").lower()
    if "gbk" in ctype or "gb2312" in ctype or "gb18030" in ctype:
        return body.decode("gbk", "ignore")
    if "utf-8" in ctype or "utf8" in ctype:
        return body.decode("utf-8", "ignore")
    for enc in ("utf-8", "gbk"):
        try:
            return body.decode(enc)
        except UnicodeDecodeError:
            continue
    return body.decode("utf-8", "ignore")


def get_local_ip(host="192.168.1.100"):
    """取本机在通往 Portal 那条路由上使用的源 IP。"""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect((host, 80))
        return s.getsockname()[0]
    except Exception:
        return "0.0.0.0"
    finally:
        s.close()


def get_local_mac():
    """取本机物理网卡 MAC，格式化为 AA-BB-CC-DD-EE-FF。"""
    node = uuid.getnode()
    return "-".join(f"{(node >> i) & 0xFF:02X}" for i in (40, 32, 24, 16, 8, 0))


def now():
    return time.strftime("%H:%M:%S")


# ---------------------------------------------------------------- 参数探测

PARAM_KEYS = ("wlanuserip", "wlanacip", "wlanacname", "mac", "session", "ip")


def extract_params(text):
    """从 URL 或 HTML 文本里正则提取 Portal 动态参数。"""
    found = {}
    for key in PARAM_KEYS:
        m = re.search(key + r"""=([^&"'<>\s;]+)""", text)
        if m:
            found[key] = parse.unquote(m.group(1))
    return found


def discover(host, verbose=False):
    """
    通过一次被劫持的 http 请求，从 AC 的重定向里提取
    wlanuserip / wlanacip / wlanacname / mac。

    原理：未认证时访问任意外网，AC 会把请求劫持并 302 到
    http://<portal_host>/3.htm?wlanuserip=...&wlanacname=...&wlanacip=...&mac=...&session=
    """
    for probe in PROBE_URLS:
        status, headers, body = http(probe, follow=False, timeout=4)
        loc = hget(headers, "Location")
        if verbose:
            print(f"      [probe] {probe}\n              -> HTTP {status}  Location: {loc[:100]}")
        if loc:
            p = extract_params(loc)
            if p.get("wlanuserip"):
                return p
        if status == 200:
            p = extract_params(decode_body(headers, body))
            if p.get("wlanuserip"):
                return p
    return {}


# ---------------------------------------------------------------- 登录 / 登出


def build_login_request(username, password, params, cfg):
    """按抓包结果构造登录请求。"""
    host = cfg["portal_host"]
    ip = params.get("wlanuserip") or get_local_ip(host)
    acip = params.get("wlanacip") or cfg.get("wlanacip") or ""
    acname = params.get("wlanacname") or cfg.get("wlanacname") or "me60"
    # 抓包中 mac 为 00-00-00-00-00-00，优先用探测值；探测不到才回退本机 MAC
    mac = cfg.get("mac") or params.get("mac") or get_local_mac()

    query = "&".join([
        "c=ACSetting", "a=Login", "protocol=http:",
        "hostname=" + host, "iTermType=1",
        "wlanuserip=" + ip,
        "wlanacip=" + acip,
        "wlanacname=" + acname,
        "mac=" + mac,
        "ip=" + ip,
        "enAdvert=0", "queryACIP=0", "loginMethod=1",
    ])
    url = f"http://{host}:{EPORTAL_PORT}/eportal/?{query}"

    form = [
        ("DDDDD", username),      # 用户名（学号）
        ("upass", password),      # 密码 —— 明文，无需加密
        ("R1", "0"), ("R2", "0"), ("R6", "0"),
        ("para", "00"),
        ("0MKKey", password),     # 老版本兼容字段，值与 upass 相同
        ("buttonClicked", ""),
        ("redirect_url", ""),
        ("err_flag", ""),
        ("username", ""), ("password", ""), ("user", ""), ("cmd", ""),
        ("Login", ""),
    ]
    data = parse.urlencode(form).encode("utf-8")

    cookie = "; ".join([
        "program=" + str(cfg.get("portal_program", "201706081423")),
        "vlan=0",
        "md5_login2=" + parse.quote(f"{username}|{password}", safe=""),
        "ip=" + ip,
    ])

    headers = {
        "Content-Type": "application/x-www-form-urlencoded",
        "Origin": f"http://{host}",
        "Referer": f"http://{host}/",
        "User-Agent": UA,
        "Cookie": cookie,
    }
    info = {"ip": ip, "acip": acip, "acname": acname, "mac": mac}
    return url, data, headers, info


def judge_response(text):
    """返回 (True/False/None, 命中的关键字)。None 表示无法从响应判断。"""
    flat = re.sub(r"\s+", "", text)
    for k in SUCCESS_KEYS:
        if k.replace(" ", "") in flat:
            return True, k
    for k in FAIL_KEYS:
        if k in text:
            return False, k
    return None, ""


def check_online(timeout=3):
    """
    唯一可靠的联网判定：请求 captive-portal 探测地址并校验内容。
    只看 HTTP 状态码是错的 —— 被 Portal 劫持时会返回 200 + 一段 HTML。
    """
    status, _, _ = http(ONLINE_URL_204, timeout=timeout)
    if status == 204:
        return True
    status, _, body = http(ONLINE_URL_TXT, timeout=timeout)
    if status == 200:
        if body.decode("utf-8", "ignore").strip() == ONLINE_TXT_EXPECT:
            return True
    return False


def do_login(cfg, username, password):
    print("[1/4] 探测 Portal 动态参数 ...")
    params = discover(cfg["portal_host"], verbose=cfg.get("_verbose", False))
    if params:
        print(f"      -> wlanuserip={params.get('wlanuserip')}  "
              f"wlanacip={params.get('wlanacip')}  wlanacname={params.get('wlanacname')}")
    else:
        print("      -> 未探测到（可能已联网，或 AC 不劫持探测地址）；改用本机默认值")

    print("[2/4] 提交认证请求 ...")
    url, data, headers, info = build_login_request(username, password, params, cfg)
    print(f"      POST {url}")
    status, resp_headers, body = http(url, data=data, headers=headers, follow=True, timeout=8)
    text = decode_body(resp_headers, body)
    print(f"      HTTP {status}，响应体 {len(text)} 字节")

    ok, kw = judge_response(text)
    if ok is True:
        print(f"      [+] 响应命中成功关键字：{kw}")
    elif ok is False:
        print(f"      [-] 响应命中失败关键字：{kw}")
        print(f"      响应片段：{text[:300]}")
    else:
        print(f"      [~] 响应无明确标志（各版本格式不同，以连通性为准）")
        print(f"      响应片段：{text[:300]}")

    print("[3/4] 等待 1.5 秒让 AC 放开策略 ...")
    time.sleep(1.5)

    print("[4/4] 验证外网连通性 ...")
    online = check_online()
    print(f"      {'[PASS] 已成功联网' if online else '[FAIL] 仍未联网'}")
    return online


def do_logout(cfg):
    host = cfg["portal_host"]
    params = discover(host)
    ip = params.get("wlanuserip") or get_local_ip(host)
    query = "&".join([
        "c=ACSetting", "a=Logout", "protocol=http:",
        "hostname=" + host,
        "wlanuserip=" + ip,
        "wlanacip=" + (params.get("wlanacip") or ""),
        "wlanacname=" + (params.get("wlanacname") or cfg.get("wlanacname", "me60")),
        "ip=" + ip,
    ])
    url = f"http://{host}:{EPORTAL_PORT}/eportal/?{query}"
    headers = {"Referer": f"http://{host}/", "User-Agent": UA}
    status, resp_headers, body = http(url, headers=headers, timeout=8)
    print(f"  HTTP {status}")
    print(f"  响应片段：{decode_body(resp_headers, body)[:200]}")
    time.sleep(1.0)
    print(f"  当前连通性：{'仍在线' if check_online() else '已离线'}")
    return True


# ---------------------------------------------------------------- 守护模式


def do_watch(cfg, username, password):
    interval = int(cfg.get("check_interval_sec", 60))
    backoff = 5
    print(f"[watch] 进入守护模式，检测间隔 {interval}s，Ctrl+C 退出")
    try:
        while True:
            if check_online():
                print(f"[{now()}] 网络正常，{interval}s 后复查")
                backoff = 5
                time.sleep(interval)
                continue

            print(f"[{now()}] 检测到未联网，开始自动登录 ...")
            if do_login(cfg, username, password):
                backoff = 5
                time.sleep(interval)
            else:
                print(f"[{now()}] 登录失败，{backoff}s 后重试（指数退避，上限 300s）")
                time.sleep(backoff)
                backoff = min(backoff * 2, 300)
    except KeyboardInterrupt:
        print("\n[watch] 已退出")
    return True


# ---------------------------------------------------------------- 入口


def load_config():
    if os.path.exists(CONFIG_PATH):
        try:
            with open(CONFIG_PATH, "r", encoding="utf-8") as f:
                cfg = json.load(f)
            merged = dict(DEFAULT_CONFIG)
            merged.update(cfg)
            return merged
        except Exception as e:
            print(f"[!] 读取 config.json 失败：{e}，使用默认配置")
    return dict(DEFAULT_CONFIG)


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    p = argparse.ArgumentParser(
        description="华为 ePortal (ACSetting) 校园网自动登录 —— 协议验证脚本")
    p.add_argument("action", nargs="?", default="login",
                   choices=["probe", "check", "login", "logout", "watch"],
                   help="probe=只探测参数 / check=检测联网 / login=登录 / logout=退出 / watch=守护")
    p.add_argument("-u", "--user", help="用户名（学号）")
    p.add_argument("-p", "--passwd", dest="passwd", help="密码")
    p.add_argument("-v", "--verbose", action="store_true", help="打印探测细节")
    args = p.parse_args()

    cfg = load_config()
    cfg["_verbose"] = args.verbose
    username = args.user or cfg["username"]
    password = args.passwd or cfg["password"]

    print("=" * 62)
    print(" 华为 ePortal (ACSetting) 校园网自动登录 · 协议验证")
    print(f" Portal: http://{cfg['portal_host']}:{EPORTAL_PORT}/eportal/")
    print("=" * 62)

    if args.action == "probe":
        print("[probe] 探测 Portal 动态参数 ...")
        params = discover(cfg["portal_host"], verbose=True)
        print(f"[probe] 结果：{json.dumps(params, ensure_ascii=False, indent=2) if params else '未探测到参数'}")
        print(f"[probe] 本机 IP  : {get_local_ip(cfg['portal_host'])}")
        print(f"[probe] 本机 MAC : {get_local_mac()}")

    elif args.action == "check":
        print(f"[check] 当前是否联网：{'已联网' if check_online() else '未联网'}")

    elif args.action == "login":
        print(f"[login] 账号 {username}")
        do_login(cfg, username, password)

    elif args.action == "logout":
        do_logout(cfg)

    elif args.action == "watch":
        do_watch(cfg, username, password)


if __name__ == "__main__":
    main()
