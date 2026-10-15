export interface SseMessage {
  event: string;
  data: string;
}

/**
 * Incremental Server-Sent Events parser (WHATWG event-stream format).
 * Network chunks may split or merge events arbitrarily, so input is buffered
 * until a complete line is available.
 */
export class SseParser {
  private buffer = "";
  private eventName = "";
  private dataLines: string[] = [];

  push(chunk: string): SseMessage[] {
    this.buffer += chunk;
    const messages: SseMessage[] = [];

    for (;;) {
      const index = this.buffer.search(/[\r\n]/);
      if (index < 0) {
        break;
      }
      // A trailing "\r" may be the first half of "\r\n"; wait for the next chunk.
      if (this.buffer[index] === "\r" && index === this.buffer.length - 1) {
        break;
      }

      const line = this.buffer.slice(0, index);
      const separator = this.buffer.startsWith("\r\n", index) ? 2 : 1;
      this.buffer = this.buffer.slice(index + separator);
      this.processLine(line, messages);
    }

    return messages;
  }

  private processLine(line: string, messages: SseMessage[]): void {
    if (line === "") {
      if (this.dataLines.length > 0) {
        messages.push({ event: this.eventName || "message", data: this.dataLines.join("\n") });
      }
      this.eventName = "";
      this.dataLines = [];
      return;
    }

    if (line.startsWith(":")) {
      return; // comment / keep-alive
    }

    const colon = line.indexOf(":");
    const field = colon < 0 ? line : line.slice(0, colon);
    let value = colon < 0 ? "" : line.slice(colon + 1);
    if (value.startsWith(" ")) {
      value = value.slice(1);
    }

    if (field === "event") {
      this.eventName = value;
    } else if (field === "data") {
      this.dataLines.push(value);
    }
  }
}
