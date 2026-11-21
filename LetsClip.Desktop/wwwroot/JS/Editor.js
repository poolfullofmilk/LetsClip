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
        const attach = (video, source, playhead, timeline, dotnet, length, start, end, volume, muted, separator, frames, autoPlay) => {
            detach();
            // Detach Removed The Source Behind Blazor's Back
            if (video.getAttribute("src") !== source) video.setAttribute("src", source);
            // Frame Starts, Then The Clip's End
            const bounds = [...new Set(frames.map((time) => Math.max(0, time)).filter((time) => time < length)), length].sort((first, second) => first - second);
            // One Signal Takes Every Listener Off On Detach
            const listening = new AbortController();
            const { signal } = listening;
            state = { video, playhead, timeline, dotnet, separator, bounds, listening, duration: length, view: { start: 0, end: length }, viewTimer: 0, reported: 0, sent: -1, paused: true, stopAt: null, frame: 0, drag: null, live: { busy: false, next: null } };
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
            // The Crop Covers The Picture, So The Player Hears Clicks
            video.parentElement.addEventListener("click", click, { signal });
            video.parentElement.addEventListener("dblclick", fullScreen, { signal });
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

        // Start And End Follow A Drag, Blazor Writes The Rest
        const showTimes = ({ start, end }) => {
            for (const [edge, time] of [["Start", start], ["End", end]]) {
                const field = document.querySelector(`.trim-field[data-edge="${edge}"]`);
                const input = field?.querySelector("input");
                if (!input || input === document.activeElement) continue;
                input.value = formatTime(time);
                field.dataset.seconds = String(Math.round(time * 10) / 10).replace(".", state.separator);
            }
        };

        // Blazor Hears A Drag Live, One Call At A Time
        const tell = (method, ...values) => {
            const live = state?.live;
            if (!live) return;
            if (live.busy) {
                live.next = [method, values];
                return;
            }
            live.busy = true;
            state.dotnet
                .invokeMethodAsync(method, ...values)
                .catch(() => {})
                .finally(() => {
                    live.busy = false;
                    const next = live.next;
                    live.next = null;
                    if (next && state?.live === live) tell(next[0], ...next[1]);
                });
        };

        // A Settled Value Drops The Live One Still Waiting
        const settle = (method, ...values) => {
            state.live.next = null;
            state.dotnet.invokeMethodAsync(method, ...values).catch(() => {});
        };

        const commit = (range) => settle("OnRange", range.start, range.end);

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
            used();
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

        // Once Used, The Timeline's Zoom Hint Stays Away
        const used = () => (state.timeline.dataset.used = "");

        // A Handle Drags From Where It Was Grabbed, Else Seeks
        const down = (event) => {
            if (!state || event.button !== 0) return;
            used();
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
            if (state.drag !== "seek") tell("OnRanging", state.range.start, state.range.end);
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

        // The Click Ending A Crop Drag Plays Nothing
        const click = () => {
            if (!state) return;
            if (state.cropMoved) state.cropMoved = false;
            else toggle();
        };

        document.addEventListener("fullscreenchange", () => {
            if (state) state.video.controls = document.fullscreenElement === state.video;
        });

        // Escape Leaves Full Screen, Browser Keys Are Off
        window.addEventListener("keydown", (event) => {
            if (event.key === "Escape" && document.fullscreenElement) document.exitFullscreen().catch(() => {});
        });

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

        // Dragged To Move, Grips Size It, Blazor Hears It Live
        const cropDown = (event) => {
            if (state) state.cropMoved = false;
            const frame = event.target instanceof Element ? event.target.closest(".crop-frame") : null;
            if (!state || !frame || event.button !== 0) return;
            const box = frame.parentElement.getBoundingClientRect();
            const start = frame.getBoundingClientRect();
            const grip = event.target.closest(".crop-grip")?.dataset.grip;
            // A Share Of Each Side, Room For Every Grip
            const share = parseFloat(frame.dataset.smallest) || 0.1;
            const smallestWidth = Math.min(box.width, Math.max(box.width * share, 80));
            const smallestHeight = Math.min(box.height, Math.max(box.height * share, 80));
            const clamp = (value, low, high) => Math.min(high, Math.max(low, value));
            // Shares Of The Picture, Its Own Edges Exact
            const snap = (value) => (value < 0.001 ? 0 : value > 0.999 ? 1 : value);
            const placed = (left, top, right, bottom) => {
                const crop = { left: snap((left - box.left) / box.width), top: snap((top - box.top) / box.height) };
                crop.width = snap((right - box.left) / box.width) - crop.left;
                crop.height = snap((bottom - box.top) / box.height) - crop.top;
                return crop;
            };
            const at = (clientX, clientY) => {
                const [across, down] = [clientX - event.clientX, clientY - event.clientY];
                if (!grip) {
                    const left = clamp(start.left + across, box.left, box.right - start.width);
                    const top = clamp(start.top + down, box.top, box.bottom - start.height);
                    return placed(left, top, left + start.width, top + start.height);
                }

                // Only The Grip's Own Sides Follow, The Others Stay Put
                const moves = (side) => grip.includes(side);
                const left = moves("w") ? clamp(start.left + across, box.left, start.right - smallestWidth) : start.left;
                const right = moves("e") ? clamp(start.right + across, start.left + smallestWidth, box.right) : start.right;
                const top = moves("n") ? clamp(start.top + down, box.top, start.bottom - smallestHeight) : start.top;
                const bottom = moves("s") ? clamp(start.bottom + down, start.top + smallestHeight, box.bottom) : start.bottom;
                return placed(left, top, right, bottom);
            };
            const show = (crop) => {
                for (const side of ["left", "top", "width", "height"]) frame.style.setProperty(`--crop-${side}`, crop[side]);
                frame.toggleAttribute("data-whole", crop.width === 1 && crop.height === 1);
            };
            frame.setPointerCapture(event.pointerId);
            event.preventDefault();
            const dragged = new AbortController();
            // A Few Pixels Of Wobble Still Count As A Click
            const moved = (pointer) => state && (state.cropMoved ||= Math.hypot(pointer.clientX - event.clientX, pointer.clientY - event.clientY) > 3);
            frame.addEventListener(
                "pointermove",
                (move) => {
                    if (!moved(move)) return;
                    const crop = at(move.clientX, move.clientY);
                    show(crop);
                    tell("OnCropping", crop.left, crop.top, crop.width, crop.height);
                },
                { signal: dragged.signal }
            );
            const done = (up) => {
                dragged.abort();
                if (!moved(up)) return;
                const crop = at(up.clientX, up.clientY);
                show(crop);
                settle("OnCrop", crop.left, crop.top, crop.width, crop.height);
            };
            frame.addEventListener("pointerup", done, { signal: dragged.signal, once: true });
            frame.addEventListener("pointercancel", done, { signal: dragged.signal, once: true });
        };

        return { attach, detach, play, pause, place, stepEdge, mark, showStrip, showWave, setVolume, fullScreen, placeCrop, showZoom };
    })()
);
