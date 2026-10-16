# -*- coding: utf-8 -*-
"""生成开机模式的机器域 DPAPI 凭据副本（与应用 CredentialStore.WriteBootCopy 等价）。
用法: python make_boot_credential.py [数据目录]
验证项：用户域解密 → 机器域加密 → 机器域解密 → 与原文一致。
"""
import base64
import ctypes
import ctypes.wintypes as wt
import json
import os
import sys
from ctypes import wintypes

class DATA_BLOB(ctypes.Structure):
    _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]

def _blob(data: bytes) -> DATA_BLOB:
    buf = ctypes.create_string_buffer(data, len(data))
    return DATA_BLOB(len(data), ctypes.cast(buf, ctypes.POINTER(ctypes.c_char)))

def _unblob(blob: DATA_BLOB) -> bytes:
    out = ctypes.string_at(blob.pbData, blob.cbData)
    ctypes.windll.kernel32.LocalFree(blob.pbData)
    return out

def protect(data: bytes, entropy: bytes, local_machine: bool) -> bytes:
    out = DATA_BLOB()
    flags = 0x04 if local_machine else 0  # CRYPTPROTECT_LOCAL_MACHINE
    ok = ctypes.windll.crypt32.CryptProtectData(
        _blob(data), None, _blob(entropy), None, None, flags, ctypes.byref(out))
    if not ok:
        raise OSError("CryptProtectData failed")
    return _unblob(out)

def unprotect(data: bytes, entropy: bytes, local_machine: bool) -> bytes:
    out = DATA_BLOB()
    flags = 0x04 if local_machine else 0
    ok = ctypes.windll.crypt32.CryptUnprotectData(
        _blob(data), None, _blob(entropy), None, None, flags, ctypes.byref(out))
    if not ok:
        raise OSError("CryptUnprotectData failed")
    return _unblob(out)

ENTROPY = b"CampusAutoLogin.v1"

data_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    os.environ["APPDATA"], "CampusAutoLogin2.0")
main_path = os.path.join(data_dir, "credential.dat")
boot_path = os.path.join(data_dir, "credential-boot.dat")

with open(main_path, "r", encoding="utf-8") as f:
    dto = json.load(f)

plain = unprotect(base64.b64decode(dto["Secret"]), ENTROPY, local_machine=False)
enc = protect(plain, ENTROPY, local_machine=True)
# 回验：机器域密文必须能以 LocalMachine（SYSTEM 可用的域）解开且与原文一致
back = unprotect(enc, ENTROPY, local_machine=True)
assert back == plain, "round-trip mismatch"

with open(boot_path, "w", encoding="utf-8") as f:
    json.dump({"Username": dto["Username"], "Secret": base64.b64encode(enc).decode()}, f)

print("OK 用户名=%s, 机器域副本已写入 %s, 往返校验通过" % (dto["Username"], boot_path))
