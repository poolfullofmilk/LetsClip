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

// Its Set Here Icon Wins Over A Half Typed Time
document.addEventListener(
    "mousedown",
    (event) => {
        const target = event.target instanceof Element ? event.target : null;
        const input = target?.closest(".mud-input-adornment") ? target.closest(".trim-field")?.querySelector("input") : null;
        if (!input || input !== document.activeElement) return;
        input.value = input.dataset.shown;
        input.blur();
    },
    true
);

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

// Fine Lines Snap To Screen Pixels, Following Scale Changes
const trackDevicePixel = () => {
    const ratio = devicePixelRatio;
    document.documentElement.style.setProperty("--device-pixel", `${1 / ratio}px`);
    // A Narrow Range, The Ratio Is Rarely An Exact Decimal
    matchMedia(`(min-resolution: ${ratio - 0.001}dppx) and (max-resolution: ${ratio + 0.001}dppx)`).addEventListener("change", trackDevicePixel, { once: true });
};
trackDevicePixel();

// Resting On A Card Plays Its Clip, Silently
(() => {
    let previewed = null;

    const stopPreview = () => {
        if (!previewed) return;
        const { holder, timer, frame, video, bar } = previewed;
        previewed = null;
        clearTimeout(timer);
        cancelAnimationFrame(frame);
        delete holder.dataset.playing;
        if (!video) return;
        // After Its Fade, Then The File Is Let Go
        setTimeout(() => {
            video.removeAttribute("src");
            video.load();
            video.remove();
            bar.remove();
        }, 200);
    };

    const startPreview = (holder) => {
        const entry = { holder, frame: 0 };
        previewed = entry;
        // A Pointer Only Passing Over Loads Nothing
        entry.timer = setTimeout(() => {
            const video = document.createElement("video");
            video.muted = true;
            video.loop = true;
            video.src = holder.dataset.clip;
            const bar = document.createElement("div");
            bar.className = "clip-preview-bar";
            const fill = bar.appendChild(document.createElement("div"));
            holder.append(video, bar);
            Object.assign(entry, { video, bar });
            video.addEventListener("playing", () => (holder.dataset.playing = ""), { once: true });
            video.play().catch(() => {});
            const follow = () => {
                if (previewed !== entry) return;
                if (!holder.isConnected) return stopPreview();
                if (video.duration) fill.style.transform = `scaleX(${video.currentTime / video.duration})`;
                entry.frame = requestAnimationFrame(follow);
            };
            entry.frame = requestAnimationFrame(follow);
        }, 250);
    };

    // Anywhere On The Card, Never On Its Buttons
    document.addEventListener("pointerover", (event) => {
        const target = event.target instanceof Element ? event.target : null;
        const card = target && !target.closest("button, .info-icon") ? target.closest(".hover-card") : null;
        const holder = card?.querySelector(".clip-preview") ?? null;
        if (holder === (previewed?.holder ?? null)) return;
        stopPreview();
        if (holder && !matchMedia("(prefers-reduced-motion: reduce)").matches) startPreview(holder);
    });

    // Opening It, Leaving Or Hiding The Window Ends One
    document.addEventListener("pointerdown", stopPreview);
    document.addEventListener("visibilitychange", stopPreview);
    document.documentElement.addEventListener("pointerleave", stopPreview);
})();

window.letsClip = (() => {
    // Changes Run Through Here Animate, Named Cards Slide
    const transition = (dotnet, method, fromTop) => {
        const change = async () => {
            await dotnet.invokeMethodAsync(method).catch(() => {});
            // A New Order Or Filter Reads From Its Start
            if (fromTop) document.querySelector(".page-scroll")?.scrollTo(0, 0);
        };
        // No Transition Support Still Makes The Change
        if (!document.startViewTransition) return void change();
        // A Newer Change Skips This One, Which Rejects Ready
        document.startViewTransition(change).ready.catch(() => {});
    };

    // The Last Card Nearing The View Asks For More Cards
    let listEnd = null;
    const watchEnd = (dotnet, method) => {
        listEnd?.disconnect();
        const last = document.querySelector(".clip-grid > :last-child");
        if (!last) return;
        const near = (entries) => entries.some((entry) => entry.isIntersecting) && dotnet.invokeMethodAsync(method).catch(() => {});
        listEnd = new IntersectionObserver(near, { root: last.closest(".page-scroll"), rootMargin: "100% 0px" });
        listEnd.observe(last);
    };

    // The Shortest Selection, Matching TrimRange.Step
    const step = 0.1;

    // A Shown Edge Sits Half A Millisecond Inside Its Frame
    const inside = 0.0005;
    let state = null;

    // The Editor Attaches Only A Clip Longer Than Zero
    const duration = () => state.duration;
    const clamp = (time) => Math.min(duration(), Math.max(0, time));
    // Whole Screen Pixels Of The Track, So Edges Meet Exactly
    const percent = (time) => `round(${Math.min(100, Math.max(0, (time / duration()) * 100))}%, var(--device-pixel))`;

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
        state = { video, playhead, timeline, dotnet, separator, bounds, listening, duration: length, reported: 0, sent: -1, paused: true, stopAt: null, frame: 0, drag: null };
        // Points Saved Before Snapping Move Onto Their Frames
        const range = { start, end };
        setEdge(range, "start", start);
        setEdge(range, "end", end);
        if (range.start !== start || range.end !== end) commit(range);
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
        if (video.readyState >= 2) loaded();
        if (video.error) failed();
        video.addEventListener("click", toggle, { signal });
        video.addEventListener("dblclick", fullScreen, { signal });
        timeline.addEventListener("pointerdown", down, { signal });
        timeline.addEventListener("pointermove", move, { signal });
        timeline.addEventListener("pointerup", up, { signal });
        timeline.addEventListener("pointercancel", up, { signal });
        timeline.addEventListener("keydown", key, { signal });
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
        handleOf("start").style.left = percent(start);
        handleOf("end").style.left = percent(end);
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
        const size = document.querySelector("[data-bytes-per-second]");
        if (size) showValue(size, formatSize(size.dataset.bytesPerSecond * (end - start)));
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

    // A Typed Or Marked Point Moves Like A Drag
    const place = (edge, time) => {
        if (!state) return;
        const range = readRange();
        setEdge(range, edge, time);
        paint(range);
        showEdge(range, edge);
        commit(range);
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
    // Scrolled Just Enough To Show The Whole Panel, Never Animated
    const reveal = (id) => document.getElementById(id)?.scrollIntoView({ block: "nearest" });

    return { transition, watchEnd, attach, detach, play, pause, place, stepEdge, mark, setVolume, fullScreen, watchKeys, stopKeys: () => stopKeys(), reveal };
})();