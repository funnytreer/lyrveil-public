# NetEaseBridgeProbe

这是“词间”自有网易云精确进度桥的最小只读原型。它连接用户明确指定的本机 Chrome DevTools Protocol（CDP）端口，读取网易云音乐页面中已经存在的播放状态，然后输出一份 JSON 快照。

## 安全边界

- 只允许连接 `127.0.0.1` / `localhost`，并校验页面 WebSocket 与指定端口一致。
- 不会启动、关闭、注入或修改网易云音乐。
- 不提供播放、暂停、跳转等控制命令。
- 不写日志、不保存歌曲记录，不把数据发送到网络。
- 不包含 BetterNCM、InfLink 或其他第三方项目的代码。

CDP 调试端口会让同一台电脑上的其他程序也有机会检查页面，因此不应长期开放固定端口。正式接入时应使用随机本机端口，并只在进度桥运行期间开放。

## 使用

前提是网易云音乐已经由用户以 CDP 调试端口启动；本工具本身不会替用户启动或重启网易云。

```powershell
dotnet run --project tools/NetEaseBridgeProbe -- --port 9222 --list
dotnet run --project tools/NetEaseBridgeProbe -- --port 9222
```

连续把快照交给词间本机接收端：

```powershell
dotnet run --project tools/NetEaseBridgeProbe -- --port 9222 --watch --bridge-port <词间随机端口>
```

连续模式每 125 毫秒只读检查一次。普通时间锚点最多每秒发送 4 次；检测到跳转、切歌或播放状态变化时会立即发送。CDP 页面或词间接收端断开后，当前连接会关闭并自动重连，不会把快照或错误写入日志文件。

如果自动识别不到正确页面，可以先用 `--list` 查看页面 ID，再明确指定：

```powershell
dotnet run --project tools/NetEaseBridgeProbe -- --port 9222 --target <页面ID>
```

可用 `--timeout <秒>` 调整连接与命令超时，范围为 1–60 秒，默认 8 秒。

## 输出

成功连接后会输出：

- 歌曲 ID、歌名、歌手、专辑；
- 播放状态；
- 当前位置与总时长（毫秒）；
- 少量不含播放历史的兼容性诊断字段。

网易云的页面模块不是公开稳定接口，升级后字段可能变化。探针会在无法定位播放模块时返回明确错误和诊断信息，而不会回退到修改客户端或读取用户文件。
