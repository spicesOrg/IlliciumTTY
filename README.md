# IlliciumTTY

IlliciumTTY 是一个基于 Avalonia 的 SSH 桌面客户端，面向需要同时使用远程终端和远程文件管理的日常运维、开发与调试场景。

应用提供连接树、单连接工作区、多终端工作区、SFTP 文件管理和终端/文件管理器路径同步能力。项目当前以 .NET 10 和 Avalonia 12 构建。

## 功能特性

- SSH 连接管理：支持收藏链接、临时链接、文件夹分组、编辑、删除与拖拽移动。
- 单连接工作区：左侧管理连接，右侧同时展示 SSH 终端和远程文件管理器。
- 多终端工作区：选择文件夹后可批量打开其中的 SSH 链接，并可开启多终端输入同步。
- 远程文件管理：支持目录浏览、新建文件/文件夹、删除、重命名、权限修改、上传与下载。
- 远程文件编辑：下载远程文件到本地临时文件，保存后自动回传。
- 路径同步：支持终端到文件管理器、文件管理器到终端、双向同步和禁用同步。
- 配置持久化：连接配置保存到本地 SQLite 数据库。
- 密钥处理：Windows 下使用 DPAPI 保护保存的敏感信息；其他平台会以 Base64 明文形式保存。

> 当前 SSH Agent 认证选项尚未实际接入。由于 SSH.NET 当前实现不支持本机 SSH Agent，使用时请优先选择密码或私钥认证。

## 技术栈

- .NET 10
- Avalonia 12
- CommunityToolkit.Mvvm
- SSH.NET
- Microsoft.Data.Sqlite

## 项目结构

```text
.
├── IlliciumTTY.sln
├── IlliciumTTY/
│   ├── Controls/       # Avalonia 自定义控件与工作区面板
│   ├── Models/         # 连接、工作区、远程文件等领域模型
│   ├── Services/       # SSH、SFTP、配置存储、密钥保护等服务
│   ├── ViewModels/     # MVVM 状态与命令
│   ├── Views/          # 主窗口视图
│   └── IlliciumTTY.csproj
├── packaging/          # 打包相关目录
└── LICENSE
```

## 环境要求

- .NET SDK 10.0 或兼容版本
- Windows、Linux 或 macOS 桌面环境
- 可访问目标服务器的 SSH/SFTP 服务

## 本地运行

```powershell
dotnet restore IlliciumTTY.sln
dotnet run --project IlliciumTTY/IlliciumTTY.csproj
```

## 构建

```powershell
dotnet build IlliciumTTY.sln -c Release
```

## 发布示例

Windows x64：

```powershell
dotnet publish IlliciumTTY/IlliciumTTY.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Linux x64：

```powershell
dotnet publish IlliciumTTY/IlliciumTTY.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
```

发布产物默认位于：

```text
IlliciumTTY/bin/Release/net10.0/<runtime>/publish/
```

## 配置与数据

应用启动后会在可写用户目录下创建配置数据库：

```text
IlliciumTTY/illiciumtty.db
```

目录优先级由应用自动选择：

1. LocalApplicationData
2. ApplicationData
3. Documents
4. 应用目录下的 `Data/`

Windows 下保存的密码和私钥口令会通过 DPAPI 加密；非 Windows 平台目前仅做 Base64 编码保存，请避免在不可信环境中保存敏感凭据。

## 使用说明

1. 在左侧连接树中新建 SSH 链接或文件夹。
2. 填写主机、端口、用户名、认证方式和默认远程路径。
3. 选择单个 SSH 链接会进入单连接工作区，并自动连接终端与加载远程文件列表。
4. 选择文件夹会进入多终端工作区，批量连接文件夹中的 SSH 链接。
5. 在同步按钮中切换终端与文件管理器的路径同步模式。
6. 在多终端模式下，可按需开启输入同步，将当前终端输入广播到其他已连接会话。

## 许可证

本项目使用 BSD 3-Clause License。详见 [LICENSE](LICENSE)。
