# AI 开发交接包 · 能不能直接用 AI 做出来？

> 回答：**能——但只把现有文件丢给 AI，会有 3 处硬缺口导致返工。**
> 本文档就是用来填这三个坑的。

---

## 一、直接结论

| 问题 | 答案 |
|---|---|
| 现有文件够不够让 AI 理解需求？ | ✅ 够，需求/架构/协议都很完整 |
| 现有文件够不够让 AI 一次写对？ | ⚠️ **不够**，3 处缺口 |
| AI 能做出能跑的雏形吗？ | ✅ 能，约 80% |
| 剩下的 20% 靠什么补？ | 本文档的「技术栈拍板 + 验收标准 + 提示词」 |
| 还有个前置风险？ | ⚠️ **协议尚未实测**（详见第三节） |

---

## 二、三个硬缺口（这是会返工的根源）

### 缺口 1：技术栈没拍板 ❌

现有文档里 **C# 是推荐、Python 是备选、还提了 Go/Electron**。
AI 读到这种「多选题」，会自己挑一个——**挑错的概率不低**（比如给你生成一个 Electron 项目）。

**必须明确指定一种。** 推荐 → 见第四节。

### 缺口 2：只有代码片段，没有可编译的项目 ❌

架构文档里有 7 段 C# 代码，但都是 **fragment**：

```csharp
public sealed class TrayContext : ApplicationContext   // ← 缺 using、缺命名空间、
{                                                       //    缺 .csproj、缺 ConnMonitor、
    ...                                                 //    缺 Coordinator、缺 SettingsForm
}
```

AI 需要自己**拼装**这些片段 → 常见的翻车方式：
- `.csproj` 里漏 `PackageReference`（`System.Security.Cryptography.ProtectedData`、`Microsoft.Win32.SystemEvents`）→ 编译不过
- 忘了 `<OutputType>WinExe</OutputType>` → 开机黑窗
- 类之间调用关系靠猜 → 编译错误循环

**建议**：直接让 AI 从零生成完整项目，把片段当参考，而不是让它「补全片段」。

### 缺口 3：没有验收标准 ❌

AI 写完不知道自己对不对，你也不知道它写对没有。
**必须在提示词里写死测试用例**（见第六节）。

---

## 三、前置风险：协议尚未实测 ⚠️

**这是最重要的提醒。**

目前协议结论来自两部分：
1. 抓包（`登录信息.txt`）—— 是**实际发生的真实请求**，这部分可信度 100%
2. Portal 源码静态分析（`a41.js`）—— 是**代码里怎么写的**，可信度很高

但**「抓包请求能成功登录」这件事本身，还没有被实际验证过**。
因为当前网络处于在线状态，没法测。

风险场景：
- 学校可能在 Portal 侧有**额外的前端校验**（如 `para` 字段实际需要动态计算）
- 可能有**时间戳/签名**逻辑藏在其他 JS 里（本次只分析了 `a41.js`，`a42.js` 只扫了关键字）
- `session` 参数在某些版本里是必需的

**成本极低的解法**：先跑一次验证脚本。掉线后执行：

```bash
cd C:\Users\25712\WorkBuddy\autologin\protocol
python portal_login.py probe     # 能看到 wlanuserip 有值 = 参数探测通了
python portal_login.py login     # 返回 [PASS] 已成功联网 = 协议完全确认
```

**这两条命令跑通之前，让 AI 动手等于在流沙上盖楼。**

---

## 四、路线选择

| 路线 | 做法 | 适用 |
|---|---|---|
| **A. 先验证再交给 AI**（推荐） | 先跑通上面两条命令 → 再把本文档 + 提示词交给 AI | 最稳，避免返工 |
| **B. 直接交给 AI，边测边改** | 用提示词让 AI 生成项目，自己想办法测 | 图快，接受返工 |
| **C. 自己做 MVP** | 用现成 Python 脚本改成 20 行的常驻版 | 只想自己用，不求美观 |

---

## 五、推荐技术栈（让 AI 照这个来）

