// Rows And Cards Marked For It Answer Enter And Space
document.addEventListener("keydown", (event) => {
    if ((event.key === "Enter" || event.key === " ") && event.target instanceof HTMLElement && event.target.hasAttribute("data-keyclick")) {
        event.preventDefault();
        event.target.click();
    }
});

// A Focused Trim Field Holds Plain Seconds, Selected
document.addEventListener("focusin", (event) => {
    const field = event.target instanceof HTMLInputElement ? event.target.closest("[data-seconds]") : null;
    if (!field) return;
    event.target.dataset.shown = event.target.value;
    event.target.value = field.dataset.seconds;
    event.target.select();
});

// Left Untouched It Shows Its Time Again
document.addEventListener("focusout", (event) => {
    const field = event.target instanceof HTMLInputElement ? event.target.closest("[data-seconds]") : null;
    if (field && event.target.value === field.dataset.seconds) event.target.value = event.target.dataset.shown;
});

// Escape Closes An Open Menu, Focus Returns To Its Button
window.addEventListener(
    "keydown",
    (event) => {
        if (event.key !== "Escape") return;
        const activator = document.querySelector('.mud-menu :is(button, .mud-menu-activator)[aria-expanded="true"]');
        if (!activator) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        activator.click();
        activator.focus?.();
    },
    true
);

