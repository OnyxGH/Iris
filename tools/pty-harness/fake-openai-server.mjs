import http from "node:http";
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let n = 0;
http.createServer(async (req, res) => {
  let body = ""; for await (const c of req) body += c;
  if (!req.url.includes("chat/completions")) { res.writeHead(200, {"content-type":"application/json"}); res.end(JSON.stringify({data:[{id:"Fake"}]})); return; }
  const parsed = JSON.parse(body);
  const hasToolResult = parsed.messages.some((m) => m.role === "tool");
  res.writeHead(200, { "content-type": "text/event-stream" });
  const send = (delta, finish = null) => res.write(`data: ${JSON.stringify({ id: "c" + n, object: "chat.completion.chunk", model: "Fake", choices: [{ index: 0, delta, finish_reason: finish }] })}\n\n`);
  n++;
  if (!hasToolResult) {
    for (const w of ["Let me ", "check the ", "directory."]) { send({ reasoning_content: w }); await sleep(150); }
    send({ content: "I'll run a command." }); await sleep(150);
    send({ tool_calls: [{ index: 0, id: "call_1", type: "function", function: { name: "bash", arguments: "" } }] });
    for (const part of ['{"command":', '"echo tool-ran', ' && echo line2"}']) { send({ tool_calls: [{ index: 0, function: { arguments: part } }] }); await sleep(150); }
    send({}, "tool_calls");
  } else {
    const text = "Done. Output was:\n\n```\ntool-ran\nline2\n```\n\n| a | b |\n|---|---|\n| 1 | **2** |\n\n- item one\n- item `two`\n";
    for (let i = 0; i < text.length; i += 12) { send({ content: text.slice(i, i + 12) }); await sleep(60); }
    send({}, "stop");
  }
  res.write(`data: ${JSON.stringify({ id: "u", object: "chat.completion.chunk", model: "Fake", choices: [], usage: { prompt_tokens: 1200, completion_tokens: 80, total_tokens: 1280 } })}\n\n`);
  res.end("data: [DONE]\n\n");
}).listen(8690, () => console.log("fake server on 8690"));