```
语言 / 框架 : C# / .NET 8 + WinForms
入口类型    : <OutputType>WinExe</OutputType>
启动结构    : ApplicationContext + NotifyIcon（不创建主 Form）
异步模型    : async/await + CancellationToken
配置格式    : System.Text.Json
必需 NuGet  : System.Security.Cryptography.ProtectedData
              Microsoft.Win32.SystemEvents
发布方式    : dotnet publish -c Release -r win-x64 --self-contained
```

> 选择理由：托盘 / 开机自启 / DPAPI 凭据加密 / 单实例全是 Windows 原生能力，
> 无需第三方库；体积约 15MB；杀毒软件误报率远低于 PyInstaller 打包的 Python exe。

**注意**：本机当前**未安装 .NET SDK**，需先装 .NET 8 SDK（约 200MB）。
如果不想装，就选 Python 路线（`pystray` + `requests`），但要在提示词里明确改成 Python。

---

## 六、可直接复制的提示词

> 把下面整段复制给 AI（ChatGPT / Claude / DeepSeek 均可）。
> 用之前，把 `【】` 里的内容按你的实际情况改掉。

````text
你是一名资深 Windows 桌面开发工程师。请为我实现一个「校园网自动登录」的
常驻托盘程序。下面是完整需求和已确认的技术事实，请严格按此实现。

## 一、目标
Windows 10/11 x64 桌面程序。开机后静默在后台运行，自动检测网络，
未联网时用保存的账号自动完成校园网 Portal 认证。

## 二、技术栈（必须严格遵守，不要更换）
- C# / .NET 8 + WinForms
- csproj 中必须设置 <OutputType>WinExe</OutputType>
- 入口用 ApplicationContext + NotifyIcon，禁止创建主 Form
- 需要 NuGet：System.Security.Cryptography.ProtectedData、
  Microsoft.Win32.SystemEvents
- 配置用 System.Text.Json

## 三、已确认的认证协议（华为 ePortal / ACSetting，密码明文）

登录请求：
  POST http://【Portal地址，如192.168.1.100】:801/eportal/
       ?c=ACSetting&a=Login&protocol=http:&hostname=【Portal地址】&iTermType=1
       &wlanuserip=<动态>&wlanacip=<动态>&wlanacname=<动态>&mac=<动态>&ip=<动态>
       &enAdvert=0&queryACIP=0&loginMethod=1

  请求头：
    Content-Type: application/x-www-form-urlencoded
    Origin:  http://【Portal地址】
    Referer: http://【Portal地址】/
    Cookie:  program=【模板号如201706081423】; vlan=0;
             md5_login2=<用户名>|<密码>;   ← 注意：字段名叫md5，实际是明文拼接，
                                             其中 | 需 URL 编码成 %7C
             ip=<动态IP>

  请求体（x-www-form-urlencoded，共 15 个字段）：
    DDDDD=<用户名>&upass=<密码明文>&R1=0&R2=0&R6=0&para=00
    &0MKKey=<密码明文>&buttonClicked=&redirect_url=&err_flag=
    &username=&password=&user=&cmd=&Login=
    说明：upass 就是密码原文，【不需要】任何 MD5 / RSA / AES 加密。
          0MKKey 是兼容字段，值必须与 upass 相同。

动态参数获取（关键，不能写死）：
  wlanuserip / wlanacip / wlanacname / mac 四个参数每次都不同，必须在每次
  登录前动态获取。方法：向任意外网地址发起一次 HTTP GET（不跟随重定向），
  未认证时 AC 会 302 劫持到 Portal 页，从响应的 Location 头里用正则提取
  这四个参数。建议探测地址按顺序试：
    http://connect.rom.miui.com/generate_204
    http://www.msftconnecttest.com/connecttest.txt
    http://www.baidu.com/
  若探测不到（例如已在线），则 wlanuserip 回退为本机在默认路由上的源 IP，
  mac 回退为本机物理网卡 MAC（格式 AA-BB-CC-DD-EE-FF）。

注销：同 URL，把 a=Login 换成 a=Logout，无请求体。

