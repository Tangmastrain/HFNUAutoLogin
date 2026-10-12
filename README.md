# 校园网自动登录 · CampusAutoLogin 2.0

一个常驻托盘、无人值守的 Windows 校园网自动登录工具。开机自动醒来 → 检查网络 → 掉线立即重新认证。

> 协议逆向：华为 **ePortal (ACSetting)** Web Portal · HTTP · 密码明文（`enMd5=0`）

![主界面](CampusAutoLogin2.0/shots/main-connected.png)

---

## 功能

| 层级 | 功能 |
|---|---|
| **核心** | 开机自启（任务计划，延迟 30s）· DPAPI 加密记住账号 · 自动登录 · 隐藏式启动 |
| **支撑** | 连通性探测 · 掉线重连 · 休眠唤醒恢复 · 网络事件监听 · 失败重试 · 单实例 · 日志 |
| **体验** | 托盘状态图标 · 右键菜单 · 设置窗口 · 首次运行引导 |
| **增强** | 一键注销 · 自启开关 · 检测间隔可调 · 登录成功通知 |

---

## 2.0 相对 1.2.2 的变化

- 横幅**去掉连接时间，只保留状态**，更清爽
- 「启动时隐藏窗口」**默认改为关闭**，首次运行更直观
- 与 1.x **完全隔离**，可并存：
  - 独立互斥体
  - 独立配置目录 `%APPDATA%\CampusAutoLogin2.0`
  - 独立自启项 `CampusAutoLogin2.0`

---

## 下载

前往 [Releases](../../releases) 下载 `CampusAutoLogin2.0.exe`。

**单文件自包含，双击即用**，无需安装 .NET 运行时。

> 系统要求：Windows 10 1809 (build 17763) 或更高。

---

## 使用

1. 双击运行，首次会弹出设置窗口
2. 填入校园网账号密码（用 DPAPI 加密存到本地，不落明文）
3. 按需开启「开机自启」
4. 之后它就在托盘里待着，掉线自动重连

配置与日志位置：`%APPDATA%\CampusAutoLogin2.0\`

---

## 安全说明

本程序以**密码明文**方式提交到校园网 ePortal 认证接口（服务端要求，`enMd5=0`）。
因此：

- 所有网络请求**仅限校园网内网**（`192.168.1.100`），不经过任何第三方
- 账号密码本地使用 **Windows DPAPI** 加密存储，绑定当前用户
- 程序不含任何遥测、上报、外传逻辑

---

## 从源码构建

```bash
dotnet publish CampusAutoLogin2.0/CampusAutoLogin2.0.csproj \
  -c Release -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o CampusAutoLogin2.0/publish
```

需要 .NET 8 SDK。

---

## 目录结构

```
CampusAutoLogin2.0/
├── Auth/          # 认证协议实现（ePortal ACSetting）
├── Core/          # 协调器、连通性监控、重试策略
├── Platform/      # 自启、托盘图标、热管理、日志
├── Storage/       # DPAPI 凭据存储、配置持久化
├── UI/            # 主窗口、设置窗口、托盘菜单、主题
└── tools/         # 图标生成脚本
```

---

## 免责声明

仅供个人在**已获授权的校园网络**内使用。请遵守所在学校网络使用规定。
