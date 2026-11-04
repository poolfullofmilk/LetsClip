// Rows And Cards Marked For It Answer Enter And Space
document.addEventListener("keydown", (event) => {
    if ((event.key === "Enter" || event.key === " ") && event.target instanceof HTMLElement && event.target.hasAttribute("data-keyclick")) {
        event.preventDefault();
        event.target.click();
    }
});

// Escape Closes An Open Menu First, Focus Goes Back To Its Button
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

// An Info Icon Inside A Switch Label Never Toggles The Switch
document.addEventListener("click", (event) => {
    if (event.target instanceof Element && event.target.closest(".info-icon")) {
        event.preventDefault();
    }
});

window.letsClip = (() => {
    let state = null;

    // The Playhead Follows The Video Each Frame, Blazor Hears The Time Ten Times A Second
    const loop = () => {
        if (!state) return;
        const { video, playhead, dotnet } = state;
        const duration = state.duration || video.duration || 0;
        if (state.stopAt !== null && !video.paused && video.currentTime >= state.stopAt) {
            video.pause();
            video.currentTime = state.stopAt;
        }
        if (playhead && duration > 0) {
            playhead.style.left = `${Math.min(100, Math.max(0, (video.currentTime / duration) * 100))}%`;
        }
        const now = performance.now();
        if (now - state.reported > 100 || video.paused !== state.paused) {
            state.reported = now;
            state.paused = video.paused;
            dotnet.invokeMethodAsync("OnPlayerTime", video.currentTime, video.paused).catch(() => {});
        }
        state.frame = requestAnimationFrame(loop);
    };

    const attach = (video, playhead, timeline, dotnet, duration) => {
        detach();
        state = { video, playhead, timeline, dotnet, duration, reported: 0, paused: true, stopAt: null, frame: 0, drag: null, grip: 0 };
        video.addEventListener("error", state.onError = () => dotnet.invokeMethodAsync("OnPlayerError").catch(() => {}));
        video.addEventListener("dblclick", state.onDouble = () => fullScreen());
        timeline.addEventListener("pointerdown", state.onDown = (event) => down(event));
        timeline.addEventListener("pointermove", state.onMove = (event) => move(event));
        timeline.addEventListener("pointerup", state.onUp = (event) => up(event));
        timeline.addEventListener("pointercancel", state.onUp);
        timeline.addEventListener("keydown", state.onKey = (event) => key(event));
        state.frame = requestAnimationFrame(loop);
    };

    // A Focused Handle Steps 0.1 s With The Arrow Keys, 1 s With Shift
    const key = (event) => {
        const handle = event.target instanceof Element ? event.target.closest("[data-handle]") : null;
        if (!state || !handle) return;
        // Held Keys Repeat Faster Than Blazor Renders, So The Last Asked Value Is The Base
        const shown = parseFloat(handle.getAttribute("aria-valuenow") || "0");
        const recent = handle.pendingAt && performance.now() - handle.pendingAt < 400;
        const value = recent ? handle.pending : shown;
        const step = event.shiftKey ? 1 : 0.1;
        const next = { ArrowLeft: value - step, ArrowDown: value - step, ArrowRight: value + step, ArrowUp: value + step, Home: 0, End: state.duration }[event.key];
        if (next === undefined) return;
        event.preventDefault();
        handle.pending = Math.round(next * 10) / 10;
        handle.pendingAt = performance.now();
        state.dotnet.invokeMethodAsync("OnHandle", handle.dataset.handle, handle.pending).catch(() => {});
    };

    // Playback Stops And Releases The File When The Editor Closes
    const detach = () => {
        if (!state) return;
        cancelAnimationFrame(state.frame);
        const { video, timeline } = state;
        video.removeEventListener("error", state.onError);
        video.removeEventListener("dblclick", state.onDouble);
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

    const timeAt = (event) => {
        const bounds = state.timeline.getBoundingClientRect();
        const share = Math.min(1, Math.max(0, (event.clientX - bounds.left) / bounds.width));
        return share * (state.duration || 0);
    };

    // A Handle Drags Its Edge From Where It Was Grabbed, Anywhere Else Seeks
    const down = (event) => {
        if (!state || event.button !== 0) return;
        const handle = event.target instanceof Element ? event.target.closest("[data-handle]") : null;
        state.drag = handle ? handle.dataset.handle : "seek";
        state.grip = handle ? timeAt(event) - parseFloat(handle.getAttribute("aria-valuenow") || "0") : 0;
        state.timeline.setPointerCapture(event.pointerId);
        event.preventDefault();
        handle?.focus();
        if (!handle) send(timeAt(event));
    };

    const move = (event) => {
        if (!state?.drag) return;
        state.pending = timeAt(event) - state.grip;
        if (state.queued) return;
        state.queued = true;
        requestAnimationFrame(() => {
            if (!state) return;
            state.queued = false;
            send(state.pending);
        });
    };

    const up = (event) => {
        if (!state?.drag) return;
        send(timeAt(event) - state.grip);
        state.drag = null;
    };

    const send = (time) => {
        if (state.drag === "seek") {
            state.video.currentTime = time;
            state.dotnet.invokeMethodAsync("OnSeek", time).catch(() => {});
        } else {
            state.dotnet.invokeMethodAsync("OnHandle", state.drag, time).catch(() => {});
        }
    };

    // Play Runs From The Playhead To The End Point, Starting Over Once There
    const play = (start, end) => {
        if (!state) return;
        const { video } = state;
        const time = video.currentTime;
        const duration = state.duration || video.duration || 0;
        if (Math.abs(time - end) < 0.05 || time >= duration - 0.05) {
            video.currentTime = start;
            state.stopAt = end;
        } else {
            state.stopAt = time < end ? end : null;
        }
        video.play().catch(() => {});
    };

    const pause = () => state?.video.pause();

    // A Jump Past The End Point Lets Playback Run On From There
    const nudge = (seconds) => {
        if (!state) return;
        const { video } = state;
        const duration = state.duration || video.duration || 0;
        const time = Math.min(duration, Math.max(0, video.currentTime + seconds));
        video.currentTime = time;
        if (state.stopAt !== null && time >= state.stopAt) state.stopAt = null;
        state.dotnet.invokeMethodAsync("OnSeek", time).catch(() => {});
    };

    const seek = (time) => {
        if (state) state.video.currentTime = time;
    };

    const setMuted = (muted) => {
        if (state) state.video.muted = muted;
    };

    // Full Screen Shows The Clip Alone With The Browser's Own Controls
    const fullScreen = () => {
        if (!state) return;
        if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
        else state.video.requestFullscreen().catch(() => {});
    };

    document.addEventListener("fullscreenchange", () => {
        if (state) state.video.controls = document.fullscreenElement === state.video;
    });

    // Escape Leaves Full Screen, The Browser Keys That Would Are Off
    window.addEventListener("keydown", (event) => {
        if (event.key === "Escape" && document.fullscreenElement) document.exitFullscreen().catch(() => {});
    });

    // The Next Key Pressed With Its Modifiers Becomes The Hotkey, Escape Cancels
    const captureKey = (dotnet) =>
        new Promise((resolve) => {
            const listener = (event) => {
                if (["Control", "Shift", "Alt", "Meta"].includes(event.key)) return;
                event.preventDefault();
                event.stopPropagation();
                window.removeEventListener("keydown", listener, true);
                resolve(event.key === "Escape" ? null : { code: event.code, control: event.ctrlKey, alt: event.altKey, shift: event.shiftKey, meta: event.metaKey });
            };
            window.addEventListener("keydown", listener, true);
        });

    return { attach, detach, play, pause, nudge, seek, setMuted, fullScreen, captureKey };
})();