## 四、响应判定（这里有致命陷阱，务必按我给的规则实现）
Portal 响应页是 GBK 编码，必须先读 Content-Type，按 gbk 解码，
否则中文乱码会导致关键字匹配永远失败。

判定顺序：
  1) 先查失败词表，命中即失败：
     认证失败 / Authentication failure / AC认证失败 /
     REQ_CHALLENGE失败 / 参数签名失败 / 临时账号上线失败 / 网络错误 /
     密码错误 / 账号或密码 / 用户不存在 / 已欠费 / 用户被锁定
  2) 再查成功词表，命中即成功：
     登录成功 / Login successfully / IP 已经在线 / IP already online /
     "result":1
  3) 两者都不命中 → 最终以连通性探测为准

*** 绝对禁止写 if (response.Contains("成功")) ***
   因为页面上同时存在「注销成功」「密码修改成功」这些文案，
   只匹配单个「成功」字会导致：用户一注销就被误判为登录成功。
   必须匹配完整词「登录成功」。

## 五、功能要求

【L1 核心】
1. 开机自启：用 Windows 计划任务实现，不要用注册表 Run 键。
   命令等价于：
     schtasks /Create /TN "CampusAutoLogin" /TR "\"<exe全路径>\"" 
              /SC ONLOGON /DELAY 0000:30 /RL HIGHEST /F
   必须带 /DELAY 0000:30（延迟 30 秒，等网卡拿到 DHCP）。
   设置界面提供开关，关闭时执行 schtasks /Delete。
2. 记住账号密码：用 DPAPI（ProtectedData.Protect/Unprotect，
   DataProtectionScope.CurrentUser）。用户名可明文存入 config.json，
   密码必须单独加密。解密失败要优雅处理（提示重新输入），不要崩溃。
   日志里绝对不许出现密码。
3. 自动登录：见第三节协议。
4. 隐藏式启动：全程无窗口。程序启动后只显示托盘图标。

【L2 支撑】
5. 连通性检测：请求 http://connect.rom.miui.com/generate_204，
   必须【同时校验状态码和响应体】——返回 204 且无 body 才算已联网。
   备用：http://www.msftconnecttest.com/connecttest.txt，body 必须严格
   等于 "Microsoft Connect Test"。
   【绝对不可以只看 HTTP 200】Portal 劫持时会返回 200 + HTML。
6. 定时巡检：默认每 60 秒检测一次，检测间隔可配置。
7. 掉线自动重连：检测到未联网 → 立即走一次完整登录流程。
8. 失败重试：区分两类失败——
   - 网络类失败（超时/连接失败）：指数退避重试 5s→10s→20s→…上限300s
   - 业务类失败（密码错误等）：【停止重试】，托盘提示用户去改密码
   成功后重置退避计时器。
9. 休眠唤醒恢复：订阅 SystemEvents.PowerModeChanged，
   在 PowerModes.Resume 时触发一次检测（注意唤醒后网卡还要几秒才就绪，
   仍要走"探测→失败重试"流程，不要直接发登录请求）。
10. 网络事件监听：订阅 NetworkChange.NetworkAvailabilityChanged，
    网络可用时立即触发检测，把"最多等60秒"变成"秒级响应"。
11. 单实例：用 new Mutex(true, @"Global\CampusAutoLogin", out bool isNew)，
    非首个实例静默退出。
12. 日志：写到 %APPDATA%\CampusAutoLogin\logs\，按大小滚动
    （单文件 1MB，保留 5 个），格式 [时间] 级别 事件，不含密码。

【L3 体验】
13. 托盘图标：悬停提示显示状态，如"校园网自动登录 · 已连接"。
    注意 NotifyIcon.Text 上限 63 字符。
14. 右键菜单：立即登录 / 断开连接 / 设置… / 查看日志 / 退出。
15. 设置窗口：用户名、密码（密码框）、检测间隔、开机延迟、
    开机自启开关、Portal地址（高级）、Portal模板号（高级）。
    按钮：保存 / 立即测试登录 / 取消。
