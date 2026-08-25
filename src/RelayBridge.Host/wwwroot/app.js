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
    }
};