window.letsClip = (() => {
    // Changes Run Through Here Animate, Named Cards Slide
    const transition = (dotnet, method) => {
        document.startViewTransition(() => dotnet.invokeMethodAsync(method).catch(() => {}));
    };

    // The Shortest Selection, Matching TrimRange.Step
    const step = 0.1;

    // A Shown Edge Sits Half A Millisecond Inside Its Frame
    const inside = 0.0005;
    let state = null;

    // The Editor Attaches Only A Clip Longer Than Zero
    const duration = () => state.duration;
    const clamp = (time) => Math.min(duration(), Math.max(0, time));
    const percent = (time) => `${Math.min(100, Math.max(0, (time / duration()) * 100))}%`;

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
        playhead.style.left = percent(time);
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

    // Each Clip Opens Playing Its Kept Part With Sound
    const attach = (video, playhead, timeline, dotnet, length, start, end, volume, separator, frames) => {
        detach();
        // Frame Starts, Then The Clip's End
        const bounds = [...new Set(frames.map((time) => Math.max(0, time)).filter((time) => time < length)), length].sort((first, second) => first - second);
        state = { video, playhead, timeline, dotnet, separator, bounds, duration: length, reported: 0, sent: -1, paused: true, stopAt: null, frame: 0, drag: null };
        // Points Saved Before Snapping Move Onto Their Frames
        const range = { start, end };
        setEdge(range, "start", start);
        setEdge(range, "end", end);
        if (range.start !== start || range.end !== end) commit(range);
        video.currentTime = range.start + inside;
        setVolume(volume, false);
        video.addEventListener("error", state.onError = () => dotnet.invokeMethodAsync("OnPlayerError").catch(() => {}));
        video.addEventListener(
            "loadeddata",
            (state.onLoaded = () => {
                dotnet.invokeMethodAsync("OnPlayerReady").catch(() => {});
                play(range.start, range.end);
            }),
            { once: true }
        );
        if (video.readyState >= 2) state.onLoaded();
        if (video.error) state.onError();
        video.addEventListener("dblclick", state.onDouble = () => fullScreen());
        timeline.addEventListener("pointerdown", state.onDown = (event) => down(event));
        timeline.addEventListener("pointermove", state.onMove = (event) => move(event));
        timeline.addEventListener("pointerup", state.onUp = (event) => up(event));
        timeline.addEventListener("pointercancel", state.onUp);
        timeline.addEventListener("keydown", state.onKey = (event) => key(event));
        for (const name of wakers) video.addEventListener(name, kick);
        kick();
    };

    // Playback Stops And Releases The File When The Editor Closes
    const detach = () => {
        if (!state) return;
        cancelAnimationFrame(state.frame);
        const { video, timeline } = state;
        video.removeEventListener("error", state.onError);
        video.removeEventListener("loadeddata", state.onLoaded);
        video.removeEventListener("dblclick", state.onDouble);
        for (const name of wakers) video.removeEventListener(name, kick);
        if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
        timeline.removeEventListener("pointerdown", state.onDown);
        timeline.removeEventListener("pointermove", state.onMove);
        timeline.removeEventListener("pointerup", state.onUp);
        timeline.removeEventListener("pointercancel", state.onUp);
        timeline.removeEventListener("keydown", state.onKey);
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

    // The Nearest Frame Edge Within Half A Step, Else The Time
    const fit = (time, low, high) => {
        const clamped = Math.min(high, Math.max(low, time));
        const index = floorIndex(clamped);
        const near = [state.bounds[index], state.bounds[index + 1]]
            .filter((bound) => bound !== undefined && bound >= low - 1e-6 && bound <= high + 1e-6 && Math.abs(bound - clamped) <= step / 2)
            .sort((first, second) => Math.abs(first - clamped) - Math.abs(second - clamped))[0];
        return near ?? Math.round(clamped * 1000) / 1000;
    };

    const setEdge = (range, edge, time) => {
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
        selection.style.width = percent(end - start);
        handleOf("start").style.left = percent(start);
        handleOf("end").style.left = percent(end);
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
        // Its Own Text Node, Which Blazor Keeps Writing To
        const length = document.querySelector("[data-length]")?.firstChild;
        if (length) length.nodeValue = formatTime(end - start);
    };

    const commit = (range) => state.dotnet.invokeMethodAsync("OnRange", range.start, range.end).catch(() => {});

    // Arrows Step 0.1 s, Shift 1 s, Held Keys Build
    const key = (event) => {
        const handle = event.target instanceof Element ? event.target.closest("[data-handle]") : null;
        if (!state || !handle) return;
        const edge = handle.dataset.handle;
        const range = state.keyed && performance.now() - state.keyed.at < 600 ? state.keyed.range : readRange();
        const jump = event.shiftKey ? 1 : step;
        const time = { ArrowLeft: range[edge] - jump, ArrowDown: range[edge] - jump, ArrowRight: range[edge] + jump, ArrowUp: range[edge] + jump, Home: 0, End: duration() }[event.key];
        if (time === undefined) return;
        event.preventDefault();
        delete handle.dataset.grabbed;
        setEdge(range, edge, time);
        paint(range);
        showEdge(range, edge);
        state.keyed = { range, at: performance.now() };
        commit(range);
    };

    // Unclamped, So A Handle Grabbed Anywhere Reaches Both Ends
    const timeAt = (event) => {
        const bounds = state.timeline.getBoundingClientRect();
        return ((event.clientX - bounds.left) / bounds.width) * duration();
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

    // Play Runs To The End Point, From Start When Outside
    const play = (start, end) => {
        if (!state) return;
        const { video } = state;
        if (video.currentTime < start - 0.05 || video.currentTime >= end - 0.05) video.currentTime = start + inside;
        state.stopAt = end;
        video.play().catch(() => {});
    };

    const pause = () => state?.video.pause();

    // A Jump Past The End Point Lets Playback Run On
    const nudge = (seconds) => {
        if (!state) return;
        const time = clamp(state.video.currentTime + seconds);
        state.video.currentTime = time;
        if (state.stopAt !== null && time >= state.stopAt) state.stopAt = null;
    };

    // A Typed Or Marked Point Moves Its Handle Like A Drag
    const place = (edge, time) => {
        if (!state) return;
        const range = readRange();
        setEdge(range, edge, time);
        paint(range);
        showEdge(range, edge);
        commit(range);
    };

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
            else dotnet.invokeMethodAsync("OnKeys", event.code, event.ctrlKey, event.altKey, event.shiftKey, event.metaKey).catch(() => {});
        };
        const onHidden = () => {
            if (document.hidden) done(false);
        };
        window.addEventListener("keydown", listener, true);
        document.addEventListener("visibilitychange", onHidden);
        stopKeys = () => {
            window.removeEventListener("keydown", listener, true);
            document.removeEventListener("visibilitychange", onHidden);
            stopKeys = () => {};
        };
    };
    return { transition, attach, detach, play, pause, nudge, place, mark, setVolume, fullScreen, watchKeys, stopKeys: () => stopKeys() };
})();