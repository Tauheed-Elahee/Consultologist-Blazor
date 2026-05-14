(function () {
  const streams = new Map();

  function closeStream(streamId) {
    const stream = streams.get(streamId);

    if (!stream) {
      return;
    }

    stream.closed = true;
    stream.source.close();
    streams.delete(streamId);
  }

  window.consultGenerationJobEvents = {
    start(url, dotNetReference) {
      if (typeof EventSource === "undefined") {
        dotNetReference.invokeMethodAsync(
          "OnConsultGenerationJobStreamError",
          "EventSource is not supported by this browser."
        );
        return null;
      }

      const streamId =
        typeof crypto !== "undefined" && typeof crypto.randomUUID === "function"
          ? crypto.randomUUID()
          : `${Date.now()}-${Math.random()}`;

      const source = new EventSource(url);
      const stream = { source, closed: false };
      streams.set(streamId, stream);

      const forward = (eventName) => (event) => {
        dotNetReference.invokeMethodAsync(
          "OnConsultGenerationJobEvent",
          eventName,
          event.data
        );

        if (eventName === "done" || eventName === "error") {
          closeStream(streamId);
        }
      };

      source.addEventListener("snapshot", forward("snapshot"));
      source.addEventListener("section-completed", forward("section-completed"));
      source.addEventListener("section-failed", forward("section-failed"));
      source.addEventListener("heartbeat", forward("heartbeat"));
      source.addEventListener("done", forward("done"));
      source.addEventListener("error", (event) => {
        if (typeof event.data !== "string") {
          return;
        }

        forward("error")(event);
      });

      source.onerror = () => {
        if (stream.closed) {
          return;
        }

        closeStream(streamId);
        dotNetReference.invokeMethodAsync(
          "OnConsultGenerationJobStreamError",
          "Consult generation event stream failed."
        );
      };

      return streamId;
    },

    close(streamId) {
      closeStream(streamId);
    },
  };
})();
