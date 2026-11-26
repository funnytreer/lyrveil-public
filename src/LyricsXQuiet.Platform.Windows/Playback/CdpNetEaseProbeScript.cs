namespace LyricsXQuiet.Platform.Windows.Playback;

public static class CdpNetEaseProbeScript
{
    private const string HelperKey = "lyrveil.netease.readonly-playback.v14";

    public const string ResetExpression =
        "(() => { const key = Symbol.for(\"" + HelperKey + "\"); "
        + "const helper = globalThis[key]; "
        + "try { if (helper && typeof helper.dispose === 'function') helper.dispose(); } catch (_) { } "
        + "try { delete globalThis[key]; } catch (_) { } return true; })()";

    /// <summary>
    /// Installs a page-local, read-only accessor on first use. Later samples call
    /// only that accessor, avoiding repeated scans of the page's module cache.
    /// The helper reads playback state; it never seeks, starts or pauses audio.
    /// </summary>
    public const string Expression = """
        (async () => {
          const helperKey = Symbol.for("lyrveil.netease.readonly-playback.v14");
          const legacyHelperKeys = [
            Symbol.for("lyrveil.netease.readonly-playback.v13"),
            Symbol.for("lyrveil.netease.readonly-playback.v12"),
            Symbol.for("lyrveil.netease.readonly-playback.v11"),
            Symbol.for("lyrveil.netease.readonly-playback.v10"),
            Symbol.for("lyrveil.netease.readonly-playback.v9"),
            Symbol.for("lyrveil.netease.readonly-playback.v8"),
            Symbol.for("lyrveil.netease.readonly-playback.v7"),
            Symbol.for("lyrveil.netease.readonly-playback.v6"),
            Symbol.for("lyrveil.netease.readonly-playback.v5"),
            Symbol.for("lyrveil.netease.readonly-playback.v4"),
            Symbol.for("lyrveil.netease.readonly-playback.v3"),
            Symbol.for("lyrveil.netease.readonly-playback.v2"),
            Symbol.for("lyrveil.netease.readonly-playback.v1")
          ];
          for (const legacyHelperKey of legacyHelperKeys) {
            const legacyHelper = globalThis[legacyHelperKey];
            if (legacyHelper === undefined) continue;
            try {
              if (legacyHelper && typeof legacyHelper.dispose === "function") {
                legacyHelper.dispose();
              }
            } catch (_) { }
            try { delete globalThis[legacyHelperKey]; } catch (_) { }
          }
          const existing = globalThis[helperKey];
          if (typeof existing === "function") {
            try { return await existing(); }
            catch (_) {
              try { if (typeof existing.dispose === "function") existing.dispose(); } catch (_) { }
              try { delete globalThis[helperKey]; } catch (_) { }
            }
          }

          const failed = message => ({
            schemaVersion: 1,
            source: "netease-cdp",
            capturedAt: new Date().toISOString(),
            ok: false,
            track: null,
            playback: null,
            error: message
          });
          const isObject = value => value !== null &&
            (typeof value === "object" || typeof value === "function");
          const own = (value, key) => isObject(value) ? value[key] : undefined;
          const firstDefined = values => values.find(value =>
            value !== undefined && value !== null && value !== "");
          const finiteNumber = value => {
            const number = typeof value === "number" ? value : Number(value);
            return Number.isFinite(number) ? number : null;
          };
          const simpleValue = value => {
            if (value === null || value === undefined) return null;
            if (["string", "number", "boolean"].includes(typeof value)) return value;
            if (isObject(value)) {
              for (const key of ["state", "status", "value", "name"]) {
                const nested = value[key];
                if (["string", "number", "boolean"].includes(typeof nested)) return nested;
              }
            }
            return String(value);
          };
          const expandedExports = exportsValue => {
            const values = [];
            if (!isObject(exportsValue)) return values;
            values.push(exportsValue);
            if (isObject(exportsValue.default)) values.push(exportsValue.default);
            try {
              const children = Object.values(exportsValue);
              if (children.length <= 80) {
                for (const child of children) if (isObject(child)) values.push(child);
              }
            } catch (_) { }
            return values;
          };
          const normalizeTimedPosition = (value, durationMs) => {
            const number = finiteNumber(value);
            if (number === null || number < 0) return null;
            // NetEase playprogress.current and seek.position are seconds.
            // Do not infer units from magnitude: 0.1 means 100 ms, not 0.1 ms.
            const milliseconds = Math.max(0, Math.round(number * 1000));
            return durationMs === null
              ? milliseconds
              : Math.min(milliseconds, durationMs);
          };

          let webpackRequire = null;
          const chunkNames = Object.keys(globalThis).filter(name =>
            name === "webpackJsonp" || name.startsWith("webpackJsonp"));

          for (const name of chunkNames) {
            const chunk = globalThis[name];
            if (!Array.isArray(chunk) ||
                typeof chunk.push !== "function" ||
                chunk.push === Array.prototype.push) continue;

            let captured = null;
            const chunkId = 900000000 + Math.floor(Math.random() * 90000000);
            const moduleId = chunkId + 100000000;
            try {
              // NetEase desktop currently uses Webpack 4's deferred-entry layout.
              // This matches the separately verified one-shot probe and avoids
              // trying an incompatible Webpack 5 runtime callback first.
              chunk.push([[chunkId], {
                [moduleId]: (_module, _exports, candidate) => { captured = candidate; }
              }, [[moduleId]]]);
            } catch (_) { captured = null; }

            if (captured && isObject(captured.c)) {
              webpackRequire = captured;
              try {
                delete captured.c[moduleId];
                if (isObject(captured.m)) delete captured.m[moduleId];
              } catch (_) { }
              break;
            }
          }

          if (!webpackRequire) {
            return failed("The NetEase page module runtime is unavailable.");
          }

          // NetEase 3.1.38 exposes the current absolute song position through
          // the read-only `playingProgress` facade used by its own automation
          // surface. Discover the backing observable from the module factory
          // instead of pinning a build-specific module id or export name.
          // This is only a startup/discontinuity anchor. Live absolute
          // play-progress events remain the media clock during playback.
          let startupProgressReader = null;
          try {
            for (const factory of Object.values(webpackRequire.m || {})) {
              let source = "";
              try { source = Function.prototype.toString.call(factory); }
              catch (_) { continue; }
              const getterMatch = source.match(
                /get\s+playingProgress\s*\(\)\s*\{[\s\S]{0,500}?current\s*:\s*([A-Za-z_$][\w$]*)\.([A-Za-z_$][\w$]*)\.getValue\(\)/);
              if (!getterMatch) continue;
              const alias = getterMatch[1];
              const exportName = getterMatch[2];
              const escapedAlias = alias.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
              const moduleMatch = source.match(new RegExp(
                "(?:^|[,;])\\s*" + escapedAlias +
                "\\s*=\\s*[A-Za-z_$][\\w$]*\\((\\d+)\\)"));
              if (!moduleMatch) continue;
              let moduleExports = null;
              try { moduleExports = webpackRequire(Number(moduleMatch[1])); }
              catch (_) { continue; }
              const observable = moduleExports && moduleExports[exportName];
              if (!observable || typeof observable.getValue !== "function") continue;
              startupProgressReader = () => finiteNumber(observable.getValue());
              const initialValue = startupProgressReader();
              if (initialValue !== null && initialValue >= 0) break;
              startupProgressReader = null;
            }
          } catch (_) { startupProgressReader = null; }

          let audioPlayer = null;
          let audioPlayerScore = -1;
          let storeFacade = null;
          const cacheEntries = Object.entries(webpackRequire.c || {});
          for (const [, moduleRecord] of cacheEntries) {
            for (const candidate of expandedExports(moduleRecord && moduleRecord.exports)) {
              const possibleAudio = isObject(candidate.AudioPlayer)
                ? candidate.AudioPlayer
                : candidate;
              const possibleAudioScore =
                (typeof possibleAudio.subscribePlayStatus === "function" ? 100 : 0) +
                (typeof possibleAudio.getPlaybackInfo === "function" ? 10 : 0) +
                (typeof possibleAudio.getPlayedTime === "function" ? 1 : 0);
              if (possibleAudioScore > audioPlayerScore) {
                audioPlayer = possibleAudio;
                audioPlayerScore = possibleAudioScore;
              }
              if (!storeFacade && typeof candidate.getStore === "function") {
                storeFacade = candidate;
              }
            }
            // A subscriber plus direct playback information is already the best
            // useful combination. Weaker candidates must not end the scan early.
            if (audioPlayerScore >= 110 && storeFacade) break;
          }

          if (!audioPlayer && !storeFacade) {
            return failed("The NetEase playback modules are unavailable.");
          }
          if (!audioPlayer ||
              typeof audioPlayer.subscribePlayStatus !== "function") {
            return failed("The NetEase live playback subscription is unavailable.");
          }

          // getPlayedTime is not a media position. It is a monotonically
          // increasing amount of audio that has actually passed through the
          // player. It is used only to confirm that output resumed after a
          // seek. Prefer playedAudioTime and fall back to playedTime only when
          // NetEase omits the former.
          const extractAccumulatedAudioMilliseconds = response => {
            if (!isObject(response)) return null;
            if (own(response, "result") === false) return null;
            const variants = [response, own(response, "data")].filter(isObject);
            if (isObject(own(response, "result"))) {
              variants.push(own(response, "result"));
            }
            for (const key of ["playedAudioTime", "playedTime"]) {
              for (const variant of variants) {
                const seconds = finiteNumber(own(variant, key));
                if (seconds === null || seconds < 0 || seconds > 86400) continue;
                return Math.round(seconds * 1000);
              }
            }
            return null;
          };
          const readAccumulatedAudioMilliseconds = async identifiers => {
            if (!audioPlayer || typeof audioPlayer.getPlayedTime !== "function") {
              return null;
            }
            const attempted = new Set();
            for (const identifier of identifiers) {
              if (identifier === undefined || identifier === null || identifier === "") {
                continue;
              }
              const normalized = String(identifier);
              if (attempted.has(normalized)) continue;
              attempted.add(normalized);
              try {
                const response = await Promise.resolve(
                  audioPlayer.getPlayedTime.call(audioPlayer, { playId: identifier }));
                const milliseconds = extractAccumulatedAudioMilliseconds(response);
                if (milliseconds !== null) return milliseconds;
              } catch (_) { }
            }
            return null;
          };
          const readStartupProgressMilliseconds = durationMs => {
            if (typeof startupProgressReader !== "function") return null;
            try {
              const seconds = startupProgressReader();
              if (seconds === null || seconds < 0 || seconds > 86400) return null;
              const milliseconds = Math.max(0, Math.round(seconds * 1000));
              return durationMs === null ? milliseconds : Math.min(durationMs, milliseconds);
            } catch (_) { return null; }
          };

          // NetEase emits an exact absolute target on seek and then resumes
          // absolute play-progress callbacks. getPlayedTime is only a gate
          // proving that audio output has resumed; it is never paired with a
          // guessed seek-time baseline and never becomes the media position.
          const seekGate = {
            active: false,
            trackKey: null,
            playId: null,
            generation: 0,
            targetMs: null,
            observedAt: 0,
            playingObservedAt: 0,
            counterBaselineMs: null,
            lastCounterMs: null,
            firstProgressMs: null,
            lastProgressMs: null,
            firstProgressAt: 0,
            lastProgressAt: 0,
            stableSamples: 0
          };
          const seekTransitionIsProtected = () => seekGate.active
            && Date.now() - seekGate.observedAt <= 750;
          const resetSeekGate = () => {
            seekGate.active = false;
            seekGate.trackKey = null;
            seekGate.playId = null;
            seekGate.generation += 1;
            seekGate.targetMs = null;
            seekGate.observedAt = 0;
            seekGate.playingObservedAt = 0;
            seekGate.counterBaselineMs = null;
            seekGate.lastCounterMs = null;
            seekGate.firstProgressMs = null;
            seekGate.lastProgressMs = null;
            seekGate.firstProgressAt = 0;
            seekGate.lastProgressAt = 0;
            seekGate.stableSamples = 0;
          };
          const beginSeekGate = (targetSeconds, playId, observedAt) => {
            const targetMs = finiteNumber(targetSeconds) * 1000;
            if (!Number.isFinite(targetMs) || targetMs < 0) return false;
            const normalizedPlayId = playId === undefined || playId === null
              ? seekGate.playId
              : String(playId);
            seekGate.active = true;
            seekGate.playId = normalizedPlayId;
            seekGate.generation += 1;
            seekGate.targetMs = targetMs;
            seekGate.observedAt = observedAt;
            seekGate.playingObservedAt = 0;
            // The first counter read after the discontinuity is deliberately
            // the baseline. Reusing or estimating a pre-seek sample made the
            // permanent error depend on asynchronous request timing.
            seekGate.counterBaselineMs = null;
            seekGate.lastCounterMs = null;
            seekGate.firstProgressMs = null;
            seekGate.lastProgressMs = null;
            seekGate.firstProgressAt = 0;
            seekGate.lastProgressAt = 0;
            seekGate.stableSamples = 0;
            return true;
          };

          // Only NetEase's play-progress and seek notifications provide the
          // authoritative absolute media position.
          const liveTimeline = {
            active: true,
            playId: null,
            trackKey: null,
            position: null,
            observedAt: 0,
            eventType: null
          };
          let timelineRequiresFreshEvent = false;
          let lastStableStatus = null;
          let hasRunningProgress = false;
          let playStateRevision = 0;
          const normalizeStatus = (raw, isStoreState = false) => {
            const value = simpleValue(raw);
            if (value === null) return null;
            if (typeof value === "boolean") return value ? "playing" : "paused";
            const text = String(value).trim().toLowerCase();
            // Redux uses 2 for playing and 1 for paused. Native playstate uses the reverse.
            if (text === "1") return isStoreState ? "paused" : "playing";
            if (text === "2") return isStoreState ? "playing" : "paused";
            if (text === "0" || (isStoreState ? text === "-1" : ["3", "4"].includes(text))) return "stopped";
            if (["playing", "play", "started"].includes(text)) return "playing";
            if (["paused", "pause"].includes(text)) return "paused";
            if (["stopped", "stop", "ended", "end", "error", "null"].includes(text)) return "stopped";
            return null;
          };
          const rememberStatus = status => {
            if (status !== lastStableStatus || status !== "playing") hasRunningProgress = false;
            if (status !== null) lastStableStatus = status;
            if (status === "paused") seekGate.playingObservedAt = 0;
          };
          const observeGateProgress = (positionSeconds, playId, observedAt) => {
            if (!seekGate.active) return false;
            if (seekGate.playId !== null
                && playId !== null
                && playId !== undefined
                && String(seekGate.playId) !== String(playId)) {
              return false;
            }
            const positionMs = finiteNumber(positionSeconds) * 1000;
            if (!Number.isFinite(positionMs) || positionMs < 0) return false;
            // Transitional decoder callbacks can still describe the old
            // position. A decoder can also land on a keyframe before the target.
            const nearTarget = positionMs >= seekGate.targetMs - 1000
              && positionMs <= seekGate.targetMs + 2500;
            if (!nearTarget) return false;
            seekGate.playId ??= playId;
            if (seekGate.lastProgressMs === null) {
              seekGate.firstProgressMs = positionMs;
              seekGate.firstProgressAt = observedAt;
              seekGate.stableSamples = 1;
            } else {
              const elapsed = Math.max(1, observedAt - seekGate.lastProgressAt);
              const advanced = positionMs - seekGate.lastProgressMs;
              if (advanced >= -20 && advanced <= elapsed * 1.75 + 75) {
                seekGate.stableSamples += 1;
              } else {
                seekGate.firstProgressMs = positionMs;
                seekGate.firstProgressAt = observedAt;
                seekGate.stableSamples = 1;
              }
            }
            seekGate.lastProgressMs = positionMs;
            seekGate.lastProgressAt = observedAt;
            return true;
          };
          const clearLiveTimeline = () => {
            liveTimeline.playId = null;
            liveTimeline.trackKey = null;
            liveTimeline.position = null;
            liveTimeline.observedAt = 0;
            liveTimeline.eventType = null;
            hasRunningProgress = false;
            resetSeekGate();
          };
          const rememberTimeline = (type, payload) => {
            if (!liveTimeline.active) return;
            const playId = firstDefined([
              own(payload, "playId"), own(own(payload, "data"), "playId"),
              own(own(payload, "result"), "playId")
            ]);
            if (type === "playstate") {
              if (playId != null && liveTimeline.playId != null && String(playId) !== liveTimeline.playId) return;
              const variants = [payload, own(payload, "data"), own(payload, "result")];
              for (const variant of variants) {
                const state = isObject(variant)
                  ? firstDefined([variant.state, variant.playState, variant.playingState, variant.status, variant.isPlaying, variant.value])
                  : variant;
                const status = normalizeStatus(state);
                if (status === null) continue;
                if (status === "stopped" && seekTransitionIsProtected()) return;
                playStateRevision += 1;
                rememberStatus(status);
                return;
              }
              // An unrecognized transition still invalidates the wall-clock baseline.
              hasRunningProgress = false;
              return;
            }
            if (type !== "playprogress" && type !== "seek") return;
            const position = type === "seek"
              ? firstDefined([
                  own(payload, "position"), own(own(payload, "data"), "position"),
                  own(own(payload, "result"), "position"), own(payload, "current")
                ])
              : firstDefined([
                  own(payload, "current"), own(own(payload, "data"), "current"),
                  own(own(payload, "result"), "current"), own(payload, "position")
                ]);
            if (position === undefined || position === null) return;
            const numericPosition = finiteNumber(position);
            if (numericPosition === null || numericPosition < 0) return;
            const observedAt = Date.now();
            const eventPlayId = playId === undefined || playId === null
              ? liveTimeline.playId
              : String(playId);
            timelineRequiresFreshEvent = false;
            if (type === "seek") {
              if (!beginSeekGate(numericPosition, eventPlayId, observedAt)) return;
            } else {
              if (seekGate.active) {
                observeGateProgress(numericPosition, eventPlayId, observedAt);
                // Keep the exact seek target as the visible anchor until the
                // output counter proves that sound has resumed.
                return;
              }
              if (lastStableStatus === "playing" && hasRunningProgress
                  && liveTimeline.position !== null
                  && liveTimeline.observedAt > 0
                  && (liveTimeline.playId === null
                    || eventPlayId === null
                    || String(liveTimeline.playId) === String(eventPlayId))) {
                const expectedSeconds = liveTimeline.position
                  + Math.max(0, observedAt - liveTimeline.observedAt) / 1000;
                if (Math.abs(numericPosition - expectedSeconds) > 0.75) {
                  // Recover even if this NetEase build omits a seek callback.
                  if (beginSeekGate(numericPosition, eventPlayId, observedAt)) {
                    observeGateProgress(numericPosition, eventPlayId, observedAt);
                    liveTimeline.position = numericPosition;
                    liveTimeline.observedAt = observedAt;
                    liveTimeline.eventType = "seek";
                  }
                  return;
                }
              }
            }
            const normalizedPlayId = eventPlayId;
            if (playId !== undefined
                && liveTimeline.playId !== null
                && String(playId) !== String(liveTimeline.playId)) {
              // The next snapshot will bind this new event to the metadata that
              // owns it instead of carrying the previous song's track key over.
              liveTimeline.trackKey = null;
            }
            liveTimeline.playId = normalizedPlayId;
            liveTimeline.position = numericPosition;
            liveTimeline.observedAt = observedAt;
            liveTimeline.eventType = type;
            hasRunningProgress = type === "playprogress" && lastStableStatus === "playing";
          };
          const subscriptions = [];
          if (audioPlayer && typeof audioPlayer.subscribePlayStatus === "function") {
            for (const type of ["playprogress", "seek", "playstate"]) {
              const callback = payload => rememberTimeline(type, payload);
              try {
                const result = audioPlayer.subscribePlayStatus.call(
                  audioPlayer,
                  { type, callback });
                subscriptions.push({ type, callback, result });
              } catch (_) { }
            }
          }

          const readSnapshot = async () => {
            const revisionAtRead = playStateRevision;
            let state = null;
            if (storeFacade) {
              try {
                const storeOrState = await Promise.resolve(
                  storeFacade.getStore.call(storeFacade));
                state = storeOrState && typeof storeOrState.getState === "function"
                  ? storeOrState.getState()
                  : storeOrState;
              } catch (_) { }
            }

            const playing = own(state, "playing") || own(state, "player") ||
              own(state, "play") || {};
            const current = firstDefined([
              own(playing, "curPlaying"), own(playing, "currentPlaying"),
              own(playing, "currentTrack"), own(playing, "playingTrack"),
              own(state, "curPlaying"), own(state, "currentTrack")
            ]) || {};
            const track = own(current, "track") || own(current, "song") ||
              own(current, "resource") || current;
            const albumValue = firstDefined([
              own(track, "album"), own(track, "al"), own(current, "album")
            ]);
            const artistValue = firstDefined([
              own(track, "artists"), own(track, "ar"), own(track, "artist"),
              own(current, "artists"), own(current, "artist")
            ]);
            const artist = Array.isArray(artistValue)
              ? artistValue.map(item => firstDefined([
                  own(item, "name"), own(item, "artistName"), item
                ])).filter(Boolean).join(" / ")
              : firstDefined([
                  own(artistValue, "name"), own(artistValue, "artistName"), artistValue
                ]);
            const album = firstDefined([
              own(albumValue, "name"), own(albumValue, "albumName"), albumValue
            ]);
            const songId = firstDefined([
              own(current, "resourceId"), own(current, "songId"), own(track, "id"),
              own(track, "songId"), own(current, "id")
            ]);
            const resourceTrackId = firstDefined([
              own(playing, "resourceTrackId"), own(playing, "onlineResourceId"),
              own(current, "resourceId"), own(track, "id")
            ]);
            const playId = firstDefined([
              own(playing, "playId"), own(current, "playId"), own(state, "playId"),
              resourceTrackId
            ]);

            let playbackInfo = null;
            if (audioPlayer && playId !== undefined && playId !== null) {
              if (typeof audioPlayer.getPlaybackInfo === "function") {
                try {
                  playbackInfo = await Promise.resolve(
                    audioPlayer.getPlaybackInfo.call(audioPlayer, { playId }));
                } catch (_) { }
              }
            }

            // A counter reply may arrive after another click. Only its own seek
            // generation can use it to decide whether output has resumed.
            const counterGeneration = seekGate.generation;
            const accumulatedAudioMs = seekGate.active
              ? await readAccumulatedAudioMilliseconds([playId, resourceTrackId])
              : null;
            const sources = [
              ["playbackInfo", playbackInfo],
              ["playing", playing], ["current", current], ["track", track]
            ];
            const findNumber = keys => {
              for (const [sourceName, source] of sources) {
                if (!isObject(source)) continue;
                const variants = [source, own(source, "result"), own(source, "data")]
                  .filter(isObject);
                for (const variant of variants) {
                  for (const key of keys) {
                    const number = finiteNumber(own(variant, key));
                    if (number !== null) return { number, key, sourceName };
                  }
                }
              }
              return null;
            };
            const durationCandidate = findNumber([
              "durationMs", "resourceDurationMs", "duration", "resourceDuration",
              "totalTime", "length", "dt"
            ]);
            const normalizeDuration = candidate => {
              if (!candidate) return null;
              if (candidate.key.toLowerCase().includes("ms") || candidate.key === "dt") {
                return Math.max(0, Math.round(candidate.number));
              }
              return Math.max(0, Math.round(
                candidate.number <= 43200 ? candidate.number * 1000 : candidate.number));
            };
            const durationMs = normalizeDuration(durationCandidate);
            const currentTrackKey = `${String(songId)}|${String(resourceTrackId)}`;
            const hasLiveTimeline = liveTimeline.position !== null;
            if (hasLiveTimeline
                && liveTimeline.trackKey !== null
                && liveTimeline.trackKey !== currentTrackKey
                && !seekTransitionIsProtected()) {
              // Media metadata can switch before playId does. Never allow the
              // previous song's last progress event to survive that transition.
              clearLiveTimeline();
            }
            if (liveTimeline.position !== null && liveTimeline.trackKey === null) {
              liveTimeline.trackKey = currentTrackKey;
            }
            const currentPlayId = String(firstDefined([playId, resourceTrackId]));
            if (liveTimeline.position !== null && liveTimeline.playId === null) {
              liveTimeline.playId = currentPlayId;
            } else if (liveTimeline.playId !== null
                && String(liveTimeline.playId) !== currentPlayId
                && String(liveTimeline.playId) !== String(resourceTrackId)
                && !seekTransitionIsProtected()) {
              clearLiveTimeline();
            }
            const eventPlayIdMatches = liveTimeline.playId === null
              || String(liveTimeline.playId) === String(playId)
              || String(liveTimeline.playId) === String(resourceTrackId);
            let eventPositionMs = eventPlayIdMatches && liveTimeline.observedAt > 0
              ? normalizeTimedPosition(liveTimeline.position, durationMs)
              : null;
            // The semantic playingProgress getter is an absolute read-only
            // startup anchor. Live seek/playprogress remains authoritative when
            // present, and getPlayedTime is never treated as an absolute value.
            const startupProgressMs = readStartupProgressMilliseconds(durationMs);
            let positionMs = eventPositionMs ?? startupProgressMs;

            const storeStatus = firstDefined([
              own(playing, "playingState"), own(playing, "playState"),
              own(playing, "state"), own(playing, "isPlaying")
            ]);
            const nativeStatus = firstDefined([
              own(playbackInfo, "playingState"), own(playbackInfo, "playState"),
              own(playbackInfo, "state")
            ]);
            let status = revisionAtRead !== playStateRevision
              ? lastStableStatus
              : normalizeStatus(storeStatus, true) ?? normalizeStatus(nativeStatus);
            if (status === "stopped"
                && seekTransitionIsProtected()
                && (lastStableStatus === "playing" || lastStableStatus === "paused")) {
              // NetEase can expose one transitional Stop between the seek
              // callback and the first decoder progress. Keep the previous
              // semantic state so the exact target remains frozen instead of
              // starting a fallback clock early.
              status = lastStableStatus;
            }
            rememberStatus(status);
            const isPlaying = status === null ? null : status === "playing";

            if (status === "stopped") {
              clearLiveTimeline();
              eventPositionMs = null;
              positionMs = null;
            }

            let gateTimedOutWithoutProgress = false;
            if (seekGate.active && status !== "stopped") {
              if (seekGate.trackKey !== null
                  && seekGate.trackKey !== currentTrackKey
                  && !seekTransitionIsProtected()) {
                resetSeekGate();
              } else {
                seekGate.trackKey = currentTrackKey;
              }
            }
            if (seekGate.active && counterGeneration !== seekGate.generation) {
              positionMs = seekGate.targetMs;
            } else if (seekGate.active && status === "paused") {
              // A paused seek stays at the exact requested position. Capturing
              // a counter baseline is harmless, but paused wall time never
              // consumes the output-resume timeout.
              seekGate.playingObservedAt = 0;
              if (accumulatedAudioMs !== null) {
                seekGate.counterBaselineMs = accumulatedAudioMs;
                seekGate.lastCounterMs = accumulatedAudioMs;
              }
              positionMs = seekGate.targetMs;
            } else if (seekGate.active && status === "playing") {
              const now = Date.now();
              if (seekGate.playingObservedAt === 0) {
                seekGate.playingObservedAt = now;
              }
              if (accumulatedAudioMs !== null) {
                if (seekGate.lastCounterMs !== null
                    && accumulatedAudioMs + 100 < seekGate.lastCounterMs) {
                  // A playback-instance counter reset starts a new baseline;
                  // it does not change the absolute media position.
                  seekGate.counterBaselineMs = accumulatedAudioMs;
                }
                if (seekGate.counterBaselineMs === null) {
                  seekGate.counterBaselineMs = accumulatedAudioMs;
                }
                seekGate.lastCounterMs = accumulatedAudioMs;
              }

              const recentProgress = seekGate.lastProgressAt > 0
                && now - seekGate.lastProgressAt <= 500;
              const progressSpanMs = seekGate.firstProgressAt > 0
                ? seekGate.lastProgressAt - seekGate.firstProgressAt
                : 0;
              const progressAdvanceMs = seekGate.firstProgressMs !== null
                && seekGate.lastProgressMs !== null
                ? seekGate.lastProgressMs - seekGate.firstProgressMs
                : 0;
              const progressStable = recentProgress
                && seekGate.stableSamples >= 2
                && progressSpanMs >= 30
                && progressAdvanceMs >= 10;
              const audioAdvanced = accumulatedAudioMs !== null
                && seekGate.counterBaselineMs !== null
                && accumulatedAudioMs - seekGate.counterBaselineMs >= 15;
              const counterUnavailableFallback = accumulatedAudioMs === null
                && seekGate.stableSamples >= 3
                && progressSpanMs >= 80
                && progressAdvanceMs >= 20;
              if (progressStable
                  && (audioAdvanced || counterUnavailableFallback)) {
                // The output gate is open. Re-anchor to the newest absolute
                // progress callback from this seek generation. Counter request
                // latency can no longer create a permanent position error.
                const acceptedPositionMs = seekGate.lastProgressMs;
                const acceptedObservedAt = seekGate.lastProgressAt;
                const acceptedPlayId = seekGate.playId;
                resetSeekGate();
                liveTimeline.playId = acceptedPlayId;
                liveTimeline.position = acceptedPositionMs / 1000;
                liveTimeline.observedAt = acceptedObservedAt;
                liveTimeline.eventType = "playprogress";
                eventPositionMs = normalizeTimedPosition(
                  liveTimeline.position, durationMs);
                hasRunningProgress = true;
                positionMs = eventPositionMs;
                timelineRequiresFreshEvent = false;
              } else {
                positionMs = seekGate.targetMs;
                if (now - seekGate.playingObservedAt > 4500) {
                  // A missing post-seek absolute progress cannot be repaired by
                  // an accumulated counter. Fail closed until a fresh event.
                  timelineRequiresFreshEvent = true;
                  clearLiveTimeline();
                  eventPositionMs = null;
                  gateTimedOutWithoutProgress = true;
                }
              }
            }

            const title = firstDefined([
              own(track, "name"), own(track, "title"), own(track, "songName"),
              own(current, "name"), own(current, "title")
            ]);
            const timelineEventAgeMs = liveTimeline.observedAt > 0
              ? Math.max(0, Date.now() - liveTimeline.observedAt)
              : 0;
            // A dead subscription must not pin a playing song to an old event
            // forever. Fail this optional bridge sample so Windows' native
            // estimated clock can take over; never treat getPlayedTime as an
            // absolute position because it is accumulated audio-play time.
            const playingTimelineIsStale = status === "playing"
              && !seekGate.active
              && liveTimeline.observedAt > 0
              && timelineEventAgeMs > 3000;
            const output = {
              schemaVersion: 1,
              source: "netease-cdp",
              capturedAt: new Date().toISOString(),
              ok: !!(title && songId !== undefined && positionMs !== null &&
                durationMs !== null && status !== null && !playingTimelineIsStale
                && !gateTimedOutWithoutProgress
                && !timelineRequiresFreshEvent),
              track: {
                id: songId === undefined ? null : simpleValue(songId),
                title: title === undefined ? null : simpleValue(title),
                artist: artist === undefined ? null : simpleValue(artist),
                album: album === undefined ? "" : simpleValue(album)
              },
              playback: {
                status,
                isPlaying,
                positionMs,
                durationMs,
                playId: playId === undefined ? null : simpleValue(playId),
                timelineEventType: liveTimeline.eventType,
                timelineEventAgeMs,
                timelineFrozen: seekGate.active,
                seekLeadCorrectionMs: 0,
                timelineClock: eventPositionMs !== null
                  ? "absolute-event"
                  : startupProgressMs !== null
                    ? "startup-progress"
                    : "unavailable"
              },
              error: null
            };
            if (!output.ok) output.error = timelineRequiresFreshEvent
              ? "Seek settling timed out without a progress anchor."
              : playingTimelineIsStale
                ? "The live playback timeline is stale."
              : "Playback information is incomplete.";
            return output;
          };

          try {
            Object.defineProperty(globalThis, helperKey, {
              value: readSnapshot,
              configurable: true,
              enumerable: false,
              writable: false
            });
          } catch (_) { }
          readSnapshot.dispose = () => {
            liveTimeline.active = false;
            clearLiveTimeline();
            // NetEase 3.1 exposes inconsistent unsubscribe coverage across
            // event types. Marking callbacks inactive is the reliable cleanup;
            // attempt an unsubscribe only when the matching method accepts it.
            for (const subscription of subscriptions) {
              try {
                if (typeof subscription.result === "function") subscription.result();
              } catch (_) { }
              if (audioPlayer && typeof audioPlayer.unSubscribePlayStatus === "function") {
                try {
                  audioPlayer.unSubscribePlayStatus.call(audioPlayer, {
                    type: subscription.type,
                    callback: subscription.callback
                  });
                } catch (_) { }
              }
            }
          };
          return await readSnapshot();
        })()
        """;
}
