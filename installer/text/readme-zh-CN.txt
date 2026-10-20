安装完成后

1. 从开始菜单或任务栏启动 Ask Key。
2. 首次启动时按引导设置高安全模式：设置一个口令，并选择是否启用 Windows Hello 解锁。
3. 高安全模式下，口令是恢复手段；启用 Windows Hello 后，之后每次启动只需 Hello 验证。

数据位置
- %AppData%\AskKey 凭证数据库与密钥文件（仅当前账户可读写）
- %ProgramData%\AskKey\Guard Guard 服务的主密钥与各账户的 Hello 注册

卸载
从「设置 - 应用 - 已安装的应用」卸载 Ask Key，应用与 Guard 服务会一并移除。
凭证数据保留在 %AppData%\AskKey，需要彻底清除请在卸载后手动删除该目录。
