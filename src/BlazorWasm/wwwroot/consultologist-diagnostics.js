window.consultologistDiagnostics = {
	getBrowserState: () => ({
		VisibilityState: document.visibilityState || "unknown",
		NavigatorOnLine:
			typeof navigator.onLine === "boolean" ? navigator.onLine : null,
		ServiceWorkerControlled: !!(
			navigator.serviceWorker && navigator.serviceWorker.controller
		)
	})
};
