"use strict";

document.addEventListener("click", async function (event) {
    const button = event.target.closest("[data-copy]");
    if (!button) return;
    const source = button.getAttribute("data-copy");
    const element = document.getElementById(source);
    const value = element ? (element.value || element.textContent).trim() : source;
    try {
        await navigator.clipboard.writeText(value);
        const original = button.textContent;
        button.textContent = document.documentElement.lang === "el" ? "Αντιγράφηκε" : "Copied";
        window.setTimeout(() => { button.textContent = original; }, 1800);
    } catch {
        if (element && typeof element.select === "function") element.select();
        const feedback = document.getElementById("copy-feedback");
        if (feedback) feedback.textContent = document.documentElement.lang === "el" ? "Επιλέξτε τον σύνδεσμο και αντιγράψτε τον." : "Select the link and copy it.";
    }
});
