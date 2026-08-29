// SPDX-License-Identifier: MPL-2.0

window.relayBridge = {
    copyText: async function (text) {
        await navigator.clipboard.writeText(text);
    },
    downloadText: function (fileName, text) {
        const blob = new Blob([text], { type: "text/plain;charset=utf-8" });
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
    },
    printPage: function () {
        window.print();
    },
    launchMicrosoftSetup: function () {
        window.location.href = "relaybridge-setup://start";
    },
    launchPrinterApply: function (uri) {
        if (!/^relaybridge-printer:\/\/apply\/[0-9a-f-]{36}$/i.test(uri)) {
            throw new Error("Invalid RelayBridge printer apply request.");
        }
        window.location.href = uri;
        window.setTimeout(async function waitForRelayBridge() {
            for (let attempt = 0; attempt < 60; attempt++) {
                try {
                    const response = await fetch("/health", { cache: "no-store" });
                    if (response.ok) {
                        window.location.reload();
                        return;
                    }
                } catch {
                    // Expected while the approved helper restarts the service.
                }
                await new Promise(resolve => window.setTimeout(resolve, 1000));
            }
        }, 4000);
    }
};
