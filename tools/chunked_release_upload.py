#!/usr/bin/env python3
"""
GitHub Release 大文件分片上传。
背景：本地到 uploads.github.com 上行约 70KB/s，单次 POST 传 78MB 会中途被掐断，
      服务端留下 digest=null 的半成品。改为 Content-Range 分片续传，断片可重试。

⚠️ 已失效（2026-10-16 实测，勿再使用）：
   GitHub 已不再接受 Content-Range 续传 —— 第 1 片创建资产成功，第 2 片起返回
   400 "Multipart form data required"。官方文档明确：上传 release 资产只支持
   「单次 POST 原始字节」，没有断点续传。大文件请用稳定网络单次
   `gh release upload <tag> <file> --clobber`（本机方案见桌面
   HFNUAutoLogin-v2.0.4-upload / upload.ps1）。

用法: python chunked_release_upload.py <file> <release_id> <repo> <asset_name>
"""
import json
import os
import ssl
import subprocess
import sys
import time
import urllib.request
import urllib.error
import urllib.parse

CHUNK = 2 * 1024 * 1024          # 2MB/片（网络不稳，片小重传成本低）
MAX_RETRY = 12                   # 单片最大重试
TIMEOUT = 180                    # 单片超时(秒)
BACKOFF = 3                      # 退避基数


def get_token() -> str:
    out = subprocess.run(["gh", "auth", "token"], capture_output=True, text=True, check=True)
    return out.stdout.strip()


def api(path: str, token: str, method: str = "GET", body=None, ctype=None):
    req = urllib.request.Request(
        f"https://api.github.com{path}",
        method=method,
        data=body,
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "User-Agent": "chunked-uploader",
            **({"Content-Type": ctype} if ctype else {}),
        },
    )
    ctx = ssl.create_default_context()
    with urllib.request.urlopen(req, timeout=60, context=ctx) as r:
        raw = r.read()
        return json.loads(raw) if raw else {}


def upload_chunk(upload_url: str, token: str, data: bytes, start: int, total: int, name: str, asset_id: int | None):
    """上传单片。
    start=0  -> 创建资产: POST {upload_url}?name=xxx
    start>0  -> 续传:     POST https://uploads.github.com/repos/{repo}/releases/{rel}/assets/{asset_id}
                          header Content-Range: bytes {start}-{end}/*
    """
    if start == 0:
        url = f"{upload_url}?name={urllib.parse.quote(name)}"
        headers = {"Content-Type": "application/octet-stream"}
    else:
        # 续传地址 = upload_url + "/" + asset_id
        url = f"{upload_url}/{asset_id}"
        headers = {
            "Content-Type": "application/octet-stream",
            "Content-Range": f"bytes {start}-{start + len(data) - 1}/*",
        }
    req = urllib.request.Request(url, method="POST", data=data,
                                 headers={"Authorization": f"Bearer {token}",
                                          "User-Agent": "chunked-uploader", **headers})
    ctx = ssl.create_default_context()
    with urllib.request.urlopen(req, timeout=TIMEOUT, context=ctx) as r:
        return json.loads(r.read())


def main():
    path, release_id, repo, asset_name = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
    total = os.path.getsize(path)
    token = get_token()

    # 查询 release 的 upload_url
    rel = api(f"/repos/{repo}/releases/{release_id}", token)
    upload_url = rel["upload_url"].split("{")[0]
    print(f"文件: {path}  {total} bytes  ({total/1048576:.1f} MB)")
    print(f"上传地址: {upload_url}")
    print(f"分片: {CHUNK/1048576:.0f} MB  共 {(total + CHUNK - 1)//CHUNK} 片")

    t0 = time.time()
    offset = 0
    asset = None
    asset_id = None

    # ---- 断点续传：查找同名半成品资产，从已收字节数接着传 ----
    try:
        existing = api(f"/repos/{repo}/releases/{release_id}/assets", token)
        same = [a for a in existing if a.get("name") == asset_name and a.get("state") == "starter"]
        if same:
            asset_id = same[0]["id"]
            offset = same[0]["size"]
            print(f"发现半成品资产 id={asset_id}，已收 {offset} 字节，从该处续传")
        elif any(a.get("name") == asset_name and a.get("state") == "uploaded" for a in existing):
            print(f"资产 {asset_name} 已完整存在（uploaded），删除后重传")
            for a in existing:
                if a.get("name") == asset_name:
                    api(f"/repos/{repo}/releases/assets/{a['id']}", token, method="DELETE")
    except Exception as e:
        print(f"检查已有资产失败（忽略，从头传）: {e}")

    with open(path, "rb") as f:
        idx = 0
        while offset < total:
            f.seek(offset)
            data = f.read(CHUNK)
            if not data:
                break
            idx += 1
            nchunks = (total + CHUNK - 1) // CHUNK
            for attempt in range(1, MAX_RETRY + 1):
                try:
                    ts = time.time()
                    asset = upload_chunk(upload_url, token, data, offset, total, asset_name, asset_id)
                    dur = time.time() - ts
                    spd = len(data) / dur / 1024 if dur > 0 else 0
                    if asset_id is None:
                        asset_id = asset.get("id")
                        print(f"  资产 id = {asset_id}")
                    print(f"  片 {idx}/{nchunks}  {offset}-{offset+len(data)-1}  "
                          f"{dur:.0f}s  {spd:.0f}KB/s  state={asset.get('state')}", flush=True)
                    break
                except urllib.error.HTTPError as e:
                    body = e.read().decode("utf-8", "ignore")[:200]
                    wait = min(BACKOFF * attempt, 15)
                    print(f"  片 {idx} 第 {attempt} 次失败: HTTP {e.code} {body}  -> {wait}s 后重试", flush=True)
                    if attempt == MAX_RETRY:
                        raise SystemExit(f"片 {idx} 重试耗尽，已传 {offset} 字节，可重跑续传")
                    time.sleep(wait)
                except Exception as e:
                    wait = min(BACKOFF * attempt, 15)
                    print(f"  片 {idx} 第 {attempt} 次失败: {type(e).__name__} {str(e)[:120]}  -> {wait}s 后重试", flush=True)
                    if attempt == MAX_RETRY:
                        raise SystemExit(f"片 {idx} 重试耗尽，已传 {offset} 字节，可重跑续传")
                    time.sleep(wait)
            offset += len(data)

    print(f"\n完成，总耗时 {time.time()-t0:.0f}s")
    print(json.dumps({k: asset.get(k) for k in ("id", "name", "size", "state", "digest", "browser_download_url")},
                     ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
