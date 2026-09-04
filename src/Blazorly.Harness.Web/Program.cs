using Blazorly.Harness.Web;

// Thin entry: everything lives in UiHost so the packaged `blazorly` launcher can
// boot the same UI (`blazorly serve`) without duplicating the host.

return await UiHost.RunAsync(args);
