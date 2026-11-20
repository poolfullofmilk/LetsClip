// The Editor's Player, Timeline And Crop Join The Page's Calls
Object.assign(
    window.letsClip,
    (() => {
        // The Shortest Selection, Matching TrimRange.Step
        const step = 0.1;

        // A Shown Edge Sits Half A Millisecond Inside Its Frame
        const inside = 0.0005;
        let state = null;

        // The Editor Attaches Only A Clip Longer Than Zero
        const duration = () => state.duration;
        const clamp = (time) => Math.min(duration(), Math.max(0, time));
        // Whole Screen Pixels Of The Shown Window, Edges Meet Exactly
        const percent = (time) => {
            const { start, end } = state.view;
            return `round(${Math.min(100, Math.max(0, ((time - start) / (end - start)) * 100))}%, var(--device-pixel))`;
        };
        const outside = (time) => time < state.view.start - 1e-6 || time > state.view.end + 1e-6;

        // The Playhead Follows Each Frame, Blazor Hears Ten A Second
        const loop = () => {
            if (!state) return;
            state.frame = 0;
            const { video, playhead, dotnet } = state;
            if (state.stopAt !== null && !video.paused && video.currentTime >= state.stopAt) {
                video.pause();
                video.currentTime = Math.max(0, state.stopAt - inside);
            }
            const time = video.currentTime;
            // Playing Past A Zoomed Window Turns The Page
            if (!video.paused && outside(time)) follow(time);
            playhead.style.left = percent(time);
            playhead.style.visibility = outside(time) ? "hidden" : "";
            const now = performance.now();
            if (video.paused !== state.paused || (time !== state.sent && now - state.reported > 100)) {
                state.reported = now;
                state.sent = time;
                state.paused = video.paused;
                dotnet.invokeMethodAsync("OnPlayerTime", time, video.paused).catch(() => {});
            }
            // A Paused, Drawn Video Stops The Loop, Saving CPU
            if (!video.paused || time !== state.sent) state.frame = requestAnimationFrame(loop);
        };

        // Play, Pause And Every Seek Start The Loop Again
        const wakers = ["play", "pause", "seeking", "seeked"];
        const kick = () => {
            if (state && !state.frame) state.frame = requestAnimationFrame(loop);
        };

        // Opening A Clip Plays It, A Cut Or Restore Waits
        const attach = (video, source, playhead, timeline, dotnet, length, start, end, volume, muted, separator, frames, sizes, keys, autoPlay) => {
            detach();
            // Detach Removed The Source Behind Blazor's Back
            if (video.getAttribute("src") !== source) video.setAttribute("src", source);
            // Frame Starts, Then The Clip's End
            const bounds = [...new Set(frames.map((time) => Math.max(0, time)).filter((time) => time < length)), length].sort((first, second) => first - second);
            // One Signal Takes Every Listener Off On Detach
            const listening = new AbortController();
            const { signal } = listening;
            // Running Totals Of Frame Bytes, A Copy Weighs At Once
            const totals = [0];
            for (const size of sizes) totals.push(totals[totals.length - 1] + size);
            const packets = { times: frames, totals, keys };
            state = { video, playhead, timeline, dotnet, separator, bounds, packets, listening, duration: length, view: { start: 0, end: length }, viewTimer: 0, reported: 0, sent: -1, paused: true, stopAt: null, frame: 0, drag: null };
            // Points Saved Before Snapping Move Onto Their Frames
            const range = { start, end };
            setEdge(range, "start", start);
            setEdge(range, "end", end);
            paint(range);
            if (range.start !== start || range.end !== end) commit(range);
            // Read Before The Seek, Which Hides Data Already There
            const hadData = video.readyState >= 2;
            video.currentTime = range.start + inside;
            setVolume(volume, muted);
            const failed = () => {
                release(video);
                dotnet.invokeMethodAsync("OnPlayerError").catch(() => {});
            };
            const loaded = () => {
                dotnet.invokeMethodAsync("OnPlayerReady").catch(() => {});
                if (autoPlay) play(range.start, range.end);
                // The Held Frame Lets Go Once The Right One Shows
                if (video.seeking) video.addEventListener("seeked", () => release(video), { signal, once: true });
                else release(video);
            };
            video.addEventListener("error", failed, { signal });
            video.addEventListener("loadeddata", loaded, { signal, once: true });
            if (hadData || video.readyState >= 2) loaded();
            if (video.error) failed();
            video.addEventListener("click", toggle, { signal });
            video.addEventListener("dblclick", fullScreen, { signal });
            // The Crop Follows The Picture, Dragged To Move Or Size
            video.parentElement.addEventListener("pointerdown", cropDown, { signal });
            cropSizes.observe(video);
            timeline.addEventListener("pointerdown", down, { signal });
            timeline.addEventListener("pointermove", move, { signal });
            timeline.addEventListener("pointerup", up, { signal });
            timeline.addEventListener("pointercancel", up, { signal });
            timeline.addEventListener("keydown", key, { signal });
            timeline.addEventListener("wheel", wheel, { signal, passive: false });
            for (const name of wakers) video.addEventListener(name, kick, { signal });
            kick();
        };

        const frozenOf = (video) => video.parentElement.querySelector(".player-frozen");
        const release = (video) => delete frozenOf(video).dataset.held;

        // The Frame On Screen Stays While The File Changes
        const hold = (video) => {
            if (video.readyState < 2) return;
            const canvas = frozenOf(video);
            const box = video.getBoundingClientRect();
            canvas.width = video.videoWidth;
            canvas.height = video.videoHeight;
            canvas.getContext("2d").drawImage(video, 0, 0);
            canvas.style.width = `${box.width}px`;
            canvas.style.height = `${box.height}px`;
            canvas.dataset.held = "";
        };

        // Playback Stops And Releases The File When The Editor Closes
        const detach = (holdFrame) => {
            if (!state) return;
            cancelAnimationFrame(state.frame);
            state.listening.abort();
            const { video } = state;
            cropSizes.unobserve(video);
            if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
            if (holdFrame) hold(video);
            video.pause();
            video.removeAttribute("src");
            video.load();
            state = null;
        };

        // The Kept Range Lives In The Handles, Millisecond Exact
        const handleOf = (edge) => state.timeline.querySelector(`[data-handle="${edge}"]`);
        const readRange = () => ({ start: parseFloat(handleOf("start").dataset.time), end: parseFloat(handleOf("end").dataset.time) });

        // The Last Frame Edge At Or Before A Time
        const floorIndex = (time) => {
            const { bounds } = state;
            let low = 0;
            let high = bounds.length - 1;
            while (low < high) {
                const middle = (low + high + 1) >> 1;
                if (bounds[middle] <= time + 1e-6) low = middle;
                else high = middle - 1;
            }
            return low;
        };

        // Nearest Frame Edge Within Half A Step, Else The Time
        const fit = (time, low, high) => {
            const clamped = Math.min(high, Math.max(low, time));
            const index = floorIndex(clamped);
            const near = [state.bounds[index], state.bounds[index + 1]]
                .filter((bound) => bound !== undefined && bound >= low - 1e-6 && bound <= high + 1e-6 && Math.abs(bound - clamped) <= step / 2)
                .sort((first, second) => Math.abs(first - clamped) - Math.abs(second - clamped))[0];
            return near ?? Math.round(clamped * 1000) / 1000;
        };

        const setEdge = (range, edge, time) => {
            // Too Short To Trim, The Whole Clip Stays
            if (duration() <= step) return;
            if (edge === "start") range.start = fit(time, 0, range.end - step);
            else range.end = fit(time, range.start + step, duration());
        };

        // Start Shows Its First Frame, End The Last One Kept
        const showEdge = (range, edge) => {
            state.video.currentTime = edge === "start" ? range.start + inside : Math.max(range.start, range.end - inside);
        };

        // A Drag Paints The Handles, Blazor Hears Once It Settles
        const paint = ({ start, end }) => {
            const [before, after] = state.timeline.querySelectorAll(".timeline-shade");
            const selection = state.timeline.querySelector(".timeline-selection");
            before.style.width = percent(start);
            after.style.left = percent(end);
            selection.style.left = percent(start);
            selection.style.width = `calc(${percent(end)} - ${percent(start)})`;
            // Within Half A Pixel Of The Clip's End, Frame Colour
            const { view } = state;
            const half = (view.end - view.start) / state.timeline.clientWidth / devicePixelRatio / 2;
            state.timeline.toggleAttribute("data-at-start", view.start <= 0 && start < half);
            state.timeline.toggleAttribute("data-at-end", view.end >= duration() && duration() - end < half);
            handleOf("start").style.left = percent(start);
            handleOf("end").style.left = percent(end);
            handleOf("start").style.visibility = outside(start) ? "hidden" : "";
            handleOf("end").style.visibility = outside(end) ? "hidden" : "";
            // The Handles Stay The One Truth Between Inputs
            handleOf("start").dataset.time = start;
            handleOf("end").dataset.time = end;
            // Playing On Stops At The End Point As It Stands
            if (state.stopAt !== null) state.stopAt = end;
            showTimes({ start, end });
        };

        // Written Like Formatter.FormatDuration, 1m30,5s
        const formatTime = (seconds) => {
            const tenths = Math.round(Math.max(0, seconds) * 10);
            const hours = Math.floor(tenths / 36000);
            const minutes = Math.floor(tenths / 600) % 60;
            const rest = (tenths % 600) / 10;
            const text = (hours ? `${hours}h` : "") + (minutes ? `${minutes}m` : "");
            return rest > 0 || !text ? `${text}${String(rest).replace(".", state.separator)}s` : text;
        };

        // Start, End And Length Follow A Drag Live
        const showTimes = ({ start, end }) => {
            for (const [edge, time] of [["Start", start], ["End", end]]) {
                const field = document.querySelector(`.trim-field[data-edge="${edge}"]`);
                const input = field?.querySelector("input");
                if (!input || input === document.activeElement) continue;
                input.value = formatTime(time);
                field.dataset.seconds = String(Math.round(time * 10) / 10).replace(".", state.separator);
            }
            // Length, And A Copy's Size, Struck Live Once They Differ
            showValue(document.querySelector("[data-length]"), formatTime(end - start));
            const size = document.querySelector("[data-copy-audio]");
            if (size) showValue(size, formatSize(copyBytes(start, end, size.dataset)));
        };

        // Weighed Like SizeBudget.CopyBytes: From The Keyframe Before The Seek
        const copyBytes = (start, end, { copyAudio, copyFile, copyWhole, copyVideo }) => {
            // The Whole Clip With Every Track Is The File Itself
            const file = Number(copyFile);
            if (copyWhole !== undefined && end - start >= duration() - step / 2) return file;
            const soundPerSecond = Number(copyAudio);
            const video = copyVideo !== undefined;
            const { times, totals, keys } = state.packets;
            // No Packet List, A Share Like ClipExporter.CopyBytes
            if (!times.length) return Math.min(file, Math.floor(video ? (file * (end - start)) / duration() : (end - start) * 16000));
            const seek = Math.max(0, start - 0.001);
            const stop = seek + (end - start);
            let first = 0;
            for (const key of keys) {
                if (times[key] > seek) break;
                first = key;
            }
            // The First Frame At Or After The Stop
            let low = first;
            let high = times.length;
            while (low < high) {
                const middle = (low + high) >> 1;
                if (times[middle] < stop) low = middle + 1;
                else high = middle;
            }
            // The Sound Is Copied From That Keyframe On Too
            const lead = times.length ? Math.max(0, seek - times[first]) : 0;
            const sound = Math.round((end - start + lead) * soundPerSecond);
            // 12 Index Bytes A Packet, Never More Than The Clip
            const pictures = video ? totals[low] - totals[first] + 12 * (low - first) : 0;
            return Math.min(file, pictures + sound + 1000);
        };

        // Its Own Text Node, Which Blazor Keeps Writing To
        const showValue = (value, text) => {
            if (!value?.firstChild) return;
            value.firstChild.nodeValue = text;
            const row = value.parentElement;
            row.toggleAttribute("data-changed", text !== row.querySelector("s")?.textContent.trim());
        };

        // Decimal Units Without ,0, Like Formatter.FormatSize
        const formatSize = (bytes) =>
            (bytes >= 1e9 ? `${Number((bytes / 1e9).toFixed(2))} GB` : bytes >= 1e6 ? `${Number((bytes / 1e6).toFixed(1))} MB` : `${Math.max(1, Math.round(bytes / 1e3))} KB`).replace(".", state.separator);

        const commit = (range) => state.dotnet.invokeMethodAsync("OnRange", range.start, range.end).catch(() => {});

        // Arrows Step 0.1 s, Shift 1 s, Held Keys Build
        const key = (event) => {
            const handle = event.target instanceof Element ? event.target.closest("[data-handle]") : null;
            if (!state || !handle) return;
            const edge = handle.dataset.handle;
            const range = readRange();
            const jump = event.shiftKey ? 1 : step;
            const time = { ArrowLeft: range[edge] - jump, ArrowDown: range[edge] - jump, ArrowRight: range[edge] + jump, ArrowUp: range[edge] + jump, Home: 0, End: duration() }[event.key];
            if (time === undefined) return;
            event.preventDefault();
            delete handle.dataset.grabbed;
            setEdge(range, edge, time);
            paint(range);
            showEdge(range, edge);
            commit(range);
        };

        // Unclamped, So A Handle Grabbed Anywhere Reaches Both Ends
        const timeAt = (event) => {
            const bounds = state.timeline.getBoundingClientRect();
            const { start, end } = state.view;
            return start + ((event.clientX - bounds.left) / bounds.width) * (end - start);
        };

        // The Wheel Zooms Around The Pointer, Shift Or Sideways Pans
        const wheel = (event) => {
            if (!state) return;
            event.preventDefault();
            const { view } = state;
            const span = view.end - view.start;
            if (event.shiftKey || Math.abs(event.deltaX) > Math.abs(event.deltaY)) {
                const delta = event.deltaX || event.deltaY;
                return setView(view.start + (delta / state.timeline.clientWidth) * span, span);
            }
            // Down To A Sixtieth Of The Clip, Never Below Five
            const smallest = Math.min(duration(), Math.max(5, duration() / 60));
            const next = Math.min(duration(), Math.max(smallest, span * (event.deltaY < 0 ? 2 / 3 : 3 / 2)));
            if (next === span) return;
            const anchor = timeAt(event);
            setView(anchor - ((anchor - view.start) * next) / span, next);
        };

        // Painted At Once, Blazor Told Once It Settles
        const setView = (start, span) => {
            const from = Math.min(Math.max(0, start), duration() - span);
            state.view = { start: from, end: from + span };
            paint(readRange());
            const time = state.video.currentTime;
            state.playhead.style.left = percent(time);
            state.playhead.style.visibility = outside(time) ? "hidden" : "";
            strips.forEach(paintStrip);
            // A Bar Shows Where The Window Sits In The Clip
            const bar = state.timeline.querySelector(".timeline-zoom");
            bar.style.display = span >= duration() - 0.001 ? "none" : "";
            bar.style.left = `${(from / duration()) * 100}%`;
            bar.style.width = `${(span / duration()) * 100}%`;
            clearTimeout(state.viewTimer);
            state.viewTimer = setTimeout(() => state?.dotnet.invokeMethodAsync("OnView", state.view.start, state.view.end).catch(() => {}), 250);
        };

        const follow = (time) => {
            const span = state.view.end - state.view.start;
            setView(time - span / 10, span);
        };

        // A Handle Drags From Where It Was Grabbed, Else Seeks
        const down = (event) => {
            if (!state || event.button !== 0) return;
            const handle = event.target instanceof Element ? event.target.closest("[data-handle]") : null;
            state.drag = handle ? handle.dataset.handle : "seek";
            state.range = readRange();
            state.moved = false;
            state.grip = handle ? timeAt(event) - state.range[state.drag] : 0;
            state.timeline.setPointerCapture(event.pointerId);
            event.preventDefault();
            if (!handle) {
                state.video.currentTime = clamp(timeAt(event));
                return;
            }
            // Focused For Arrow Keys, Ringed Only After A Key
            handle.dataset.grabbed = "";
            handle.addEventListener("blur", () => delete handle.dataset.grabbed, { once: true });
            handle.focus();
        };

        const apply = (time) => {
            if (state.drag === "seek") {
                state.video.currentTime = clamp(time);
                return;
            }
            setEdge(state.range, state.drag, time);
            paint(state.range);
            showEdge(state.range, state.drag);
        };

        // Pointer Moves Already Arrive Once Per Frame
        const move = (event) => {
            if (!state?.drag) return;
            state.moved = true;
            apply(timeAt(event) - state.grip);
        };

        const up = (event) => {
            if (!state?.drag) return;
            if (state.moved) {
                apply(timeAt(event) - state.grip);
                if (state.drag !== "seek") commit(state.range);
            }
            state.drag = null;
        };

        // From The Playhead, Stopping At The End Point Before It
        const play = (start, end) => {
            if (!state) return;
            const { video } = state;
            const time = video.currentTime;
            // Resting On Either End Starts Over From Cut Start
            const atEnd = Math.abs(time - end) < 0.05 || time >= duration() - 0.05;
            if (atEnd) video.currentTime = start + inside;
            state.stopAt = !atEnd && time > end ? null : end;
            video.play().catch(() => {});
        };

        const pause = () => state?.video.pause();

        // Whole Frames Across The Strip, Each The Moment Under It
        const strips = new Set();
        const paintStrip = (canvas) => {
            if (!canvas.isConnected) {
                strips.delete(canvas);
                stripSizes.unobserve(canvas);
                return;
            }
            const image = canvas.stripImage;
            if (!image?.naturalWidth) return;
            const { clientWidth: width, clientHeight: height } = canvas;
            const pad = parseFloat(getComputedStyle(canvas.parentElement).paddingLeft) || 0;
            const cells = Number(canvas.dataset.cells);
            const cellWidth = image.naturalWidth / cells;
            // Zoomed, Each Tile Is A Moment Of The Shown Window
            const view = state?.view;
            const length = state?.duration;
            const zoom = canvas.zoom;
            const zoomed = view && zoom?.image.naturalWidth && (!zoom.wave || zoom.wave.naturalWidth) && zoom.start <= view.start + 0.001 && zoom.end >= view.end - 0.001 ? zoom : null;
            const count = Math.max(1, Math.round(width / ((height * cellWidth) / image.naturalHeight)));
            canvas.width = Math.round(width * devicePixelRatio);
            canvas.height = Math.round(height * devicePixelRatio);
            const context = canvas.getContext("2d");
            context.imageSmoothingQuality = "high";
            for (let index = 0; index < count; index++) {
                // Times Run Over The Track, Between The Handles
                const centre = ((index + 0.5) * width) / count;
                const share = Math.min(Math.max((centre - pad) / Math.max(1, width - 2 * pad), 0), 1);
                const time = view && length ? view.start + share * (view.end - view.start) : share;
                const source = zoomed ? zoomed.image : image;
                const fraction = zoomed ? (time - zoomed.start) / (zoomed.end - zoomed.start) : view && length ? time / length : share;
                const cell = Math.min(cells - 1, Math.max(0, Math.floor(fraction * cells)));
                const sourceWidth = source.naturalWidth / cells;
                const left = Math.round((index * canvas.width) / count);
                const right = Math.round(((index + 1) * canvas.width) / count);
                context.drawImage(source, cell * sourceWidth, 0, sourceWidth, source.naturalHeight, left, 0, right - left, canvas.height);
            }

            // The Sound Across The Strip's Middle, Timed Between The Handles
            const wave = canvas.waveImage;
            if (!wave?.naturalWidth) return;
            const band = Math.round(canvas.height * 0.55);
            const top = Math.round((canvas.height - band) / 2);
            const side = Math.round(pad * devicePixelRatio);
            const shade = context.createLinearGradient(0, top, 0, top + band);
            shade.addColorStop(0, "transparent");
            shade.addColorStop(0.5, getComputedStyle(canvas).getPropertyValue("--shade"));
            shade.addColorStop(1, "transparent");
            context.fillStyle = shade;
            context.fillRect(0, top, canvas.width, band);

            // White Lightens, The Picture's Black Leaves The Frames Alone
            context.globalCompositeOperation = "screen";
            context.globalAlpha = 0.7;
            if (zoomed?.wave) context.drawImage(zoomed.wave, side, top, canvas.width - 2 * side, band);
            else if (view && length) {
                // The Whole Clip's Outline, Its Shown Window Stretched
                const from = (view.start / length) * wave.naturalWidth;
                const across = ((view.end - view.start) / length) * wave.naturalWidth;
                context.drawImage(wave, from, 0, across, wave.naturalHeight, side, top, canvas.width - 2 * side, band);
            } else context.drawImage(wave, side, top, canvas.width - 2 * side, band);
            context.globalAlpha = 1;
            context.globalCompositeOperation = "source-over";
        };
        const stripSizes = new ResizeObserver((entries) => entries.forEach((entry) => paintStrip(entry.target)));
        addEventListener("resize", () => strips.forEach(paintStrip));
        const showStrip = (canvas) => {
            canvas.waveImage = null;
            canvas.zoom = null;
            const image = new Image();
            image.onload = () => paintStrip(canvas);
            image.src = canvas.dataset.src;
            canvas.stripImage = image;
            strips.add(canvas);
            stripSizes.observe(canvas);
        };

        const showWave = (canvas, url) => {
            canvas.waveImage = null;
            if (!url) return paintStrip(canvas);
            const wave = new Image();
            wave.onload = () => canvas.waveImage === wave && paintStrip(canvas);
            wave.src = url;
            canvas.waveImage = wave;
        };

        // A Zoomed Window's Strip And Outline, Drawn Once Loaded
        const showZoom = (canvas, stripUrl, waveUrl, start, end) => {
            const zoom = { start, end, image: new Image(), wave: waveUrl ? new Image() : null };
            const loaded = () => canvas.zoom === zoom && paintStrip(canvas);
            zoom.image.onload = loaded;
            zoom.image.src = stripUrl;
            if (zoom.wave) {
                zoom.wave.onload = loaded;
                zoom.wave.src = waveUrl;
            }
            canvas.zoom = zoom;
        };

        // A Typed Or Marked Point Moves Like A Drag
        const place = (edge, time) => {
            if (!state) return;
            const range = readRange();
            setEdge(range, edge, time);
            paint(range);
            showEdge(range, edge);
            commit(range);

            // The Typed Field Shows Where It Landed, Focus Kept
            const input = document.querySelector(`.trim-field[data-edge="${edge === "start" ? "Start" : "End"}"] input`);
            if (input) input.value = formatTime(range[edge]);
        };

        // A Field's Arrows Step Its Point Like A Handle's Keys
        const stepEdge = (edge, direction) => state && place(edge, readRange()[edge] + direction * step);

        // The Frame On Screen Starts The Clip Or Ends It
        const mark = (edge) => {
            if (!state) return;
            const { bounds, video } = state;
            const index = Math.min(floorIndex(video.currentTime), bounds.length - 2);
            const frame = edge === "start" ? bounds[index] : bounds[index + 1];
            place(edge, frame !== undefined && Math.abs(frame - video.currentTime) <= step / 2 ? frame : video.currentTime);
        };

        const setVolume = (volume, muted) => {
            if (!state) return;
            state.video.volume = volume;
            state.video.muted = muted;
        };

        // Full Screen Shows The Clip With The Browser's Controls
        const fullScreen = () => {
            if (!state) return;
            if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
            else state.video.requestFullscreen().catch(() => {});
        };

        // A Click Plays From Where The Handles Say
        const toggle = () => {
            if (!state || document.fullscreenElement) return;
            if (!state.video.paused) return pause();
            const { start, end } = readRange();
            play(start, end);
        };

        document.addEventListener("fullscreenchange", () => {
            if (state) state.video.controls = document.fullscreenElement === state.video;
        });

        // Escape Leaves Full Screen, Browser Keys Are Off
        window.addEventListener("keydown", (event) => {
            if (event.key === "Escape" && document.fullscreenElement) document.exitFullscreen().catch(() => {});
        });

        // While Picking A Hotkey Every Press Goes To The Page
        let stopKeys = () => {};
        const watchKeys = (dotnet) => {
            stopKeys();
            const done = (keep) => dotnet.invokeMethodAsync("OnKeysDone", keep).catch(() => {});
            const listener = (event) => {
                if (["Control", "Shift", "Alt", "Meta"].includes(event.key)) return;
                event.preventDefault();
                event.stopPropagation();
                const plain = !event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey;
                if (plain && event.key === "Escape") done(false);
                else if (plain && event.key === "Enter") done(true);
                else dotnet.invokeMethodAsync("OnKeys", event.code, event.key, event.ctrlKey, event.altKey, event.shiftKey, event.metaKey).catch(() => {});
            };
            const listening = new AbortController();
            const { signal } = listening;
            window.addEventListener("keydown", listener, { signal, capture: true });
            document.addEventListener("visibilitychange", () => document.hidden && done(false), { signal });
            stopKeys = () => {
                listening.abort();
                stopKeys = () => {};
            };
        };
        // The Crop Box Lies Exactly Over The Picture
        const placeCrop = () => {
            if (!state) return;
            const { video } = state;
            const box = video.parentElement.querySelector(".crop-box");
            if (!box) return;
            const player = video.parentElement.getBoundingClientRect();
            const picture = video.getBoundingClientRect();
            box.style.left = `${picture.left - player.left}px`;
            box.style.top = `${picture.top - player.top}px`;
            box.style.width = `${picture.width}px`;
            box.style.height = `${picture.height}px`;
        };
        const cropSizes = new ResizeObserver(() => placeCrop());

        // Dragged To Move, Corners Size It, Blazor Hears On Release
        const cropDown = (event) => {
            const frame = event.target instanceof Element ? event.target.closest(".crop-frame") : null;
            if (!state || !frame || event.button !== 0) return;
            const box = frame.parentElement.getBoundingClientRect();
            const start = frame.getBoundingClientRect();
            const size = parseFloat(frame.style.getPropertyValue("--crop-size")) || 1;
            const smallest = parseFloat(frame.dataset.smallest) || 1;
            const [fullWidth, fullHeight] = [start.width / size, start.height / size];
            const corner = event.target.closest(".crop-grip")?.dataset.corner;
            const clamp = (value, low, high) => Math.min(high, Math.max(low, value));
            const share = (offset, room) => (room > 0.5 ? clamp(offset / room, 0, 1) : 0.5);
            const placed = (left, top, width, height) => ({
                x: share(left - box.left, box.width - width),
                y: share(top - box.top, box.height - height),
                size: width / fullWidth,
            });
            const at = (clientX, clientY) => {
                if (!corner) {
                    const left = clamp(start.left + clientX - event.clientX, box.left, box.right - start.width);
                    const top = clamp(start.top + clientY - event.clientY, box.top, box.bottom - start.height);
                    return placed(left, top, start.width, start.height);
                }

                // The Opposite Corner Stays Put, The Box Keeps Its Shape
                const [east, south] = [corner.includes("e"), corner.includes("s")];
                const anchorX = east ? start.left : start.right;
                const anchorY = south ? start.top : start.bottom;
                const room = Math.min(1, (east ? box.right - anchorX : anchorX - box.left) / fullWidth, (south ? box.bottom - anchorY : anchorY - box.top) / fullHeight);
                const wanted = Math.max(Math.abs(clientX - anchorX) / fullWidth, Math.abs(clientY - anchorY) / fullHeight);
                const scale = clamp(wanted, smallest, Math.max(smallest, room));
                const [width, height] = [fullWidth * scale, fullHeight * scale];
                return placed(east ? anchorX : anchorX - width, south ? anchorY : anchorY - height, width, height);
            };
            const show = (crop) => {
                frame.style.setProperty("--crop-x", crop.x);
                frame.style.setProperty("--crop-y", crop.y);
                frame.style.setProperty("--crop-size", crop.size);
            };
            frame.setPointerCapture(event.pointerId);
            event.preventDefault();
            const dragged = new AbortController();
            frame.addEventListener("pointermove", (move) => show(at(move.clientX, move.clientY)), { signal: dragged.signal });
            const done = (up) => {
                dragged.abort();
                const crop = at(up.clientX, up.clientY);
                show(crop);
                state?.dotnet.invokeMethodAsync("OnCrop", crop.x, crop.y, crop.size).catch(() => {});
            };
            frame.addEventListener("pointerup", done, { signal: dragged.signal, once: true });
            frame.addEventListener("pointercancel", done, { signal: dragged.signal, once: true });
        };

        return { attach, detach, play, pause, place, stepEdge, mark, showStrip, showWave, setVolume, fullScreen, placeCrop, showZoom, watchKeys, stopKeys: () => stopKeys() };
    })()
);
