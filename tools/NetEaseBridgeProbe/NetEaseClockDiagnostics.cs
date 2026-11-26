using System.Text.Json;

namespace LyricsXQuiet.Tools.NetEaseBridgeProbe;

internal static class NetEaseClockDiagnostics
{
    private const string Expression = """
        (async () => {
          const finite = value => {
            const number = typeof value === "number" ? value : Number(value);
            return Number.isFinite(number) ? number : null;
          };
          const expanded = value => {
            const values = [];
            if (!value || (typeof value !== "object" && typeof value !== "function")) return values;
            values.push(value);
            if (value.default && (typeof value.default === "object" || typeof value.default === "function")) {
              values.push(value.default);
            }
            try {
              const children = Object.values(value);
              if (children.length <= 80) {
                for (const child of children) {
                  if (child && (typeof child === "object" || typeof child === "function")) values.push(child);
                }
              }
            } catch (_) { }
            return values;
          };
          let webpackRequire = null;
          for (const name of Object.keys(globalThis).filter(name =>
            name === "webpackJsonp" || name.startsWith("webpackJsonp"))) {
            const chunk = globalThis[name];
            if (!Array.isArray(chunk) || typeof chunk.push !== "function" ||
                chunk.push === Array.prototype.push) continue;
            let captured = null;
            const chunkId = 680000000 + Math.floor(Math.random() * 90000000);
            const moduleId = chunkId + 100000000;
            try {
              chunk.push([[chunkId], {
                [moduleId]: (_module, _exports, candidate) => { captured = candidate; }
              }, [[moduleId]]]);
            } catch (_) { }
            if (captured && captured.c) {
              webpackRequire = captured;
              try { delete captured.c[moduleId]; if (captured.m) delete captured.m[moduleId]; }
              catch (_) { }
              break;
            }
          }
          if (!webpackRequire) return { ok: false, error: "runtime unavailable" };

          let semanticPositionSeconds = null;
          for (const factory of Object.values(webpackRequire.m || {})) {
            let source = "";
            try { source = Function.prototype.toString.call(factory); } catch (_) { continue; }
            const getter = source.match(
              /get\s+playingProgress\s*\(\)\s*\{[\s\S]{0,500}?current\s*:\s*([A-Za-z_$][\w$]*)\.([A-Za-z_$][\w$]*)\.getValue\(\)/);
            if (!getter) continue;
            const escaped = getter[1].replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
            const moduleMatch = source.match(new RegExp(
              "(?:^|[,;])\\s*" + escaped + "\\s*=\\s*[A-Za-z_$][\\w$]*\\((\\d+)\\)"));
            if (!moduleMatch) continue;
            try {
              const exportsValue = webpackRequire(Number(moduleMatch[1]));
              const observable = exportsValue && exportsValue[getter[2]];
              if (observable && typeof observable.getValue === "function") {
                semanticPositionSeconds = finite(observable.getValue());
              }
            } catch (_) { }
            if (semanticPositionSeconds !== null) break;
          }

          let storeFacade = null;
          let audioPlayer = null;
          let audioScore = -1;
          for (const moduleRecord of Object.values(webpackRequire.c || {})) {
            for (const candidate of expanded(moduleRecord && moduleRecord.exports)) {
              if (!storeFacade && typeof candidate.getStore === "function") storeFacade = candidate;
              const possibleAudio = candidate.AudioPlayer && typeof candidate.AudioPlayer === "object"
                ? candidate.AudioPlayer
                : candidate;
              const score =
                (typeof possibleAudio.subscribePlayStatus === "function" ? 100 : 0) +
                (typeof possibleAudio.getPlaybackInfo === "function" ? 10 : 0) +
                (typeof possibleAudio.getPlayedTime === "function" ? 1 : 0);
              if (score > audioScore) { audioPlayer = possibleAudio; audioScore = score; }
            }
          }
          let state = null;
          try {
            const store = storeFacade && await Promise.resolve(storeFacade.getStore());
            state = store && typeof store.getState === "function" ? store.getState() : store;
          } catch (_) { }
          const playing = state && (state.playing || state.player || state.play) || {};
          const identifiers = [
            {kind: "resourceTrackId", value: playing.resourceTrackId},
            {kind: "onlineResourceId", value: playing.onlineResourceId},
            {kind: "playId", value: playing.playId}
          ].filter(item => item.value !== undefined && item.value !== null && item.value !== "");
          const accumulatedByIdentifier = [];
          if (audioPlayer && typeof audioPlayer.getPlayedTime === "function") {
            for (const identifier of identifiers) {
              let accumulated = null;
              try {
                const response = await Promise.resolve(
                  audioPlayer.getPlayedTime({playId: identifier.value}));
                const variants = [response, response && response.data,
                  response && typeof response.result === "object" ? response.result : null]
                  .filter(Boolean);
                for (const key of ["playedAudioTime", "playedTime"]) {
                  for (const variant of variants) {
                    const value = finite(variant[key]);
                    if (value !== null) { accumulated = { key, seconds: value }; break; }
                  }
                  if (accumulated) break;
                }
              } catch (_) { }
              accumulatedByIdentifier.push({
                kind: identifier.kind,
                value: String(identifier.value),
                accumulated
              });
            }
          }
          const audioMethods = [];
          if (audioPlayer) {
            try {
              const names = new Set(Object.getOwnPropertyNames(audioPlayer));
              let prototype = Object.getPrototypeOf(audioPlayer);
              while (prototype && prototype !== Object.prototype) {
                for (const name of Object.getOwnPropertyNames(prototype)) names.add(name);
                prototype = Object.getPrototypeOf(prototype);
              }
              for (const name of names) {
                if (typeof audioPlayer[name] === "function") audioMethods.push(name);
              }
              audioMethods.sort();
            } catch (_) { }
          }
          const helper = globalThis[Symbol.for("lyrveil.netease.readonly-playback.v13")];
          let helperSnapshot = null;
          try { helperSnapshot = typeof helper === "function" ? await helper() : null; }
          catch (_) { }
          return {
            ok: true,
            semanticPositionSeconds,
            accumulatedByIdentifier,
            audioMethods,
            playingState: playing.playingState ?? null,
            helperSnapshot
          };
        })()
        """;

    public static async Task<JsonElement> CaptureAsync(
        CdpConnection connection,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var response = await connection.SendCommandAsync(
            "Runtime.evaluate",
            new
            {
                expression = Expression,
                awaitPromise = true,
                returnByValue = true,
                userGesture = false,
                silent = true
            },
            timeout,
            cancellationToken);
        if (!response.TryGetProperty("result", out var remoteObject) ||
            !remoteObject.TryGetProperty("value", out var value))
        {
            throw new ProbeException("网易云页面没有返回时钟诊断结果。");
        }
        return value.Clone();
    }
}
