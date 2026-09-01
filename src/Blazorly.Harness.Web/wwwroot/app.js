window.blazorly = {
    scrollBottom: function (element) {
        if (!element) return;
        // Pin only when the user is near the bottom already.
        const nearBottom = element.scrollHeight - element.scrollTop - element.clientHeight < 160;
        if (nearBottom) {
            element.scrollTop = element.scrollHeight;
        }
    },
    getTheme: function () {
        return localStorage.getItem("blazorly.theme") || "dark";
    },
    setTheme: function (theme) {
        localStorage.setItem("blazorly.theme", theme);
        document.documentElement.dataset.theme = theme;
        return theme;
    }
};
