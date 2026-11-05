// Rows And Cards Marked For It Answer Enter And Space
document.addEventListener("keydown", (event) => {
    if ((event.key === "Enter" || event.key === " ") && event.target instanceof HTMLElement && event.target.hasAttribute("data-keyclick")) {
        event.preventDefault();
        event.target.click();
    }
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
    // The Shortest Selection, Matching TrimRange.Step
    const step = 0.1;
    let state = null;

    const duration = () => state.duration || state.video.duration || 0;
    const clamp = (time) => Math.min(duration(), Math.max(0, time));

    // The Playhead Follows Each Frame, Blazor Hears Ten A Second
    const loop = () => {
        if (!state) return;
        state.frame = 0;
        const { video, playhead, dotnet } = state;
        if (state.stopAt !== null && !video.paused && video.currentTime >= state.stopAt) {
            video.pause();
            video.currentTime = state.stopAt;
        }
        const time = video.currentTime;
        if (playhead && duration() > 0) {
            playhead.style.left = `${Math.min(100, Math.max(0, (time / duration()) * 100))}%`;
        }
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

    // Each Clip Attaches Fresh And Opens At Its Trim Start
    const attach = (video, playhead, timeline, dotnet, length, start) => {
        detach();
        state = { video, playhead, timeline, dotnet, duration: length, reported: 0, sent: -1, paused: true, stopAt: null, frame: 0, drag: null };
        video.currentTime = start;
        video.addEventListener("error", state.onError = () => dotnet.invokeMethodAsync("OnPlayerError").catch(() => {}));
        video.addEventListener("loadeddata", state.onLoaded = () => dotnet.invokeMethodAsync("OnPlayerReady").catch(() => {}));
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

    const setEdge = (range, edge, time) => {
        const value = Math.round(time * 1000) / 1000;
        if (edge === "start") range.start = Math.min(Math.max(0, value), range.end - step);
        else range.end = Math.max(Math.min(duration(), value), range.start + step);
    };

    // A Drag Paints The Handles, Blazor Hears Once It Settles
    const paint = ({ start, end }) => {
        const percent = (time) => `${Math.min(100, Math.max(0, (time / duration()) * 100))}%`;
        const [before, after] = state.timeline.querySelectorAll(".timeline-shade");
        const selection = state.timeline.querySelector(".timeline-selection");
        before.style.width = percent(start);
        after.style.left = percent(end);
        selection.style.left = percent(start);
        selection.style.width = percent(end - start);
        handleOf("start").style.left = percent(start);
        handleOf("end").style.left = percent(end);
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
        setEdge(range, edge, time);
        paint(range);
        state.video.currentTime = range[edge];
        state.keyed = { range, at: performance.now() };
        commit(range);
    };

    const timeAt = (event) => {
        const bounds = state.timeline.getBoundingClientRect();
        return Math.min(1, Math.max(0, (event.clientX - bounds.left) / bounds.width)) * duration();
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
        handle?.focus();
        if (!handle) state.video.currentTime = clamp(timeAt(event));
    };

    const apply = (time) => {
        if (state.drag === "seek") {
            state.video.currentTime = clamp(time);
            return;
        }
        setEdge(state.range, state.drag, time);
        paint(state.range);
        state.video.currentTime = state.range[state.drag];
    };

    const move = (event) => {
        if (!state?.drag) return;
        state.pending = timeAt(event) - state.grip;
        state.moved = true;
        if (state.queued) return;
        state.queued = true;
        requestAnimationFrame(() => {
            if (!state?.drag) return;
            state.queued = false;
            apply(state.pending);
        });
    };

    const up = (event) => {
        if (!state?.drag) return;
        if (state.moved) {
            apply(timeAt(event) - state.grip);
            if (state.drag !== "seek") commit(state.range);
        }
        state.drag = null;
        state.queued = false;
    };

    // Play Runs To The End Point, From Start When Outside
    const play = (start, end) => {
        if (!state) return;
        const { video } = state;
        if (video.currentTime < start - 0.05 || video.currentTime >= end - 0.05) video.currentTime = start;
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

    const setMuted = (muted) => {
        if (state) state.video.muted = muted;
    };

    const setVolume = (volume) => {
        if (state) state.video.volume = volume;
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

    // The Next Key Becomes The Hotkey, Escape Or Hiding Cancels
    let cancelCapture = () => {};
    const captureKey = () =>
        new Promise((resolve) => {
            const finish = (value) => {
                window.removeEventListener("keydown", listener, true);
                document.removeEventListener("visibilitychange", onHidden);
                cancelCapture = () => {};
                resolve(value);
            };
            const listener = (event) => {
                if (["Control", "Shift", "Alt", "Meta"].includes(event.key)) return;
                event.preventDefault();
                event.stopPropagation();
                finish(event.key === "Escape" ? null : { code: event.code, control: event.ctrlKey, alt: event.altKey, shift: event.shiftKey, meta: event.metaKey });
            };
            const onHidden = () => {
                if (document.hidden) finish(null);
            };
            window.addEventListener("keydown", listener, true);
            document.addEventListener("visibilitychange", onHidden);
            cancelCapture = () => finish(null);
        });

    return { attach, detach, play, pause, nudge, setMuted, setVolume, fullScreen, captureKey, cancelCapture: () => cancelCapture() };
})();