<p align="center">
  <img src="assets/icon.png" width="96" alt="Ask Key 图标">
</p>

<h1 align="center">Ask Key for Windows</h1>

<p align="center"><strong>面向 AI Agent 的 Windows 本地凭证管理器。</strong></p>

<p align="center">
  <a href="README.zh-CN.md">简体中文</a> · <a href="README.md">English</a>
</p>

Ask Key 把凭证加密保存在本机。Agent 只能按名称和元数据发现凭证，然后向本地 Broker 请求访问；是否放行由你决定。获批的文本值会作为环境变量传给目标进程，文件值会作为短期私有文件交付。请旨不会把库里的明文直接返回给 Agent；获批的目标进程会收到值，并且和你运行的任何进程一样可以复制或转发它。

本仓库只包含 Windows 实现。发布工具使用 .NET，不包含 PowerShell 脚本。

> **macOS 上游：** 原始 macOS 应用及共享的产品/安全文档维护在 [sudoHG/AskKey](https://github.com/sudoHG/AskKey)。本仓库不包含 macOS 源码。 这是非官方的 Windows 移植版本，上游项目不对本构建负责。

## 功能

- **查找凭证：** Agent 可以查询凭证名称、用途说明和变量映射，但目录查询不会返回明文值。
- **控制访问：** 每条凭证可以设为 **允许**、**每次询问** 或 **隐藏**。
- **审核写入：** 新建、修改、删除和整理凭证都可以要求批准；批准绑定到你看到的那份冻结内容。
- **值不出本机：** 查看或复制凭证值、查看冻结审批内容前，需要重新进行 Windows Hello 或 PIN 确认；普通管理操作在设置的 5/15 分钟会话内复用确认。
- **保护审批界面：** 会话锁定、断开或注销时，详细审批会自动收起，恢复后再显示。
- **快速创建：** 内置 API、SSH、GitHub App、云账号和数据库连接模板。

## 工作方式

```mermaid
flowchart LR
    A[Agent] --> H[askkey helper]
    H --> B[应用内 Broker]
    B --> P{你的批准}
    P -->|批准| T[目标进程<br/>环境变量或私有临时文件]
    P -->|拒绝| X[不交付任何内容]
```

Helper 只把本次请求选中的凭证交给目标命令。Broker 负责权限判断和审批队列。Agent 只能看到目标进程的输出或退出状态，看不到库里的凭证值。

## 仓库结构

| 路径 | 用途 |
|---|---|
| `AskKey.WinUI` | WinUI 3 桌面应用、托盘菜单、OOBE 和设置 |
| `AskKey.Broker` | 本地审批、策略和交付 Broker |
| `AskKey.Vault` | 加密凭证库和密钥管理 |
| `AskKey.Helper` | `askkey.exe` MCP Helper 和命令包装器 |
| `AskKey.Integrations` | Codex、Claude Code、Cursor、Grok 客户端接入 |
| `AskKey.System` | 受限进程和 Windows 运行时集成 |
| `*.Tests` | 单元测试和集成测试 |
| `tools/AskKey.Build` | .NET 发布工具：Helper 清单、构建、签名和 Velopack 打包 |

## 环境要求

- Windows 10 2004 或更高版本，或 Windows 11。
- x64。
- 构建和测试需要 .NET 10 SDK；发布包自带运行时。
- 管理操作和查看凭证值需要 Windows Hello 或 PIN。

## 构建与测试

在仓库根目录执行：

```text
dotnet test .\AskKey.slnx
dotnet run --project .\AskKey.WinUI\AskKey.WinUI.csproj -p:Platform=x64
```

调试构建不会自动设置环境变量。若要让调试运行使用独立的开发数据目录，请在启动前设置 `ASKKEY_DEVELOPMENT=1`；否则会使用正常的 `%APPDATA%\AskKey` 和 `%LOCALAPPDATA%\AskKey` 路径。

## 发布

发布工具是 .NET 项目，不是 PowerShell 脚本：

```text
dotnet run --project .\tools\AskKey.Build -- publish
```

使用当前用户证书商店里的证书签名 Host 和 Helper：

```text
dotnet run --project .\tools\AskKey.Build -- publish --certificate-thumbprint <thumbprint>
```

工具会从 `AskKey.Integrations\AskKeyVersion.cs` 读取版本号，运行测试，构建自包含 x64 包，校验 `AskKey.exe` 和 `Helpers\askkey.exe`，把卫星资源裁剪到 `zh-CN`，按需签名，调用 `vpk pack`，然后发布 Guard 服务、构建 Guard MSI，最后生成同时包含应用和 Guard 的 `AskKeySetup-<version>.exe`。

默认输出到 `artifacts\publish\release`：

- `AskKeySetup-<version>.exe` — **推荐安装器**，一次 UAC 安装应用和 Guard 服务
- `AskKey-win.msi` — 仅应用，全机器安装到 `%ProgramFiles%\AskKey\current\`
- `AskKeyGuard-<version>.msi` — 仅 Guard 服务，修复/高级安装使用
- `AskKey-win-Setup.exe` — 当前用户安装，不含 Guard，不能使用 Hello 解锁
- `AskKey-win-Portable.zip`
- `AskKey-<version>-full.nupkg`
- `assets.win.json`、`releases.win.json`、`RELEASES`

发布时请把整套文件一起上传，不要手动改 feed 文件。

### Windows Hello Guard 服务

正常安装使用 `AskKeySetup-<version>.exe`：它会先安装应用，再安装 Guard 服务；从“应用和功能”卸载时按相反顺序移除。单独的两个 MSI 只用于修复或高级部署；手动安装时先装应用，再装 `AskKeyGuard-<version>.msi`，卸载时先卸 Guard。当前用户安装（`AskKey-win-Setup.exe`）不能使用 Hello 解锁。

开发时可单独发布服务：

```text
dotnet run --project .\tools\AskKey.Build -- guard
```

`AskKey.Guard.Service\Install-GuardService.ps1` 是修复/开发安装路径；`installer\Guard\build-guard-msi.ps1 -Unsigned` 构建未签名 Guard MSI。已生成两个 MSI 后，可用 `dotnet run --project .\tools\AskKey.Build -- bundle` 重新生成 `AskKeySetup`。详见 `docs/guard-msi.md`。正式发布仍需要代码签名。

未签名构建会把 `Helpers\` 的 SHA-256 清单嵌入 Host，并在 Agent 连接前校验。Windows SmartScreen 仍会提示未签名下载。签名构建要求 Host 和 Helper 使用同一发布者证书。

## 安装

| 安装器 | 范围 | 位置 | 提权 |
|---|---|---|---|
| `AskKeySetup-<version>.exe` | 全机器，应用 + Guard | `%ProgramFiles%\AskKey\current\` + `%ProgramFiles%\AskKey\Guard\` | 需要 UAC |
| `AskKey-win.msi` + `AskKeyGuard-<version>.msi` | 全机器，分离安装/修复 | 同上 | 需要 UAC |
| `AskKey-win-Setup.exe` | 当前用户，无 Guard | `%LocalAppData%\AskKey\current\` | 不需要 |

推荐直接运行 `AskKeySetup-<version>.exe`。从“应用和功能”卸载时会一起移除应用和 Guard 服务。分离 MSI 只用于修复或高级部署；手动顺序是先应用、后 Guard，卸载时先 Guard。

安装器会把实际程序放在 `current\` 子目录：

```text
<安装根目录>\current\AskKey.exe
<安装根目录>\current\Helpers\askkey.exe
```

凭证和密钥材料存放在 `%APPDATA%\AskKey`；设置和 Broker 管道存放在 `%LOCALAPPDATA%\AskKey`。删除安装目录不会删除凭证库。

卸载后有两处内容是**有意保留**的：

- `%ProgramData%\AskKey\Guard` 保存 Guard 服务的 DPAPI 主密钥和各账户的 Windows Hello 注册。删掉它会让所有已注册账户的 Hello 解锁永久失效，所以该组件被标记为 Permanent。详见 [docs/guard-service.md](docs/guard-service.md)。
- `%LOCALAPPDATA%\Microsoft\WindowsAppSDK\velopack.AskKey.png` 是非打包应用在任务栏使用的图标位图。

旧的用户级安装不会自动迁移到全机器安装（目前没有对外发布过用户级版本）。请先卸载用户级版本再安装 `AskKeySetup-<version>.exe`，否则两个安装会同时出现在“应用和功能”里。

## Agent 接入

应用内置 Claude Code、Codex、Cursor 和 Grok CLI 的接入设置。打开 **Agent 接入**，勾选客户端并确认即可。应用会添加 Ask Key MCP 服务，保留原有客户端配置，并在报告连接成功前校验 Helper 和 Broker。

任意 MCP stdio 客户端也可以手动配置：

```json
{
  "command": "C:\\Users\\<you>\\AppData\\Local\\AskKey\\current\\Helpers\\askkey.exe",
  "args": ["mcp"]
}
```

全机器安装请改用 `C:\\Program Files\\AskKey\\current\\Helpers\\askkey.exe`。手动配置不会安装发现钩子，也不会执行应用内的连接检查；Broker 权限、审批和凭证交付保护仍然生效。

## 安全模型

Agent 只被部分信任：它可以以你的 Windows 用户身份运行命令，但没有经过审批流程，不能拿到库里的凭证值。

- **默认模式** 使用 Windows DPAPI（`CurrentUser`）包裹 vault key。它能保护静态文件，但任何以你的身份运行的程序都可以解开同一个 key。
- **高安全模式** 使用你设置的口令，通过 PBKDF2-HMAC-SHA256（600,000 次迭代）派生 KEK 包裹 vault key，并删除 DPAPI 副本。安装全机器 AskKey Guard 服务后，首次启动输入一次口令，之后的启动可以用 Windows Hello 解锁；口令始终是恢复路径。没有安装服务时，每次启动都要输入口令。启用此模式会重新包裹现有 key，不会轮换 key；切换前已经获得的 key 或 DPAPI 副本如果仍存在于 Ask Key 之外，依然有效。忘记口令后无法找回。
- 查看或复制凭证值需要设备确认。Windows Hello 确认一次后，在设置选择的时间窗口（5–60 分钟）内进行应用内管理操作不再重复验证。
- **重新认证：** 查看或复制凭证值、查看冻结审批内容、批准 Agent 读写请求、把凭证权限提升为 **允许**，以及抹除本机数据，即使在管理会话内也需要重新进行 Windows Hello 或 PIN 确认。普通管理操作在设置的 5/15 分钟会话内复用确认；锁屏、断开和注销会结束该会话。
- **进程内存边界：** 凭证库解锁期间，vault key 常驻 Ask Key 进程内存。`Lock()` 会清零它，但锁屏不会锁定凭证库，因为 Broker 仍需继续处理审批。以同一 Windows 用户身份运行的进程可以读取进程内存；这是两种 key 模式共同的限制。
- **安装与完整性：** 除非提供签名证书，Release 构建仍未签名。未签名构建信任 Host 程序集内嵌的 Helper 清单，因此同一用户对安装目录的写权限仍是信任边界。Program Files 下的全机器 MSI 去掉了这条容易利用的写路径；更强的代码身份边界需要证书。签名构建要求 Host 和 Helper 使用同一发布者。
- Helper 必须和 Host 一起放在官方安装根目录内；被复制到其他位置的 Helper 会被拒绝。
- 调用方名称和用途是自报声明，不是经过认证的身份。获批的目标进程可以复制或转发交付给它的任何内容。**隐藏** 凭证不会出现在目录中，但这不能阻止已经收到值的命令继续转发。
- Ask Key 没有备份或恢复功能，请把原始凭证另外保存好。

Windows Hello 实际弹窗、会话锁定/解锁、Authenticode 签名和硬件相关行为必须在目标机器上验证；单元测试不能证明这些。

## 许可证

MIT。见 [LICENSE](LICENSE) 和 [NOTICE](NOTICE)。
