// Builds a version-locked copy of the installed JTDX. Usage:
// node build-native.cjs ORIGINAL_EXE STUB_BIN OUTPUT_EXE
const fs = require('node:fs');
const { createHash } = require('node:crypto');
const path = require('node:path');

const expectedSha = '6b6718432edb55b3e3c8a5b884522bdc5ef5e466bf074e9dac09cc0b64ce399c';
const expectedStubSha = '3577ef74a24d03813d52ef8996aacef21ecb22c74d4f843da5bd38b5e082f289';
const expectedOutputSha = '0abba5ea8429ce4158df0d2822d9a3108dd629a7c17e8332a8d40736ed6a413a';
const [originalPath, stubPath, outputPath] = process.argv.slice(2);
const sha = data => createHash('sha256').update(data).digest('hex');
const align = (n, a) => Math.ceil(n / a) * a;
function assert(ok, message) { if (!ok) throw new Error(message); }

assert(originalPath && stubPath && outputPath, 'Supply original, assembled stub, and output.');
assert(path.resolve(originalPath).toLowerCase() !== path.resolve(outputPath).toLowerCase(),
    'The builder only produces a separate copy.');
const original = fs.readFileSync(originalPath);
assert(sha(original) === expectedSha, 'Unsupported JTDX binary.');
const stub = fs.readFileSync(stubPath);
assert(sha(stub) === expectedStubSha, 'Unexpected AutoCQ patch code.');
const pe = original.readUInt32LE(0x3c);
const opt = pe + 24;
const sectionsAt = opt + original.readUInt16LE(pe + 20);
const sectionCount = original.readUInt16LE(pe + 6);
const imageSize = original.readUInt32LE(opt + 56);
const headersSize = original.readUInt32LE(opt + 60);
const fileAlignment = original.readUInt32LE(opt + 36);
const sectionAlignment = original.readUInt32LE(opt + 32);
const exceptionDirectory = opt + 112 + 3 * 8;
assert(original.readUInt16LE(opt) === 0x20b && imageSize === 0x2d17000, 'Unexpected PE layout.');
assert(sectionsAt + 3 * 40 + sectionCount * 40 <= headersSize, 'No room for three PE sections.');
assert(original.readUInt32LE(opt + 112 + 4 * 8) === 0, 'A signed binary must not be modified here.');

const sections = [];
for (let i = 0; i < sectionCount; i++) {
    const p = sectionsAt + i * 40;
    sections.push({ va: original.readUInt32LE(p + 12), size: original.readUInt32LE(p + 8), raw: original.readUInt32LE(p + 20) });
}
function raw(rva) {
    const s = sections.find(x => rva >= x.va && rva < x.va + x.size && x.raw);
    assert(s, `No original section at RVA ${rva.toString(16)}.`);
    return s.raw + rva - s.va;
}

const hooks = [
    [0x1eefbe, '488d5424404889f1e855edffff', 0, 0x1eef00],
    [0x1edd33, '488d9c24f0000000', 1, 0x1edd20],
    [0x1eddf0, '83f82f0f875dffffff', 2, 0x1edd20],
    [0x1ee2ce, '85c00f849b010000', 3, 0x1edd20],
    [0x1ee2e2, '0fb644246388442438', 4, 0x1edd20],
    [0x09ef5b, '8b8394050000c6834c06000000', 32, 0x09eeb0],
    [0x09f3ad, '488b9038060000488b05dd18b80248898424d0000000ffd0', 5, 0x09eeb0],
    [0x09f766, '488b83900000004889f9488b9038060000488b8424d0000000ffd0', 6, 0x09eeb0],
    [0x09f85e, '4889d9e8ba3a0200', 22, 0x09eeb0],
    [0x0c4fd2, 'e849622500', 23, 0x0c3320],
    [0x0a8984, '80bb8b060000000f85cf020000', 9, 0x0a8770],
    [0x0a8ec5, '80bb86060000000f859e150000', 10, 0x0a8770],
    [0x10eef1, '8b8348050000', 24, 0x10d440],
    [0x0ab020, '488d15c9c22a01', 25, 0x0a8770],
    [0x0ab26a, '488d15afc02a01', 26, 0x0a8770],
    [0x0ab4e0, '488d1561be2a01', 27, 0x0a8770],
    [0x0a9e7d, '80bb86060000000f84b6070000', 28, 0x0a8770],
    [0x0a8dd6, '80bbf9050000000f852b240000', 29, 0x0a8770],
    [0x0a9e51, '80bb86060000000f84f5080000', 30, 0x0a8770],
    [0x0a9f49, '488d15bbd52a01', 31, 0x0a8770],
];
const lastPacketType = raw(0x21e86a);
assert(original[lastPacketType] === 51 && original.subarray(lastPacketType - 2, lastPacketType).toString('hex') === '83f8',
    'Unexpected network reader type limit.');
