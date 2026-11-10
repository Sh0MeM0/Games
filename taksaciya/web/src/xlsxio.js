/*
 * Минимальная работа с .xlsx/.xlsm без сторонних библиотек:
 *  - XlsxWriter: запись книги (ZIP без сжатия, inline-строки, жирные заголовки, ширины колонок);
 *  - readZip: чтение нужных файлов из .xlsx/.xlsm (распаковка через DecompressionStream).
 */
(function (root) {
  'use strict';
  var CRC_TABLE = (function () {
    var t = new Uint32Array(256);
    for (var n = 0; n < 256; n++) {
      var c = n;
      for (var k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1;
      t[n] = c >>> 0;
    }
    return t;
  })();
  function crc32(bytes) {
    var c = 0xFFFFFFFF;
    for (var i = 0; i < bytes.length; i++) c = CRC_TABLE[(c ^ bytes[i]) & 0xFF] ^ (c >>> 8);
    return (c ^ 0xFFFFFFFF) >>> 0;
  }
  function esc(s) {
    return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/g, '');
  }
  function colName(i) { var s = ''; i++; while (i > 0) { var m = (i - 1) % 26; s = String.fromCharCode(65 + m) + s; i = Math.floor((i - 1) / 26); } return s; }

  function zipStore(files) {          // files: [{name, data: Uint8Array}]
    var enc = new TextEncoder(), parts = [], central = [], offset = 0;
    files.forEach(function (f) {
      var name = enc.encode(f.name), data = f.data, crc = crc32(data);
      var h = new DataView(new ArrayBuffer(30));
      h.setUint32(0, 0x04034b50, true); h.setUint16(4, 20, true); h.setUint16(6, 0x0800, true);
      h.setUint16(8, 0, true); h.setUint16(10, 0, true); h.setUint16(12, 0x21, true);
      h.setUint32(14, crc, true); h.setUint32(18, data.length, true); h.setUint32(22, data.length, true);
      h.setUint16(26, name.length, true); h.setUint16(28, 0, true);
      parts.push(new Uint8Array(h.buffer), name, data);
      var c = new DataView(new ArrayBuffer(46));
      c.setUint32(0, 0x02014b50, true); c.setUint16(4, 20, true); c.setUint16(6, 20, true); c.setUint16(8, 0x0800, true);
      c.setUint16(10, 0, true); c.setUint16(12, 0, true); c.setUint16(14, 0x21, true);
      c.setUint32(16, crc, true); c.setUint32(20, data.length, true); c.setUint32(24, data.length, true);
      c.setUint16(28, name.length, true); c.setUint32(42, offset, true);
      central.push(new Uint8Array(c.buffer), name);
      offset += 30 + name.length + data.length;
    });
    var csize = central.reduce(function (s, p) { return s + p.length; }, 0);
    var e = new DataView(new ArrayBuffer(22));
    e.setUint32(0, 0x06054b50, true); e.setUint16(8, files.length, true); e.setUint16(10, files.length, true);
    e.setUint32(12, csize, true); e.setUint32(16, offset, true);
    var all = parts.concat(central, [new Uint8Array(e.buffer)]);
    var total = all.reduce(function (s, p) { return s + p.length; }, 0), out = new Uint8Array(total), pos = 0;
    all.forEach(function (p) { out.set(p, pos); pos += p.length; });
    return out;
  }

  /**
   * sheets: [{name, rows: [[cell...]], widths: [число символов], bold: [индексы строк], merge: []}]
   * cell: строка | число | null | {v, b: true (жирный), w: true (перенос)}
   */
  function writeXlsx(sheets, title) {
    var enc = new TextEncoder(), files = [];
    function add(name, text) { files.push({ name: name, data: enc.encode(text) }); }
    var sheetsXml = '', rels = '', ctypes = '';
    sheets.forEach(function (sh, i) {
      var n = i + 1, rowsXml = '';
      sh.rows.forEach(function (row, r) {
        var cells = '';
        (row || []).forEach(function (cell, c) {
          if (cell === null || cell === undefined || cell === '') return;
          var v = cell, style = 0;
          if (typeof cell === 'object') { v = cell.v; style = cell.b ? (cell.w ? 3 : 1) : (cell.w ? 2 : 0); }
          if (sh.bold && sh.bold.indexOf(r) >= 0) style = 1;
          var ref = colName(c) + (r + 1), s = style ? ' s="' + style + '"' : '';
          if (typeof v === 'number' && isFinite(v)) cells += '<c r="' + ref + '"' + s + '><v>' + v + '</v></c>';
          else if (v !== null && v !== undefined && v !== '') cells += '<c r="' + ref + '"' + s + ' t="inlineStr"><is><t xml:space="preserve">' + esc(v) + '</t></is></c>';
        });
        rowsXml += '<row r="' + (r + 1) + '">' + cells + '</row>';
      });
      var cols = '';
      if (sh.widths && sh.widths.length) {
        cols = '<cols>' + sh.widths.map(function (w, c) { return '<col min="' + (c + 1) + '" max="' + (c + 1) + '" width="' + w + '" customWidth="1"/>'; }).join('') + '</cols>';
      }
      var merges = '';
      if (sh.merge && sh.merge.length) merges = '<mergeCells count="' + sh.merge.length + '">' + sh.merge.map(function (m) { return '<mergeCell ref="' + m + '"/>'; }).join('') + '</mergeCells>';
      add('xl/worksheets/sheet' + n + '.xml', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
        '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetViews><sheetView workbookViewId="0"' +
        (sh.freeze ? '><pane ySplit="' + sh.freeze + '" topLeftCell="A' + (sh.freeze + 1) + '" activePane="bottomLeft" state="frozen"/></sheetView>' : '/>') +
        '</sheetViews>' + cols + '<sheetData>' + rowsXml + '</sheetData>' + merges +
        '<pageMargins left="0.5" right="0.5" top="0.6" bottom="0.6" header="0.3" footer="0.3"/><pageSetup orientation="landscape" paperSize="9"/></worksheet>');
      var safe = String(sh.name).replace(/[\\\/\?\*\[\]:]/g, ' ').slice(0, 31);
      sheetsXml += '<sheet name="' + esc(safe) + '" sheetId="' + n + '" r:id="rId' + n + '"/>';
      rels += '<Relationship Id="rId' + n + '" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet' + n + '.xml"/>';
      ctypes += '<Override PartName="/xl/worksheets/sheet' + n + '.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>';
    });
    var k = sheets.length + 1;
    rels += '<Relationship Id="rId' + k + '" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>';
    add('[Content_Types].xml', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">' +
      '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>' +
      '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>' +
      '<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>' +
      '<Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>' + ctypes + '</Types>');
    add('_rels/.rels', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' +
      '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>' +
      '<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/></Relationships>');
    add('docProps/core.xml', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>' + esc(title || 'Таксация') + '</dc:title><dc:creator>Таксация Лайт</dc:creator></cp:coreProperties>');
    add('xl/workbook.xml', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>' + sheetsXml + '</sheets></workbook>');
    add('xl/_rels/workbook.xml.rels', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' + rels + '</Relationships>');
    add('xl/styles.xml', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">' +
      '<fonts count="2"><font><sz val="10"/><name val="Arial"/></font><font><b/><sz val="10"/><name val="Arial"/></font></fonts>' +
      '<fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>' +
      '<borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>' +
      '<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>' +
      '<cellXfs count="4"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>' +
      '<xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>' +
      '<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment wrapText="1" vertical="top"/></xf>' +
      '<xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment wrapText="1" vertical="top"/></xf></cellXfs>' +
      '</styleSheet>');
    return zipStore(files);
  }

  /** Чтение указанных файлов из ZIP (.xlsx/.xlsm). Возвращает Promise<{name: text}>. */
  function readZip(buffer, wanted) {
    var u8 = new Uint8Array(buffer), dv = new DataView(buffer);
    var eocd = -1;
    for (var i = u8.length - 22; i >= Math.max(0, u8.length - 70000); i--) {
      if (dv.getUint32(i, true) === 0x06054b50) { eocd = i; break; }
    }
    if (eocd < 0) return Promise.reject(new Error('Файл не похож на книгу Excel (.xlsx/.xlsm).'));
    var count = dv.getUint16(eocd + 10, true), cdOff = dv.getUint32(eocd + 16, true);
    var dec = new TextDecoder('utf-8'), entries = {}, p = cdOff;
    for (var n = 0; n < count; n++) {
      var method = dv.getUint16(p + 10, true), csize = dv.getUint32(p + 20, true);
      var nlen = dv.getUint16(p + 28, true), xlen = dv.getUint16(p + 30, true), clen = dv.getUint16(p + 32, true);
      var loff = dv.getUint32(p + 42, true), name = dec.decode(u8.subarray(p + 46, p + 46 + nlen));
      entries[name] = { method: method, csize: csize, loff: loff };
      p += 46 + nlen + xlen + clen;
    }
    var names = typeof wanted === 'function' ? Object.keys(entries).filter(wanted) : wanted.filter(function (w) { return entries[w]; });
    return Promise.all(names.map(function (name) {
      var e = entries[name], lo = e.loff;
      var start = lo + 30 + dv.getUint16(lo + 26, true) + dv.getUint16(lo + 28, true);
      var data = u8.subarray(start, start + e.csize);
      if (e.method === 0) return Promise.resolve([name, dec.decode(data)]);
      if (typeof DecompressionStream === 'undefined') return Promise.reject(new Error('Браузер не умеет распаковывать файлы Excel. Обновите браузер.'));
      var ds = new DecompressionStream('deflate-raw');
      var stream = new Blob([data]).stream().pipeThrough(ds);
      return new Response(stream).arrayBuffer().then(function (ab) { return [name, dec.decode(new Uint8Array(ab))]; });
    })).then(function (pairs) { var o = {}; pairs.forEach(function (x) { o[x[0]] = x[1]; }); o._names = Object.keys(entries); return o; });
  }

  root.XlsxIO = { writeXlsx: writeXlsx, readZip: readZip, colName: colName };
})(typeof self !== 'undefined' ? self : this);
