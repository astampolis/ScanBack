"use strict";
(() => {
    const root = document.getElementById("incident-chat");
    if (!root) return;
    const feed = document.getElementById("chat-messages");
    const status = document.getElementById("chat-status");
    const form = document.getElementById("chat-form");
    const body = document.getElementById("chat-body");
    const button = form.querySelector("button");
    let after = 0, stopped = false, sending = false, pending = null;
    const seen = new Set();
    function end() {
        stopped = true; form.hidden = true; feed.replaceChildren();
        status.textContent = root.dataset.ended;
    }
    function append(message) {
        if (seen.has(message.id)) return;
        seen.add(message.id);
        const atBottom = feed.scrollHeight - feed.scrollTop - feed.clientHeight < 60;
        const item = document.createElement("article");
        item.className = "chat-message" + (message.isMine ? " chat-mine" : "");
        const label = document.createElement("strong");
        label.textContent = message.isMine ? root.dataset.you : (message.senderRole === "Reporter" ? root.dataset.reporter : root.dataset.contact);
        const time = document.createElement("time");
        time.dateTime = message.sentAt;
        time.textContent = new Date(message.sentAt).toLocaleTimeString(document.documentElement.lang, {hour:"2-digit",minute:"2-digit",timeZone:"Europe/Athens"});
        time.title = new Date(message.sentAt).toLocaleString(document.documentElement.lang, {timeZone:"Europe/Athens"}) + " (Europe/Athens)";
        const text = document.createElement("p");
        text.textContent = message.body; // Treat participant input strictly as text, never HTML.
        item.append(label, time, text); feed.append(item);
        if (feed.children.length > 300) feed.firstElementChild.remove();
        if (atBottom) feed.scrollTop = feed.scrollHeight;
    }
    async function poll() {
        if (stopped) return;
        if (!document.hidden) {
            try {
                const response = await fetch(root.dataset.url + "?after=" + after, {credentials:"same-origin",cache:"no-store"});
                if (response.status === 404 || response.status === 403) { end(); return; }
                if (!response.ok) throw new Error("Unavailable");
                const data = await response.json();
                for (const message of data.messages) { append(message); after = Math.max(after, message.id); }
                form.hidden = false;
                if (!sending && !pending) status.textContent = root.dataset.live;
            } catch { if (!sending && !pending) status.textContent = root.dataset.offline; }
        }
        if (!stopped) window.setTimeout(poll, 3000);
    }
    form.addEventListener("submit", async event => {
        event.preventDefault();
        if (sending || stopped || !body.value.trim()) return;
        // A retry uses the same ID until success, even after an uncertain HTTP outcome.
        if (pending && pending.text !== body.value) {
            status.textContent = root.dataset.failed;
            body.value = pending.text;
            return;
        }
        pending ??= {id:crypto.randomUUID(),text:body.value};
        sending = true; button.disabled = true; body.readOnly = true;
        const data = new FormData(form);
        data.set("ClientMessageId", pending.id); data.set("Body", pending.text);
        try {
            const response = await fetch(root.dataset.url, {method:"POST",body:data,credentials:"same-origin",cache:"no-store"});
            if (response.status === 404 || response.status === 403) { end(); return; }
            if (!response.ok) {
                status.textContent = response.status === 429 ? root.dataset.limited : root.dataset.failed;
                return;
            }
            // Poll in sequence rather than moving the cursor past unseen messages from another sender.
            await response.json(); body.value = ""; pending = null;
            status.textContent = root.dataset.live;
        } catch { status.textContent = root.dataset.failed; }
        finally { sending = false; button.disabled = false; body.readOnly = false; }
    });
    poll();
})();