const offsets = Array.from({ length: 34 }, (_, i) => stub.readUInt32LE(i * 4));
assert(offsets[14] === stub.length && stub.length < sectionAlignment, 'Invalid stub map or code size.');
for (const [rva, hex, index] of hooks) {
    assert(original.subarray(raw(rva), raw(rva) + hex.length / 2).toString('hex') === hex,
        `Instruction check failed at ${rva.toString(16)}.`);
    assert(offsets[index] >= offsets.length * 4 && offsets[index] < stub.length, 'Invalid hook entry.');
}

// In rc10 the two UI-tab paths in the TriggerCQ signal handler gate
// Enable TX on PromptToLog. The rest of the handler already checks
// AcceptUDPRequests, Send, and whether TX is enabled. Preserve those
// checks and change only these two results to true, so that the module
// need not change the operator's setting or the QSO completion logic.
const triggerCqPromptChecks = [
    [0x3078f9, 'e822390100'],
    [0x307974, 'e8a7380100'],
];
for (const [rva, expected] of triggerCqPromptChecks) {
    const start = raw(rva);
    assert(original.subarray(start, start + 5).toString('hex') === expected,
        `Unexpected TriggerCQ logging gate at ${rva.toString(16)}.`);
}

// guiUpdate compares the previous terminal message with the current DX
// callsign. QString::indexOf returns -1 when the call is absent. A terminal
// message sent to a new station must be eligible for the ordinary log path.
const signoffLogGate = raw(0x110e0c);
const logGateBytes = Buffer.from('83f8ff410f95c6e9b7e6ffff', 'hex');
assert(original.subarray(signoffLogGate, signoffLogGate + logGateBytes.length).equals(logGateBytes),
    'Unexpected first-terminal-message logging condition.');

const codeRva = imageSize;
const stateRva = codeRva + sectionAlignment;
const unwindRva = stateRva + sectionAlignment;
const oldTableAt = raw(original.readUInt32LE(exceptionDirectory));
const oldTableBytes = original.readUInt32LE(exceptionDirectory + 4);
assert(oldTableBytes % 12 === 0, 'Invalid original unwind table.');
const originalFunctions = new Map();
for (let n = 0; n < oldTableBytes; n += 12) {
    const p = oldTableAt + n;
    originalFunctions.set(original.readUInt32LE(p), original.subarray(p, p + 12));
}

