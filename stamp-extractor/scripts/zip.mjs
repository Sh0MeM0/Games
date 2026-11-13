// Minimal ZIP reader/writer (deflate, UTF-8 names) without external tools.
import { deflateRawSync, inflateRawSync, crc32 } from 'node:zlib';

export function readZip(buffer) {
  let eocd = buffer.length - 22;
  while (eocd >= 0 && buffer.readUInt32LE(eocd) !== 0x06054b50) eocd--;
  if (eocd < 0) throw new Error('Некорректный ZIP');
  const count = buffer.readUInt16LE(eocd + 10); let offset = buffer.readUInt32LE(eocd + 16);
  const files = {};
  for (let i = 0; i < count; i++) {
    if (buffer.readUInt32LE(offset) !== 0x02014b50) throw new Error('Некорректный каталог ZIP');
    const method = buffer.readUInt16LE(offset + 10), compressedSize = buffer.readUInt32LE(offset + 20);
    const nameLength = buffer.readUInt16LE(offset + 28), extra = buffer.readUInt16LE(offset + 30), comment = buffer.readUInt16LE(offset + 32);
    const local = buffer.readUInt32LE(offset + 42), name = buffer.toString('utf8', offset + 46, offset + 46 + nameLength);
    const start = local + 30 + buffer.readUInt16LE(local + 26) + buffer.readUInt16LE(local + 28);
    const compressed = buffer.subarray(start, start + compressedSize);
    if (!name.endsWith('/')) files[name] = method === 0 ? Buffer.from(compressed) : inflateRawSync(compressed);
    offset += 46 + nameLength + extra + comment;
  }
  return files;
}

function dosTime(date) {
  return { time: (date.getHours() << 11) | (date.getMinutes() << 5) | (date.getSeconds() >> 1),
    date: ((date.getFullYear() - 1980) << 9) | ((date.getMonth() + 1) << 5) | date.getDate() };
}

export function writeZip(entries, when = new Date(2026, 10, 13, 12, 0, 0)) {
  const locals = [], central = []; let offset = 0;
  const { time, date } = dosTime(when);
  for (const entry of entries) {
    const name = Buffer.from(entry.name, 'utf8'), data = entry.data, crc = crc32(data);
    const compressed = deflateRawSync(data, { level: 9 }), stored = compressed.length >= data.length;
    const body = stored ? data : compressed, method = stored ? 0 : 8;
    const header = Buffer.alloc(30);
    header.writeUInt32LE(0x04034b50, 0); header.writeUInt16LE(20, 4); header.writeUInt16LE(0x0800, 6); header.writeUInt16LE(method, 8);
    header.writeUInt16LE(time, 10); header.writeUInt16LE(date, 12); header.writeUInt32LE(crc, 14);
    header.writeUInt32LE(body.length, 18); header.writeUInt32LE(data.length, 22); header.writeUInt16LE(name.length, 26);
    locals.push(header, name, body);
    const record = Buffer.alloc(46);
    record.writeUInt32LE(0x02014b50, 0); record.writeUInt16LE(20, 4); record.writeUInt16LE(20, 6); record.writeUInt16LE(0x0800, 8);
    record.writeUInt16LE(method, 10); record.writeUInt16LE(time, 12); record.writeUInt16LE(date, 14); record.writeUInt32LE(crc, 16);
    record.writeUInt32LE(body.length, 20); record.writeUInt32LE(data.length, 24); record.writeUInt16LE(name.length, 28);
    record.writeUInt32LE(offset, 42);
    central.push(record, name);
    offset += 30 + name.length + body.length;
  }
  const directory = Buffer.concat(central), end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(entries.length, 8); end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(directory.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, directory, end]);
}
