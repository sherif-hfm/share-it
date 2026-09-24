(() => {
  let token, lastTrigger;
  let nextUploadId = 0;
  const toastTimers = new WeakMap();
  const uploaders = new Map();
  const dialogs = new Map();
  async function csrf() {
    const response = await fetch("/api/antiforgery", { cache: "no-store" });
    if (!response.ok) throw new Error("Unable to connect. Refresh this page and try again.");
    token = (await response.json()).token;
    return token;
  }
  async function request(url, body) {
    const response = await fetch(url, {
      method: "POST", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": await csrf() },
      body: JSON.stringify(body), credentials: "same-origin"
    });
    let data;
    try { data = await response.json(); } catch { throw new Error("The server could not complete this request. Please try again."); }
    if (!response.ok) throw new Error(data.detail || "The request could not be completed.");
    return data;
  }
  function toast(message) {
    const node = document.querySelector("dialog[open] .dialog-toast") || document.getElementById("global-toast");
    if (!node) return;
    node.textContent = message; node.classList.add("visible");
    clearTimeout(toastTimers.get(node));
    toastTimers.set(node, setTimeout(() => node.classList.remove("visible"), 3200));
  }
  async function copy(value, trigger) {
    const previous = trigger?.dataset.copyInput ? document.getElementById(trigger.dataset.copyInput) : trigger || document.activeElement;
    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(value);
        toast("Copied to clipboard."); return;
      }
    } catch { /* Try the compatibility path if clipboard access is denied. */ }
    const selection = document.createElement("textarea");
    selection.value = value; selection.setAttribute("readonly", "");
    selection.setAttribute("aria-label", "Text to copy");
    selection.style.cssText = "position:fixed;left:0;top:0;width:1px;height:1px;opacity:0;pointer-events:none";
    // Keep the temporary input inside the active dialog, where it can receive focus.
    (document.querySelector("dialog[open]") || document.body).appendChild(selection);
    selection.focus({ preventScroll: true }); selection.select();
    const restoreFocus = () => {
      const target = trigger?.dataset.copyInput ? document.getElementById(trigger.dataset.copyInput) : previous;
      if (target?.isConnected) target.focus({ preventScroll: true });
    };
    let copied = false;
    // Older browsers and HTTP LAN pages may lack the modern Clipboard API.
    try { copied = document.execCommand("copy"); } catch { /* Offer manual copying below. */ }
    if (copied) {
      selection.remove(); restoreFocus(); toast("Copied to clipboard."); return;
    }
    selection.className = "clipboard-fallback";
    selection.style.cssText = "position:fixed;left:12px;bottom:12px;z-index:4000;width:calc(100% - 24px);height:100px";
    toast("Automatic copying is unavailable. Press Ctrl+C or Cmd+C to copy the selected text, then Escape.");
    const onBlur = () => selection.remove();
    selection.addEventListener("keydown", e => {
      if (e.key === "Escape") {
        e.preventDefault(); e.stopPropagation();
        selection.removeEventListener("blur", onBlur);
        restoreFocus(); selection.remove();
      }
    });
    selection.addEventListener("blur", onBlur, { once: true });
  }
  document.addEventListener("click", e => {
    lastTrigger = e.target.closest("button,a,input,textarea,select");
    if (e.target.closest("[data-reload]")) location.reload();
    const button = e.target.closest("[data-copy-value],[data-copy-b64],[data-copy-input]");
    if (button) {
      let text = button.dataset.copyValue;
      if (button.dataset.copyB64 !== undefined) text = new TextDecoder().decode(Uint8Array.from(atob(button.dataset.copyB64), c => c.charCodeAt(0)));
      if (button.dataset.copyInput) text = document.getElementById(button.dataset.copyInput)?.value;
      copy(text || "", button);
    }
    const browse = e.target.closest("[data-browse-input]");
    if (browse) document.getElementById(browse.dataset.browseInput)?.click();
  });
  document.addEventListener("keydown", e => {
    if ((e.ctrlKey || e.metaKey) && e.key === "Enter" && e.target.dataset.saveShortcut) {
      e.preventDefault(); e.target.dispatchEvent(new Event("change", { bubbles: true }));
      document.getElementById(e.target.dataset.saveShortcut)?.click();
    }
  });
  function cycleTheme() {
    const values = ["dark", "light", "system"];
    let current = "dark"; try { current = localStorage.getItem("shareit.theme") || "dark"; } catch {}
    const next = values[(values.indexOf(current) + 1) % values.length];
    try { localStorage.setItem("shareit.theme", next); } catch {}
    document.documentElement.dataset.theme = next === "system" ? (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light") : next;
    toast(next.charAt(0).toUpperCase() + next.slice(1) + " theme");
  }
  matchMedia("(prefers-color-scheme: dark)").addEventListener("change", e => {
    try { if (localStorage.getItem("shareit.theme") === "system") document.documentElement.dataset.theme = e.matches ? "dark" : "light"; } catch {}
  });
  setInterval(() => {
    const reconnect = document.getElementById("components-reconnect-modal");
    const state = reconnect?.classList.contains("components-reconnect-show") ? "Reconnecting" : reconnect?.classList.contains("components-reconnect-failed") || reconnect?.classList.contains("components-reconnect-rejected") ? "Offline" : "Connected";
    for (const label of document.querySelectorAll("[data-connection-state]")) {
      label.textContent = state; label.parentElement.dataset.status = state.toLowerCase();
    }
    for (const node of document.querySelectorAll("[data-countdown]")) {
      const seconds = Math.max(0, Math.floor((new Date(node.dataset.countdown) - Date.now()) / 1000));
      node.textContent = seconds === 0 ? "Session expired" : seconds >= 3600 ? Math.floor(seconds / 3600) + "h " + Math.floor(seconds % 3600 / 60) + "m left" : Math.floor(seconds / 60) + "m " + String(seconds % 60).padStart(2, "0") + "s left";
    }
  }, 1000);
  function createUploadId() {
    try {
      if (typeof globalThis.crypto?.randomUUID === "function") return globalThis.crypto.randomUUID();
    } catch { /* Fall back if the browser exposes the API but blocks its use. */ }
    // These IDs only track progress within this page, including on HTTP LAN origins.
    return `upload-${++nextUploadId}`;
  }
  function attachUploads(dropzone, inputId, code, maximumBytes, dotnet) {
    const input = document.getElementById(inputId);
    const state = { xhr: null, cancelled: false, disposed: false, busy: false };
    uploaders.set(inputId, state);
    const report = async (...args) => { if (!state.disposed) try { await dotnet.invokeMethodAsync("UploadProgress", ...args); } catch {} };
    async function send(file, id) {
      if (file.size > maximumBytes) { await report(id, file.name, 0, "failed", "This file exceeds the 25 MB limit."); return; }
      await report(id, file.name, 0, "uploading", "");
      let authToken;
      try { authToken = await csrf(); }
      catch (error) { await report(id, file.name, 0, "failed", error.message || "Unable to connect."); return; }
      if (state.cancelled || state.disposed) { await report(id, file.name, 0, "cancelled", ""); return; }
      await new Promise(resolve => {
        const xhr = new XMLHttpRequest(); state.xhr = xhr;
        xhr.open("POST", "/api/v1/sessions/" + encodeURIComponent(code) + "/files");
        xhr.setRequestHeader("X-CSRF-TOKEN", authToken);
        xhr.setRequestHeader("X-File-Size", String(file.size));
        let last = 0;
        xhr.upload.onprogress = e => {
          if (e.lengthComputable && Date.now() - last > 400) { last = Date.now(); report(id, file.name, Math.min(99, Math.round(e.loaded * 100 / e.total)), "uploading", ""); }
        };
        xhr.onload = async () => {
          let result = {}; try { result = JSON.parse(xhr.responseText); } catch {}
          await report(id, file.name, xhr.status < 300 ? 100 : 0, xhr.status < 300 ? "complete" : "failed", xhr.status < 300 ? "" : result.detail || "Upload failed. Please try again.");
          resolve();
        };
        xhr.onerror = async () => { await report(id, file.name, 0, "failed", "Connection interrupted. Check the file list before retrying."); resolve(); };
        xhr.onabort = async () => { await report(id, file.name, 0, "cancelled", ""); resolve(); };
        const body = new FormData(); body.append("file", file); xhr.send(body);
      });
    }
    async function queue(files) {
      if (state.busy) { toast("Wait for the current upload or cancel it first."); return; }
      state.busy = true; state.cancelled = false;
      try { for (const file of files) { if (state.cancelled || state.disposed) break; await send(file, createUploadId()); } }
      catch (error) { toast(error.message || "Upload failed."); }
      finally { state.busy = false; state.xhr = null; input.value = ""; }
    }
    state.onChange = () => queue(Array.from(input.files));
    state.onDrag = e => { e.preventDefault(); dropzone.classList.add("drag-over"); };
    state.onLeave = () => dropzone.classList.remove("drag-over");
    state.onDrop = e => { e.preventDefault(); dropzone.classList.remove("drag-over"); queue(Array.from(e.dataTransfer.files)); };
    input.addEventListener("change", state.onChange); dropzone.addEventListener("dragover", state.onDrag);
    dropzone.addEventListener("dragleave", state.onLeave); dropzone.addEventListener("drop", state.onDrop);
    state.detach = () => {
      state.disposed = true; state.cancelled = true; state.xhr?.abort();
      input.removeEventListener("change", state.onChange); dropzone.removeEventListener("dragover", state.onDrag);
      dropzone.removeEventListener("dragleave", state.onLeave); dropzone.removeEventListener("drop", state.onDrop);
    };
  }
  window.shareIt = {
    create: minutes => request("/api/v1/sessions", { minutes }),
    cancelCreated: id => request(`/api/v1/sessions/${encodeURIComponent(id)}/cancel`, {}),
    join: (code, pin) => request("/api/v1/join", { code, pin }),
    copy, toast, cycleTheme, attachUploads,
    openDialog: (dialog, key) => { const previous = lastTrigger?.isConnected ? lastTrigger : document.activeElement; dialogs.set(key, { dialog, previous }); dialog.addEventListener("cancel", e => { e.preventDefault(); dialog.querySelector("[data-dialog-close]")?.click(); }); if (!dialog.open) dialog.showModal(); (dialog.querySelector("[data-autofocus]") || dialog.querySelector("input,textarea"))?.focus(); },
    closeDialog: key => { const entry = dialogs.get(key); if (entry) { if (entry.dialog.open) entry.dialog.close(); requestAnimationFrame(() => { if (entry.previous?.isConnected) entry.previous.focus(); }); dialogs.delete(key); } },
    cancelUploads: id => { const state = uploaders.get(id); if (state) { state.cancelled = true; state.xhr?.abort(); } },
    detachUploads: id => { uploaders.get(id)?.detach(); uploaders.delete(id); }
  };
})();
