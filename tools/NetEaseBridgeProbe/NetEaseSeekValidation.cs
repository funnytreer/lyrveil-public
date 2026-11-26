using System.Text.Json;
using LyricsXQuiet.Platform.Windows.Playback;

namespace LyricsXQuiet.Tools.NetEaseBridgeProbe;

internal static class NetEaseSeekValidation
{
    private const string Expression = """
        (async () => {
          const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
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
            const chunkId = 730000000 + Math.floor(Math.random() * 90000000);
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

          let audioPlayer = null;
          let audioScore = -1;
          let storeFacade = null;
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
          const helper = globalThis[Symbol.for("lyrveil.netease.readonly-playback.v14")];
          if (!audioPlayer || typeof audioPlayer.seek !== "function" ||
              typeof audioPlayer.resumePlay !== "function" ||
              typeof audioPlayer.pause !== "function" || typeof helper !== "function") {
            return { ok: false, error: "required playback diagnostics are unavailable" };
          }
          const readAudioCounterMs = async playId => {
            if (typeof audioPlayer.getPlayedTime !== "function") return null;
            try {
              const response = await Promise.resolve(audioPlayer.getPlayedTime({ playId }));
              const variants = [response, response && response.data,
                response && typeof response.result === "object" ? response.result : null]
                .filter(Boolean);
              for (const key of ["playedAudioTime", "playedTime"]) {
                for (const variant of variants) {
                  const value = Number(variant[key]);
                  if (Number.isFinite(value) && value >= 0) return Math.round(value * 1000);
                }
              }
            } catch (_) { }
            return null;
          };

          let state = null;
          try {
            const store = storeFacade && await Promise.resolve(storeFacade.getStore());
            state = store && typeof store.getState === "function" ? store.getState() : store;
          } catch (_) { }
          const playing = state && (state.playing || state.player || state.play) || {};
          const playId = playing.playId;
          const original = await helper();
          if (!playId || !original || !original.ok || !original.playback ||
              original.playback.status !== "paused") {
            return { ok: false, error: "validation requires a paused NetEase track" };
          }

          const durationSeconds = original.playback.durationMs / 1000;
          const originalSeconds = original.playback.positionMs / 1000;
          const targets = [0.2, 0.62, 0.37]
            .map(ratio => Math.max(5, Math.min(durationSeconds - 5, durationSeconds * ratio)))
            .map(value => Math.round(value * 10) / 10);
          const runs = [];
          const commandId = suffix => `lyrveil-validation-${Date.now()}-${suffix}`;
          let restored = false;
          try {
            await Promise.resolve(audioPlayer.resumePlay({
              playId,
              resumeOrPauseId: commandId("resume")
            }));
            await delay(450);
            for (let runIndex = 0; runIndex < targets.length; runIndex += 1) {
              const targetSeconds = targets[runIndex];
              const startedAt = Date.now();
              const seekResult = await Promise.resolve(audioPlayer.seek({
                playId,
                seekId: commandId(`seek-${runIndex}`),
                value: targetSeconds
              }));
              const samples = [];
              for (let sampleIndex = 0; sampleIndex < 18; sampleIndex += 1) {
                await delay(60);
                const snapshot = await helper();
                const playback = snapshot && snapshot.playback;
                const audioCounterMs = await readAudioCounterMs(playId);
                samples.push({
                  elapsedMs: Date.now() - startedAt,
                  ok: !!(snapshot && snapshot.ok),
                  error: snapshot ? snapshot.error : null,
                  trackId: snapshot && snapshot.track ? snapshot.track.id : null,
                  positionMs: playback ? playback.positionMs : null,
                  audioCounterMs,
                  status: playback ? playback.status : null,
                  playId: playback ? playback.playId : null,
                  frozen: playback ? playback.timelineFrozen : null,
                  seekLeadCorrectionMs: playback ? playback.seekLeadCorrectionMs : null,
                  clock: playback ? playback.timelineClock : null,
                  eventType: playback ? playback.timelineEventType : null
                });
              }
              runs.push({
                targetMs: Math.round(targetSeconds * 1000),
                seekResult,
                samples
              });
            }
          } finally {
            try {
              await Promise.resolve(audioPlayer.seek({
                playId,
                seekId: commandId("restore"),
                value: originalSeconds
              }));
              await delay(250);
              await Promise.resolve(audioPlayer.pause({
                playId,
                resumeOrPauseId: commandId("pause")
              }));
              restored = true;
            } catch (_) { }
          }

          const summaries = runs.map(run => {
            const firstFrozen = run.samples.find(sample => sample.ok && sample.frozen === true) || null;
            const firstRunning = run.samples.find(sample => sample.ok && sample.frozen === false) || null;
            const final = [...run.samples].reverse().find(sample => sample.ok) || null;
            return {
              targetMs: run.targetMs,
              sawFrozen: firstFrozen !== null,
              resumedAtMs: firstRunning ? firstRunning.elapsedMs : null,
              firstRunningPositionMs: firstRunning ? firstRunning.positionMs : null,
              finalPositionMs: final ? final.positionMs : null,
              seekLeadCorrectionMs: final ? final.seekLeadCorrectionMs : null,
              finalClock: final ? final.clock : null,
              invalidSamples: run.samples.filter(sample => !sample.ok).length
            };
          });
          const allPassed = summaries.length === 3 && summaries.every(summary =>
            summary.resumedAtMs !== null &&
            summary.firstRunningPositionMs >= summary.targetMs - 1000 &&
            summary.seekLeadCorrectionMs === 0 &&
            summary.firstRunningPositionMs <= summary.targetMs + 2500 &&
            (summary.sawFrozen ||
              (summary.resumedAtMs <= 250 &&
               summary.firstRunningPositionMs <= summary.targetMs + 750)) &&
            summary.invalidSamples === 0);
          return { ok: allPassed && restored, restored, summaries, runs };
        })()
        """;

    public static async Task<JsonElement> CaptureAsync(
        CdpConnection connection,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        _ = await NetEaseCdpProbe.CaptureAsync(connection, timeout, cancellationToken);
        var validationTimeout = TimeSpan.FromSeconds(Math.Max(timeout.TotalSeconds, 30));
        var response = await connection.SendCommandAsync(
            "Runtime.evaluate",
            new
            {
                expression = Expression,
                awaitPromise = true,
                returnByValue = true,
                userGesture = true,
                silent = true
            },
            validationTimeout,
            cancellationToken);
        if (!response.TryGetProperty("result", out var remoteObject)
            || !remoteObject.TryGetProperty("value", out var value))
        {
            throw new ProbeException("网易云页面没有返回跳转验证结果。");
        }

        return value.Clone();
    }
}
