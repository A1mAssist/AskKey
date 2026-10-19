# AskKey Windows 1.0.0 发布说明

WinUI 是唯一的界面，WPF 项目已删除。本文件记录发布流程、这次改动的根因，以及正式发布前仍需人工确认的事项。

## 一句话现状

版本号 `1.0.0`，用已安装好的 Velopack（`vpk`）打包，产物在 `windows\publish\release`。构建与打包已在本机跑通；MSI 装进 Program Files 只做了静态校验，真实安装需要你在本机点一次 UAC。

## 怎么发一版

```powershell
# 1. 改版本号
#    windows\AskKey.Integrations\AskKeyVersion.cs 里的 Current = "1.0.1"

# 2. 构建 + 测试 + 打包（会先跑全量测试）
.\windows\publish-windows.ps1

# 3. release 目录里的所有文件一起上传
```

`publish-windows.ps1` 做的事：读版本号 → `dotnet test` 全量 → `dotnet build` 自包含 win-x64 → 校验 `AskKey.exe` / `Helpers\askkey.exe` / `App.xbf` / `Views` 都在 → `vpk pack`。

**必须用 `dotnet build` 的输出，不要用 `dotnet publish`。** publish 会丢掉所有 `*.xbf` 和 `Views\`，打出来的包启动即崩（XAML parsing failed）。脚本里已经踩过一次，注释写在那儿了。

## 产物清单

| 文件 | 用途 |
|---|---|
| `AskKey-win.msi` | 全机器安装，进 Program Files，需 UAC |
| `AskKey-win-Setup.exe` | 一键安装，当前用户，无需 UAC |
| `AskKey-win-Portable.zip` | 免安装便携版 |
| `AskKey-1.0.0-full.nupkg` | 更新包 |
| `assets.win.json` / `releases.win.json` / `RELEASES` | 更新源元数据 |

后三个 json/RELEASES **不能手改**，必须和上面的文件一起上传。

## 安装位置（这次的重点）

Velopack 的两个安装器装的位置不一样，这是 Velopack 的行为，不是我们能改的：

| 安装器 | 范围 | 位置 |
|---|---|---|
| `AskKey-win.msi`（`--instLocation PerMachine`） | 全机器 | `C:\Program Files\Ask Key\current\` |
| `AskKey-win-Setup.exe` | 当前用户 | `%LOCALAPPDATA%\AskKey\current\` |

**注意 `current\` 这一层。** Velopack 把真正的程序解到 `current\` 里，外层 `AskKey.exe` 只是一个 628KB 的启动 stub。这不是我们能去掉的。

一键 Setup.exe **永远是装 LocalAppData 的**，打包时没有任何参数能改。只有 MSI 能进 Program Files，所以「装到 Program Files」这件事是靠 `--msi --instLocation PerMachine` 实现的。

## 踩过的坑：装完就认不出来了

这次真正卡住的地方不是打包，是装完之后 App 认不出自己。

`OfficialInstallTopology` 原本把安装路径写死成 `%LOCALAPPDATA%\AskKey`，而 Velopack 装出来是 `%LOCALAPPDATA%\AskKey\current\`。差一层目录，结果是：

- `askkey.exe open` 被判成 `RelocatedOrRenamed` 直接拒绝
- 任何 Agent 通过 MCP 拉起宿主都会失败
- 而且这个**对 Setup.exe 和 MSI 都成立**，不只 Program Files

也就是说不改这里，装出来的包每个用户都会遇到「Agent 连不上」。已修：`Decide()` 现在接受两个官方根目录，并且根目录和 `current\` 都认。校验过真实路径的判定结果：

```
Accepted                 C:\Users\...\AppData\Local\AskKey
Accepted                 C:\Users\...\AppData\Local\AskKey\current
Accepted                 C:\Program Files\Ask Key
Accepted                 C:\Program Files\Ask Key\current
RelocatedOrRenamed       <其他任意路径>   ← 这是对的，篡改过的副本必须拒绝
```

## 正式发布前还要做什么

按需要你确认的程度排序：

1. **真机装一次 MSI。** 我这边是非管理员，`msiexec /i ... /qn` 会弹 UAC，不适合替你点。装完确认 `C:\Program Files\Ask Key\current\AskKey.exe` 能启动、托盘图标正常、`askkey.exe health` 通。这一步只有你能做。
2. **Windows Hello 真机验证。** 解锁提示、5/15 分钟免解锁的设置项，会话锁定/解锁时的隐私遮蔽。单元测试证明不了这些。
3. **买证书后重新签一次。** 现在整包未签名，SmartScreen 会拦。签完把 thumbprint 传给 `-CertificateThumbprint`，host 和 helper 必须同一发布者，这是已有的硬校验。
4. **更新检查 UI 还没做。** `Program.cs` 里只有 `VelopackApp.Build().Run()`，没有 `SetAutoApplyOnStartup(false)`，App 里也没有「检查更新」入口。现在等于只能手动重装，等于没有自动更新。要接的话抄 OpenSynapse 的 `MainWindow.Updates.cs`。
5. **`vpk` 版本落后。** 本机是 1.2.0，官方最新 1.2.161。不影响这次发布，但建议顺手 `dotnet tool update -g vpk`。

## 已知未修（不是这次引入的）

- `AskKey.Integrations.Tests` 6 个失败、`AskKey.Helper.Tests` 1 个失败，都是改动前就存在的（MCP fixture 和协议版本相关）。已在 stash 掉本次改动后复跑确认。
- `Setup.exe` 在 `%LOCALAPPDATA%\AskKey` 只有零散配置文件、没有 `current\` 时会报「拒绝访问」并失败退出。不丢数据，重装或先卸载即可，属于 Velopack 自身行为。
