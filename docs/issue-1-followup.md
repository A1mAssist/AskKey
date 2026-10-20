# Issue #1 安全复审跟进

日期：2026-10-19

## 当前状态

- 修复提交：`fcc1a12d8e6df74a3ce76aba621b175f53e679ce`
- 远程分支：`origin/main` 已快进到该提交
- Issue：https://github.com/A1mAssist/AskKey/issues/1
- 本轮回复：https://github.com/A1mAssist/AskKey/issues/1#issuecomment-6269960257
- `codex/issue-1-followup` 临时分支已删除；远端没有同名分支
- Issue 仍保持 open，因为下面“仍未闭环”的三项是已知限制，不是本轮能够静默解决的问题

## 本轮修复内容

### 1. Fresh authentication（P1）

`AppHost.AuthenticateAndBeginManagement` 不再返回始终放行的 authenticator。以下操作即使处于有效管理会话中，也重新要求 Windows Hello/PIN：

- 查看或复制凭证值
- 查看冻结审批内容
- Agent 读/写批准
- 冻结文件写入
- 将凭证权限提升为 `Allow`
- 抹除本机数据

新增 `AskKey.WinUI/ManagementAuthenticationPolicy.cs`，按精确的 operation reason 缓存确认结果：

- `BeginManagementSession` 和实际操作使用同一个 authenticator
- 同一个 reason 只确认一次，避免重复弹 Hello
- 会话结束后缓存失效
- 普通管理操作仍然复用配置的 5/15 分钟管理会话

权限编辑现在只在真正从非 `Allow` 变为 `Allow` 时使用 fresh auth；编辑已经允许的凭证仍按普通管理处理。

### 2. 高安全模式 fail-closed（P2）

- 开启高安全时先写并校验 passphrase-wrapped key，再删除 DPAPI key
- DPAPI 删除失败或删除后仍有残留文件时，删除 wrapped key 并报错，不再误显示高安全已开启
- 高安全启动时补删崩溃遗留的 DPAPI pending/app key
- 相关测试覆盖了 DPAPI 删除失败和残留副本清理

高安全模式仍然只是重新包裹现有 vault key，**不轮换 key**。之前已经获得的 key 或 DPAPI 副本如果仍存在于 Ask Key 之外，依然有效。

### 3. 其他复审项

- 剪贴板：`SetContentWithOptions` 失败后不再退回普通 `SetContent`，避免重新写入剪贴板历史和漫游；复制直接取消并报错
- 命令解析：裸命令只解析 `.exe`/`.com`，`.cmd`/`.bat` 必须显式写扩展名
- 审批显示：命令参数改用 Windows `CommandLineToArgvW` 引号规则，不再使用 POSIX `$'...'`
- 临时文件清理：删除 `.owner-lock` 保护的目录前先释放 lease，重试可以收敛
- README 中英文同步：修正 fresh-auth、调试数据目录、进程内存边界、高安全不轮换 key、未签名安装边界、调用方自报、获批目标可转发、非官方移植等描述

## 验证结果

- `dotnet build AskKey.slnx -c Release`：0 errors，8 个既有 xUnit1031 警告
- `dotnet test AskKey.slnx -c Release`：**421/421 passed**
- WinUI x64 Release 构建通过

未做真实设备端到端验证：

- Windows Hello 实际弹窗和取消路径
- Program Files 下的 per-machine MSI 安装
- Authenticode 签名后的完整 release 流程

## 仍未闭环

1. **vault key 在进程内常驻内存**
   锁屏/断开/注销只结束管理会话，不锁 vault，因为 Broker 仍需继续处理审批。同一 Windows 用户下的进程可以读取进程内存。该限制已写入 README。

2. **高安全模式不轮换 key**
   开启高安全只是重新包裹现有 key，不是 key rotation。需要单独的 rekey 迁移。

3. **Release 未签名**
   无证书时，未签名构建仍信任 host 内嵌的 Helper 清单；同一用户对安装目录的写权限仍是代码完整性边界。证书是更强边界的剩余依赖。

## 关键文件

- `AskKey.WinUI/ManagementAuthenticationPolicy.cs`
- `AskKey.WinUI/AppHost.cs`
- `AskKey.WinUI/Views/CredentialsView.xaml.cs`
- `AskKey.WinUI/Dialogs/CredentialEditorWindow.xaml.cs`
- `AskKey.WinUI/Dialogs/ImportCredentialWindow.xaml.cs`
- `AskKey.Vault/Vault.cs`
- `AskKey.Vault/Vault.TextCredentials.cs`
- `AskKey.Vault/Vault.FileBundle.cs`
- `AskKey.Vault/FileDeliveryManager.cs`
- `AskKey.Broker/Runtime/BrokerTextRuntime.cs`
- `AskKey.Vault/Vault.CredentialDelivery.cs`
- `AskKey.WinUI/ClipboardSafety.cs`
- `README.md`
- `README.zh-CN.md`

## 后续动作

1. `D:\Workspaces\AskKey` 主工作区本轮没有改动，本地 `main` 仍停在 `d022c91`；远端 `main` 已经是 `fcc1a12`。拉取前先处理该工作区当前的未提交改动。
2. 等 issue #1 的复审者确认 fresh-auth 路径和高安全 fail-closed 行为。
3. 如果要完全关闭 issue #1，需要决定“key 常驻内存 / key rotation / 未签名 Release”是接受为长期限制，还是拆成后续 issue。
4. 隔离 worktree `C:\Users\A1mAssist\.codex\worktrees\issue-1-followup\AskKey` 目前是 detached HEAD，指向 `fcc1a12`；不再需要时可删除该 worktree。