// Each jump-in stub uses exactly its original stack frame. Chained unwind
// metadata lets the Windows unwinder recover that frame while in the stub.
const extra = [];
const info = [];
const chainRanges = hooks.map(([, , index, parent]) => [index, parent]);
chainRanges.push([19, 0x0a8770], [20, 0x0a8770]);
for (const [index, parent] of chainRanges) {
    const entry = Buffer.alloc(16);
    entry[0] = 0x21; // v1, UNW_FLAG_CHAININFO
    const chain = originalFunctions.get(parent);
    assert(chain, 'Missing original unwind parent.');
    chain.copy(entry, 4);
    info.push(entry);
    const entryRva = codeRva + offsets[index];
    const next = [...offsets].filter(x => x > offsets[index]).sort((a, b) => a - b)[0];
    extra.push({ begin: entryRva, end: codeRva + next, index: info.length - 1 });
}
// renew_lease and resolve_tick are ordinary Win64 callees and allocate 0x28.
// The fast return in resolve_tick precedes the stack allocation.
for (const [begin, end, prolog] of [[15, 16, 4], [13, 18, offsets[17] - offsets[13]]]) {
    info.push(Buffer.from([1, prolog, 1, 0, prolog, 0x42, 0, 0]));
    extra.push({ begin: codeRva + offsets[begin], end: codeRva + offsets[end], index: info.length - 1 });
}
extra.sort((a, b) => a.begin - b.begin);
for (let n = 1; n < extra.length; n++) assert(extra[n - 1].end <= extra[n].begin, 'Overlapping unwind ranges.');
const tableSize = oldTableBytes + extra.length * 12;
const unwind = Buffer.alloc(tableSize + info.reduce((n, b) => n + b.length, 0));
original.copy(unwind, 0, oldTableAt, oldTableAt + oldTableBytes);
let infoAt = tableSize;
for (let n = 0; n < extra.length; n++) {
    const e = extra[n];
    const p = oldTableBytes + n * 12;
    unwind.writeUInt32LE(e.begin, p);
    unwind.writeUInt32LE(e.end, p + 4);
    unwind.writeUInt32LE(unwindRva + infoAt, p + 8);
    info[e.index].copy(unwind, infoAt);
    infoAt += info[e.index].length;
}

const codeRaw = align(original.length, fileAlignment);
const stateRaw = codeRaw + align(stub.length, fileAlignment);
const unwindRaw = stateRaw + fileAlignment;
const output = Buffer.alloc(unwindRaw + align(unwind.length, fileAlignment));
original.copy(output);
output[lastPacketType] = 52; // 53 is consumed by the source-checked datagram hook
for (const [rva] of triggerCqPromptChecks)
    Buffer.from('b801000000', 'hex').copy(output, raw(rva)); // mov eax, 1
output[signoffLogGate + 5] = 0x94; // sete r14b: previous message was to another station
stub.copy(output, codeRaw);
unwind.copy(output, unwindRaw);
for (const [rva, hex, index] of hooks) {
    const p = raw(rva);
    output.fill(0x90, p, p + hex.length / 2);
    output[p] = 0xe9;
    output.writeInt32LE(codeRva + offsets[index] - (rva + 5), p + 1);
}
function addSection(i, name, va, size, ptr, flags) {
    const p = sectionsAt + (sectionCount + i) * 40;
    output.fill(0, p, p + 40);
    output.write(name, p, 8, 'ascii');
    output.writeUInt32LE(size, p + 8);
    output.writeUInt32LE(va, p + 12);
    output.writeUInt32LE(align(size, fileAlignment), p + 16);
    output.writeUInt32LE(ptr, p + 20);
    output.writeUInt32LE(flags, p + 36);
}
addSection(0, '.acq', codeRva, stub.length, codeRaw, 0x60000020);
addSection(1, '.acqdata', stateRva, 24, stateRaw, 0xc0000040);
addSection(2, '.acqinfo', unwindRva, unwind.length, unwindRaw, 0x40000040);
output.writeUInt16LE(sectionCount + 3, pe + 6);
output.writeUInt32LE(original.readUInt32LE(opt + 4) + align(stub.length, fileAlignment), opt + 4);
output.writeUInt32LE(original.readUInt32LE(opt + 8) + fileAlignment + align(unwind.length, fileAlignment), opt + 8);
output.writeUInt32LE(align(unwindRva + unwind.length, sectionAlignment), opt + 56);
output.writeUInt32LE(0, opt + 64); // original image checksum no longer applies
output.writeUInt32LE(unwindRva, exceptionDirectory);
output.writeUInt32LE(tableSize, exceptionDirectory + 4);
assert(sha(output) === expectedOutputSha, 'Patched JTDX does not match the verified release.');
fs.writeFileSync(outputPath, output, { flag: 'wx' });
process.stdout.write(`Verified ${hooks.length} original instruction ranges.\n`);
process.stdout.write(`Adjusted ${triggerCqPromptChecks.length} TriggerCQ logging gates.\n`);
process.stdout.write('Corrected the callsign check for logging consecutive terminal messages.\n');
process.stdout.write(`Original ${expectedSha}\nPatched  ${sha(output)}\n`);
