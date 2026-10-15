import { vi } from "vitest";

type Listener = (event: MessageEvent<string>) => void;

/** The Activity stream as a page sees it: an EventSource a test pushes frames into, as the server does. */
class FakeEventSource {
  static opened: FakeEventSource[] = [];

  readonly url: string;
  private readonly listeners = new Map<string, Set<Listener>>();

  constructor(url: string) {
    this.url = url;
    FakeEventSource.opened.push(this);
  }

  addEventListener(type: string, listener: Listener): void {
    const set = this.listeners.get(type) ?? new Set<Listener>();
    set.add(listener);
    this.listeners.set(type, set);
  }

  removeEventListener(type: string, listener: Listener): void {
    this.listeners.get(type)?.delete(listener);
  }

  close(): void {
    this.listeners.clear();
  }

  emit(type: string, data: string): void {
    const event = { data } as MessageEvent<string>;
    this.listeners.get(type)?.forEach((listener) => listener(event));
  }
}

export type ActivityStreamHandle = {
  /** The server says an Activity entry was written: `activity.latest`, with the newest entry's id. */
  entryWritten(latestEventId: number): void;
  /** The server wrote a line to its log: a `system.log` frame, whose data is the text the server sent. */
  logLineWritten(frameData: string): void;
};

/** Puts the fake stream where the page opens its EventSource. Undo with `vi.unstubAllGlobals()`. */
export function stubActivityStream(): ActivityStreamHandle {
  FakeEventSource.opened = [];
  vi.stubGlobal("EventSource", FakeEventSource);
  const opened = () => {
    const [stream] = FakeEventSource.opened;
    if (!stream) throw new Error("The page has not opened the stream.");
    return stream;
  };
  return {
    entryWritten(latestEventId) {
      opened().emit(
        "activity.latest",
        JSON.stringify({ latest_event_id: latestEventId }),
      );
    },
    logLineWritten(frameData) {
      opened().emit("system.log", frameData);
    },
  };
}