16. 首次运行：自动弹出设置窗口，提示先点「立即测试登录」验证。

## 六、代码质量要求
- 认证逻辑抽成 IAuthProvider 接口 + PortalAuthProvider 实现（策略模式），
  便于以后适配其他学校
- 路径用绝对路径，处理 exe 路径含空格的转义
- 所有网络请求设置超时（探测 3 秒，登录 8 秒）
- 退出时必须 NotifyIcon.Dispose()，否则托盘会残留死图标
- 不要硬编码任何凭据

## 七、交付要求
请给出【可以编译通过的完整项目】：所有 .cs 文件、.csproj、目录结构说明，
以及 dotnet publish 的发布命令。不要只给片段。
````

---

## 七、验收标准（AI 无法自测，必须你手动验）

按顺序验，任何一步不过就回去改：

| # | 测试项 | 操作 | 期望结果 |
|---|---|---|---|
| 1 | 编译 | `dotnet build` | 0 error |
| 2 | 无窗口启动 | 双击 exe | **不出现任何窗口**，托盘出现图标 |
| 3 | 单实例 | 再双击一次 exe | 没有新进程，无任何提示 |
| 4 | 登录功能 | 断网 → 托盘右键「立即登录」 | 托盘提示变为「已连接」，能上网 |
| 5 | 自动巡检 | 静置 2 分钟 | 日志每 60 秒出现一条探测记录 |
| 6 | 掉线重连 | 拔网线 3 秒再插回 | 60 秒内自动恢复联网 |
| 7 | 休眠恢复 | 合盖 → 打开 | 自动重新登录成功 |
| 8 | 密码错误 | 故意改错密码 | 托盘提示「密码错误」，**且不反复重试** |
| 9 | 注销 | 托盘右键「断开连接」 | 托盘变为「未连接」，**不误报登录成功** |
| 10 | 开机自启 | 重启电脑 | 约 30 秒后自动联网，全程无窗口 |
| 11 | 凭据安全 | 打开 `%APPDATA%\CampusAutoLogin\` | 看不到明文密码 |
| 12 | 托盘清理 | 托盘右键「退出」 | 图标立即消失，无残留 |

---

## 八、AI 最容易写错的 10 个点（重点盯这些）

| # | 错误写法 | 后果 | 正确写法 |
|---|---|---|---|
| 1 | `if (resp.Contains("成功"))` | 注销后被误判登录成功 | 匹配完整词 `登录成功` |
| 2 | `resp` 按 UTF-8 解码 | GBK 页面乱码，判定永远失败 | 先读 Content-Type 再按 gbk 解码 |
| 3 | 把 `wlanuserip` 写死 | 换网段/换机器就失效 | 每次从劫持跳转里提取 |
| 4 | 只看 HTTP 200 判断联网 | 永远认为已联网，**永不登录** | 204 + 空 body 才算通 |
| 5 | 用 Console 应用 | 开机自启时**黑窗一闪** | `<OutputType>WinExe</OutputType>` |
| 6 | `Application.Run(new MainForm())` | 弹出一个空白主窗口 | `Application.Run(new TrayContext())` |
| 7 | 密码错还疯狂重试 | 触发学校**账号锁定** | 业务失败立即停止重试 |
| 8 | 明文存密码 / 自己写 MD5 | 账号泄露 | DPAPI；**且这里根本不需要 MD5** |
| 9 | 退出不 Dispose NotifyIcon | 托盘残留**死图标** | 退出时 Dispose |
| 10 | 自启不延迟 | 开机时网络未就绪，必然失败 | `/DELAY 0000:30` |

---

## 九、一句话总结

> **现有文档足以让 AI 理解「做什么」，但不足以让它一次写对「怎么做」。**
> 补上「技术栈拍板 + 完整提示词 + 验收标准」这三点，AI 一次成型的概率能从
> 大约 50% 提到 85% 以上。
>
> 而剩下的 15%，绝大多数都卡在同一件事上：**协议还没实测过。**
> 先花 2 分钟跑一次 `python portal_login.py login`，比让 AI 返工三轮划算得多。
