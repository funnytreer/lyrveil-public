# PlaybackProbe

`PlaybackProbe` 是词间 · Lyrveil 的播放器兼容性探针。它只读取 Windows 的 Global System Media Transport Controls（GSMTC），不控制播放器，也不会访问歌词网络服务。

探针默认只输出到当前终端。只有显式使用 `--output` 时才会把歌曲元数据写入本地文件。

## 运行

从仓库根目录执行：

```powershell
.\scripts\run-playback-probe.ps1
```

常用模式：

```powershell
# 查看当前所有媒体会话后退出
.\scripts\run-playback-probe.ps1 --once

# 保存完整 JSONL 日志
.\scripts\run-playback-probe.ps1 --output .\artifacts\spotify.jsonl

# 终端也只输出 JSON Lines
.\scripts\run-playback-probe.ps1 --json
```

## 验证动作

每个播放器依次执行：

1. 启动一首已知歌曲。
2. 暂停，再恢复播放。
3. 拖动进度到歌曲中部。
4. 切到下一首，再切回上一首。
5. 观察 `title`、`artist`、`endTime`、`position`、`playbackStatus` 和 controls 是否正确。
6. 保存 JSONL，并把结果填写到 `docs/playback-compatibility.md`。

持续模式每秒显示的进度来自“最近一次 GSMTC 时间线 + 本地时钟推算”，不会每秒重新访问播放器。
