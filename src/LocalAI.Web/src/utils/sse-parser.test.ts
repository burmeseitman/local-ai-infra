import { describe, expect, it } from "vitest";
import { SseParser } from "./sse-parser";

function feed(chunks: string[]) {
  const parser = new SseParser();
  return chunks.flatMap((chunk) => parser.push(chunk));
}

describe("SseParser", () => {
  const stream = 'event: start\ndata: {"a":1}\n\nevent: delta\ndata: {"content":"hé"}\n\n';

  it("parses complete events", () => {
    expect(feed([stream])).toEqual([
      { event: "start", data: '{"a":1}' },
      { event: "delta", data: '{"content":"hé"}' },
    ]);
  });

  it.each([1, 2, 5, 13])("reassembles events split into %i-character chunks", (size) => {
    const chunks: string[] = [];
    for (let i = 0; i < stream.length; i += size) {
      chunks.push(stream.slice(i, i + size));
    }
    expect(feed(chunks)).toHaveLength(2);
    expect(feed(chunks)[1]).toEqual({ event: "delta", data: '{"content":"hé"}' });
  });

  it("handles CRLF split across chunks, comments and multi-line data", () => {
    const events = feed([": keep-alive\r", "\n", "data: line1\r\ndata: line2\r", "\n\r\n"]);
    expect(events).toEqual([{ event: "message", data: "line1\nline2" }]);
  });

  it("does not emit an event until the blank line arrives", () => {
    const parser = new SseParser();
    expect(parser.push('event: done\ndata: {"x":1}\n')).toEqual([]);
    expect(parser.push("\n")).toEqual([{ event: "done", data: '{"x":1}' }]);
  });
});
