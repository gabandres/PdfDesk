namespace InkvoltPdf.Core;

/// <summary>
/// The Acrobat object model form scripts see, written in JavaScript over a private host object (h). Evaluates to a
/// function taking h and returning { begin, end } for the host's event plumbing; h itself never becomes global.
/// Only what is defined here exists. Outward calls (app.launchURL, submitForm, mailDoc/mailForm, print, importAnFDF)
/// only reach the host, which asks the user before anything leaves the document; there is no importDataObject,
/// exportDataObject, SOAP, Net or file access.
/// </summary>
internal static class JsPrelude
{
    public const string Source = """
(function (h) {
  'use strict';
  var G = globalThis;

  // Built-ins that make one big string in a single call are checked before they allocate (the engine's memory limit
  // only notices afterwards): no result over MAX_TEXT characters. Arrays are capped by the engine (MaxArraySize).
  var MAX_TEXT = 1048576;
  function tooLong() { throw new RangeError('Form scripts can\'t build text longer than ' + MAX_TEXT + ' characters'); }
  function guard(proto, name, wrap) {
    var native = proto[name];
    Object.defineProperty(proto, name, { value: wrap(native), writable: true, configurable: true, enumerable: false });
  }
  var sp = String.prototype;
  guard(sp, 'repeat', function (native) {
    return function repeat(count) {
      var n = Number(count);
      if (this !== null && this !== undefined && String(this).length * n > MAX_TEXT) tooLong();
      return native.call(this, count);
    };
  });
  function padder(native) {
    return function (maxLength, fill) {
      var n = Number(maxLength);
      if (n > MAX_TEXT && this !== null && this !== undefined && n > String(this).length && (fill === undefined || String(fill) !== '')) tooLong();
      return native.call(this, maxLength, fill);
    };
  }
  guard(sp, 'padStart', padder);
  guard(sp, 'padEnd', padder);
  guard(sp, 'concat', function (native) {
    return function concat() {
      if (this === null || this === undefined) return native.apply(this, arguments);
      var parts = [], total = String(this).length;
      for (var i = 0; i < arguments.length; i++) {
        parts.push(String(arguments[i]));
        total += parts[i].length;
        if (total > MAX_TEXT) tooLong();
      }
      return native.apply(String(this), parts);
    };
  });
  function replacer(native, all) {
    return function (pattern, replacement) {
      if (this === null || this === undefined) return native.call(this, pattern, replacement);
      var s = String(this);
      if (typeof replacement === 'function') {
        var f = replacement, total = s.length;
        replacement = function () {
          var r = String(f.apply(this, arguments));
          total += r.length;
          if (total > MAX_TEXT) tooLong();
          return r;
        };
      } else {
        // Bound the result: every match takes the replacement, whose $-patterns can each bring back up to the whole text.
        var text = String(replacement), dollars = text.split('$').length - 1;
        var each = text.length + dollars * s.length;
        if (s.length + each > MAX_TEXT) {
          var global = all || (pattern instanceof RegExp && pattern.global), count = 1;
          if (global) {
            count = pattern instanceof RegExp ? (s.match(pattern) || []).length : s.split(String(pattern)).length - 1;
            if (pattern instanceof RegExp) pattern.lastIndex = 0;
          }
          if (s.length + count * each > MAX_TEXT) tooLong();
        }
      }
      return native.call(s, pattern, replacement);
    };
  }
  guard(sp, 'replace', function (native) { return replacer(native, false); });
  guard(sp, 'replaceAll', function (native) { return replacer(native, true); });
  var joining = [];
  guard(Array.prototype, 'join', function (native) {
    return function join(separator) {
      var o = Object(this), len = Math.min(Number(o.length) || 0, 4294967295);
      if (joining.indexOf(o) >= 0) return ''; // an array inside itself
      var sep = separator === undefined ? ',' : String(separator), parts = [], total = 0;
      joining.push(o);
      try {
        for (var i = 0; i < len; i++) {
          var e = o[i], t = e === undefined || e === null ? '' : String(e);
          total += t.length + (i > 0 ? sep.length : 0);
          if (total > MAX_TEXT) tooLong();
          parts.push(t);
        }
      } finally {
        joining.pop();
      }
      return native.call(parts, sep);
    };
  });

  var color = {
    transparent: ['T'], black: ['G', 0], white: ['G', 1], red: ['RGB', 1, 0, 0], green: ['RGB', 0, 1, 0],
    blue: ['RGB', 0, 0, 1], cyan: ['CMYK', 1, 0, 0, 0], magenta: ['CMYK', 0, 1, 0, 0], yellow: ['CMYK', 0, 0, 1, 0],
    dkGray: ['G', 0.25], gray: ['G', 0.5], ltGray: ['G', 0.75],
    equal: function (a, b) {
      if (!a || !b || a.length !== b.length) return false;
      for (var i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
      return true;
    },
    convert: function (c) { return c; }
  };
  G.color = color;
  G.display = { visible: 0, hidden: 1, noPrint: 2, noView: 3 };
  G.border = { s: 'solid', b: 'beveled', d: 'dashed', i: 'inset', u: 'underline' };
  G.style = { ch: 'check', cr: 'cross', di: 'diamond', ci: 'circle', st: 'star', sq: 'square' };

  function Field(name) { Object.defineProperty(this, 'name', { value: name, enumerable: true }); }
  var fp = Field.prototype;
  ['value', 'valueAsString', 'type', 'readonly', 'display', 'hidden', 'required', 'exportValues', 'numItems',
   'defaultValue', 'multiline', 'password', 'charLimit', 'textColor', 'fillColor', 'strokeColor', 'borderColor',
   'textSize', 'textFont', 'userName', 'alignment', 'borderStyle', 'lineWidth', 'print', 'doNotScroll',
   'doNotSpellCheck', 'rotation', 'comb', 'editable', 'multipleSelection', 'commitOnSelChange', 'buttonCaption'
  ].forEach(function (p) {
    Object.defineProperty(fp, p, {
      get: function () { return h.get(this.name, p); },
      set: function (v) { if (p !== 'valueAsString' && p !== 'type' && p !== 'exportValues' && p !== 'numItems') h.set(this.name, p, v); },
      enumerable: true
    });
  });
  fp.getArray = function () { return h.kids(this.name).map(function (n) { return new Field(n); }); };
  fp.getItemAt = function (i, bExport) { return h.item(this.name, i, bExport !== false); };
  fp.isBoxChecked = function (i) { return h.boxChecked(this.name, i | 0); };
  fp.isDefaultChecked = function () { return false; };
  fp.checkThisBox = function (i, b) { h.checkBox(this.name, i | 0, b !== false); };
  var noop = function () {};
  ['setAction', 'setLock', 'buttonSetCaption', 'buttonSetIcon', 'buttonImportIcon', 'signatureSetSeedValue'
  ].forEach(function (n) { fp[n] = noop; });
  fp.buttonGetCaption = function () { return ''; };
  fp.setFocus = function () { h.focus(this.name); };
  fp.setItems = function (items) { h.items('set', this.name, items ? Array.prototype.slice.call(items) : []); };
  fp.insertItemAt = function (cName, cExport, nIdx) {
    if (cName !== null && typeof cName === 'object') { cExport = cName.cExport; nIdx = cName.nIdx; cName = cName.cName; }
    h.items('insert', this.name, cName === undefined ? '' : String(cName), cExport, nIdx);
  };
  fp.deleteItemAt = function (nIdx) {
    if (nIdx !== null && typeof nIdx === 'object') nIdx = nIdx.nIdx;
    h.items('delete', this.name, nIdx);
  };
  fp.clearItems = function () { h.items('clear', this.name); };

  G.getField = function (name) {
    if (name === undefined || name === null) return null;
    name = String(name);
    return h.exists(name) ? new Field(name) : null;
  };
  G.getNthFieldName = function (i) { return h.nth(i); };
  Object.defineProperty(G, 'numFields', { get: function () { return h.count(); }, configurable: true });
  G.calculateNow = noop;
  G.dirty = false;
  G.calculate = true;
  G.documentFileName = '';
  Object.defineProperty(G, 'pageNum', { get: function () { return h.pageNum(); }, set: function (n) { h.setPageNum(n); }, configurable: true });
  Object.defineProperty(G, 'numPages', { get: function () { return h.numPages(); }, configurable: true });
  Object.defineProperty(G, 'title', { get: function () { return h.info('Title') || ''; }, configurable: true });
  var info = {};
  ['Title', 'Author', 'Subject', 'Keywords', 'Creator', 'Producer'].forEach(function (k) {
    var get = function () { return h.info(k) || ''; };
    Object.defineProperty(info, k, { get: get, enumerable: true });
    Object.defineProperty(info, k.toLowerCase(), { get: get });
  });
  G.info = info;

  function str(v) { return v === undefined || v === null ? '' : String(v); }
  function names(v) {
    if (v === undefined || v === null) return null;
    if (typeof v === 'string') return [v];
    return Array.prototype.slice.call(v).map(String);
  }
  G.submitForm = function (cURL, bFDF, bEmpty, aFields, bGet, bAnnotations, bXML, bIncrChanges, bPDF, bCanonical,
    bExclNonUserAnnots, bExclFKey, cPassword, bEmbedForm, oJavaScript, cSubmitAs, bInclNMKey, aPackets, cCharset,
    oXML, cPermID, cInstID, cUsageRights) {
    var o = cURL !== null && typeof cURL === 'object' ? cURL : {
      cURL: cURL, bFDF: bFDF, bEmpty: bEmpty, aFields: aFields, bGet: bGet, bXML: bXML, bPDF: bPDF,
      cSubmitAs: cSubmitAs, cCharset: cCharset
    };
    var as = o.cSubmitAs ? String(o.cSubmitAs).toUpperCase()
      : o.bPDF ? 'PDF' : o.bXML ? 'XFDF' : o.bFDF === false ? 'HTML' : 'FDF';
    h.submit(str(o.cURL), as, !!o.bEmpty, names(o.aFields), !!o.bGet, str(o.cCharset));
  };
  G.resetForm = function (aFields) {
    if (aFields !== null && typeof aFields === 'object' && !Array.isArray(aFields) && 'aFields' in aFields) aFields = aFields.aFields;
    h.reset(names(aFields));
  };
  G.print = function (bUI, nStart, nEnd) {
    var o = bUI !== null && typeof bUI === 'object' ? bUI : { nStart: nStart, nEnd: nEnd };
    h.print(o.nStart, o.nEnd);
  };
  function mailer(kind) {
    return function (bUI, cTo, cCc, cBcc, cSubject, cMsg) {
      var o = bUI !== null && typeof bUI === 'object' ? bUI : { cTo: cTo, cCc: cCc, cBcc: cBcc, cSubject: cSubject, cMsg: cMsg };
      h.mail(kind, str(o.cTo), str(o.cCc), str(o.cBcc), str(o.cSubject), str(o.cMsg));
    };
  }
  G.mailDoc = mailer('doc');
  G.mailForm = mailer('form');
  G.importAnFDF = function (cPath) { h.importData(str(cPath)); };
  G.importAnXFDF = G.importAnFDF;
  G.getURL = function (cURL) { h.launchURL(str(cURL)); };

  G.app = {
    viewerType: 'Reader', viewerVariation: 'Reader', viewerVersion: 11, formsVersion: 11, platform: 'WIN',
    language: 'ENU', calculate: true, runtimeHighlight: false, focusRect: true, fs: { isFullScreen: false },
    alert: function (cMsg, nIcon, nType, cTitle) {
      if (cMsg !== null && typeof cMsg === 'object') { nIcon = cMsg.nIcon; nType = cMsg.nType; cTitle = cMsg.cTitle; cMsg = cMsg.cMsg; }
      return h.alert(str(cMsg), nIcon === undefined ? 0 : nIcon, nType === undefined ? 0 : nType, str(cTitle));
    },
    beep: function (nType) { h.beep(nType === undefined ? 4 : nType); },
    response: function (cQuestion, cTitle, cDefault, bPassword, cLabel) {
      if (cQuestion !== null && typeof cQuestion === 'object') {
        cTitle = cQuestion.cTitle; cDefault = cQuestion.cDefault; bPassword = cQuestion.bPassword; cLabel = cQuestion.cLabel;
        cQuestion = cQuestion.cQuestion;
      }
      return h.response(str(cQuestion), str(cTitle), str(cDefault), !!bPassword, str(cLabel));
    },
    launchURL: function (cURL) {
      if (cURL !== null && typeof cURL === 'object') cURL = cURL.cURL;
      h.launchURL(str(cURL));
    },
    mailMsg: function (bUI, cTo, cCc, cBcc, cSubject, cMsg) { mailer('msg')(bUI, cTo, cCc, cBcc, cSubject, cMsg); },
    setTimeOut: function () { return {}; },
    setInterval: function () { return {}; },
    clearTimeOut: noop,
    clearInterval: noop
  };
  G.console = {
    println: function (m) { h.log(String(m)); },
    show: noop, hide: noop, clear: noop
  };

  var printdFormats = ['D:yyyymmddHHMMss', 'yyyy.mm.dd HH:MM:ss', 'm/d/yy h:MM:ss tt'];
  G.util = {
    printf: function () { return h.printf.apply(null, arguments); },
    printd: function (f, d) {
      if (!(d instanceof Date) || isNaN(d.getTime())) return null;
      if (typeof f === 'number') f = printdFormats[f] || printdFormats[0];
      return h.printd(String(f), d.getFullYear(), d.getMonth() + 1, d.getDate(), d.getHours(), d.getMinutes(), d.getSeconds());
    },
    scand: function (f, s) {
      var p = h.parseDate(String(s), String(f));
      return p ? new Date(p[0], p[1] - 1, p[2], p[3], p[4], p[5]) : null;
    },
    printx: function (f, s) {
      f = str(f); s = str(s);
      var out = '', j = 0, cs = 0;
      function conv(c) { return cs === 1 ? c.toUpperCase() : cs === 2 ? c.toLowerCase() : c; }
      for (var i = 0; i < f.length; i++) {
        var c = f.charAt(i);
        if (c === '?') { if (j < s.length) out += conv(s.charAt(j++)); }
        else if (c === 'X' || c === 'A' || c === '9') {
          var re = c === 'X' ? /[A-Za-z0-9]/ : c === 'A' ? /[A-Za-z]/ : /[0-9]/;
          while (j < s.length && !re.test(s.charAt(j))) j++;
          if (j < s.length) out += conv(s.charAt(j++));
        }
        else if (c === '*') { while (j < s.length) out += conv(s.charAt(j++)); }
        else if (c === '\\') { if (i + 1 < f.length) out += f.charAt(++i); }
        else if (c === '>') cs = 1;
        else if (c === '<') cs = 2;
        else if (c === '=') cs = 0;
        else out += c;
      }
      return out;
    }
  };

  function AFMakeNumber(s) {
    if (typeof s === 'number') return s;
    return h.makeNumber(s === undefined || s === null ? '' : String(s));
  }
  G.AFMakeNumber = AFMakeNumber;
  G.AFMergeChange = function (e) {
    if (e.willCommit) return e.value;
    var v = String(e.value);
    var start = e.selStart >= 0 ? e.selStart : v.length, end = e.selEnd >= 0 ? e.selEnd : v.length;
    return v.substring(0, start) + e.change + v.substring(end);
  };

  function fieldName() { return G.event && G.event.target ? G.event.target.name : ''; }
  function reject(msg) { G.app.alert(msg); G.event.rc = false; }
  function mismatch() { reject('The value entered does not match the format of the field [ ' + fieldName() + ' ]'); }

  G.AFNumber_Format = function (nDec, sepStyle, negStyle, currStyle, strCurrency, bCurrencyPrepend) {
    var e = G.event, r = h.fmtNumber(String(e.value), nDec, sepStyle, negStyle, strCurrency === undefined ? '' : String(strCurrency), bCurrencyPrepend !== false);
    if (r === null) return;
    e.value = r[0];
    if ((negStyle === 1 || negStyle === 3) && e.target) e.target.textColor = r[1] ? color.red : color.black;
  };
  G.AFNumber_Keystroke = function () {
    var e = G.event;
    if (!e.willCommit) { if (!/^[0-9\s.,+\-()$€£¥%']*$/.test(String(e.change))) e.rc = false; return; }
    var v = String(e.value);
    if (v.replace(/\s/g, '') !== '' && AFMakeNumber(v) === null) mismatch();
  };
  G.AFPercent_Format = function (nDec, sepStyle, bPercentPrepend) {
    var e = G.event, r = h.fmtPercent(String(e.value), nDec, sepStyle, !!bPercentPrepend);
    if (r !== null) e.value = r;
  };
  G.AFPercent_Keystroke = G.AFNumber_Keystroke;

  var dateFormats = ['m/d', 'm/d/yy', 'mm/dd/yy', 'mm/yy', 'd-mmm', 'd-mmm-yy', 'dd-mmm-yy', 'yy-mm-dd', 'mmm-yy',
    'mmmm-yy', 'mmm d, yyyy', 'mmmm d, yyyy', 'm/d/yy h:MM tt', 'm/d/yy HH:MM'];
  var timeFormats = ['HH:MM', 'h:MM tt', 'HH:MM:ss', 'h:MM:ss tt'];
  function formatDate(f) {
    var e = G.event, v = String(e.value);
    if (v === '') return;
    var r = h.fmtDate(v, String(f));
    if (r !== null) e.value = r;
  }
  function keyDate(f, what) {
    var e = G.event;
    if (!e.willCommit) return;
    var v = String(e.value);
    if (v !== '' && h.parseDate(v, String(f)) === null)
      reject('Invalid ' + what + ': please ensure that the ' + what + ' exists. Field [ ' + fieldName() + ' ] should match format ' + f);
  }
  G.AFDate_FormatEx = function (f) { formatDate(f); };
  G.AFDate_Format = function (n) { formatDate(dateFormats[n] || dateFormats[0]); };
  G.AFDate_KeystrokeEx = function (f) { keyDate(f, 'date/time'); };
  G.AFDate_Keystroke = function (n) { keyDate(dateFormats[n] || dateFormats[0], 'date/time'); };
  G.AFTime_FormatEx = function (f) { formatDate(f); };
  G.AFTime_Format = function (n) { formatDate(timeFormats[n] || timeFormats[0]); };
  G.AFTime_KeystrokeEx = function (f) { keyDate(f, 'time'); };
  G.AFTime_Keystroke = function (n) { keyDate(timeFormats[n] || timeFormats[0], 'time'); };
  G.AFParseDateEx = function (s, f) { return G.util.scand(f, s); };

  G.AFSpecial_Format = function (n) {
    var e = G.event, v = String(e.value);
    if (v !== '') e.value = h.fmtSpecial(v, n);
  };
  G.AFExactMatch = function (re, s) {
    var list = re instanceof RegExp ? [re] : re || [];
    s = str(s);
    for (var i = 0; i < list.length; i++) {
      var m = s.match(list[i]);
      if (m && m[0] === s) return i + 1;
    }
    return 0;
  };

  // Live typing against a mask (9 digit, A letter, O letter or digit, X anything; other characters are literals):
  // at the end of the text the mask's literals are filled in as you type; elsewhere characters only have to fit the mask.
  var maskTest = { '9': /[0-9]/, 'A': /[A-Za-z]/, 'O': /[A-Za-z0-9]/, 'X': /[\s\S]/ };
  function fits(v, mask) {
    if (v.length !== mask.length) return false;
    for (var i = 0; i < v.length; i++) {
      var m = mask.charAt(i), t = maskTest[m];
      if (t ? !t.test(v.charAt(i)) : v.charAt(i) !== m) return false;
    }
    return true;
  }
  function placeholders(mask) { return mask.replace(/[^9AOX]/g, ''); }
  function applyMask(v, mask) {
    var out = '', j = 0;
    for (var i = 0; i < mask.length; i++) out += maskTest[mask.charAt(i)] ? v.charAt(j++) : mask.charAt(i);
    return out;
  }
  function liveMask(e, mask) {
    var v = String(e.value), start = e.selStart >= 0 ? e.selStart : v.length, end = e.selEnd >= 0 ? e.selEnd : v.length;
    var head = v.substring(0, start), tail = v.substring(end), add = String(e.change), out = '', k, ch;
    if (tail !== '') {
      for (k = 0; k < add.length; k++) {
        ch = add.charAt(k);
        var ok = false;
        for (var m = 0; m < mask.length && !ok; m++) ok = maskTest[mask.charAt(m)] ? maskTest[mask.charAt(m)].test(ch) : mask.charAt(m) === ch;
        if (!ok) { e.rc = false; return; }
      }
      return;
    }
    for (k = 0; k < add.length; k++) {
      ch = add.charAt(k);
      var pos = head.length + out.length;
      while (pos < mask.length && !maskTest[mask.charAt(pos)] && ch !== mask.charAt(pos)) { out += mask.charAt(pos); pos++; }
      if (pos >= mask.length) { e.rc = false; return; }
      var mc = mask.charAt(pos), t = maskTest[mc];
      if (t ? !t.test(ch) : ch !== mc) { e.rc = false; return; }
      out += ch;
    }
    e.change = out;
  }
  G.AFSpecial_KeystrokeEx = function (mask) {
    var e = G.event;
    mask = str(mask);
    if (mask === '') return;
    if (!e.willCommit) { if (e.name === 'Keystroke') liveMask(e, mask); return; }
    var v = String(e.value);
    if (v === '' || fits(v, mask)) return;
    var p = placeholders(mask);
    if (p !== mask && fits(v, p)) { e.value = applyMask(v, mask); return; }
    reject('The value entered does not match the format of the field [ ' + fieldName() + ' ]: ' + mask);
  };

  // Zip, Zip+4, phone and social security numbers: digits only, the separators appear as you type.
  function liveDigits(e, n) {
    var v = String(e.value), start = e.selStart >= 0 ? e.selStart : v.length, end = e.selEnd >= 0 ? e.selEnd : v.length;
    var change = String(e.change);
    if (!/^[0-9\s\-().+]*$/.test(change)) { e.rc = false; return; }
    if (end < v.length) return;
    var typed = v.substring(0, start) + change, digits = typed.replace(/\D/g, '');
    var mask = n === 0 ? '99999' : n === 1 ? '99999-9999' : n === 3 ? '999-99-9999'
      : digits.length > 7 || /^\s*\(/.test(typed) ? '(999) 999-9999' : '999-9999';
    if (digits.length > placeholders(mask).length) { e.rc = false; return; }
    var out = '', j = 0;
    for (var i = 0; i < mask.length && j < digits.length; i++) out += mask.charAt(i) === '9' ? digits.charAt(j++) : mask.charAt(i);
    if (out === '' && mask.charAt(0) === '(' && /\(/.test(typed)) out = '(';
    e.selStart = 0;
    e.selEnd = v.length;
    e.change = out;
  }

  G.AFSpecial_Keystroke = function (n) {
    var e = G.event;
    if (!e.willCommit) { if (e.name === 'Keystroke') liveDigits(e, n | 0); return; }
    var v = String(e.value);
    if (v === '') return;
    var digits = v.replace(/\D/g, '').length;
    var ok = /^[0-9\s\-().+]*$/.test(v) && (n === 0 ? digits === 5 : n === 1 ? digits === 9 || digits === 5
      : n === 2 ? digits === 7 || digits === 10 : n === 3 ? digits === 9 : true);
    if (!ok) mismatch();
  };

  G.AFSimple_Calculate = function (op, fields) {
    var names = typeof fields === 'string' ? fields.split(',') : fields || [];
    var res = op === 'PRD' ? 1 : 0, count = 0;
    for (var i = 0; i < names.length; i++) {
      var f = G.getField(String(names[i]).trim());
      if (!f) continue;
      var kids = f.getArray();
      for (var j = 0; j < kids.length; j++) {
        var n = AFMakeNumber(kids[j].value);
        if (n === null) n = 0;
        if (op === 'MIN') res = count === 0 ? n : Math.min(res, n);
        else if (op === 'MAX') res = count === 0 ? n : Math.max(res, n);
        else if (op === 'PRD') res *= n;
        else res += n;
        count++;
      }
    }
    if (op === 'AVG') res = count > 0 ? res / count : 0;
    G.event.value = res;
  };

  G.AFRange_Validate = function (bGreaterThan, nGreaterThan, bLessThan, nLessThan) {
    var e = G.event;
    if (String(e.value) === '') return;
    var v = AFMakeNumber(e.value);
    if (v === null) return;
    var msg = '';
    if (bGreaterThan && bLessThan) {
      if (v < nGreaterThan || v > nLessThan)
        msg = 'The value entered must be greater than or equal to ' + nGreaterThan + ' and less than or equal to ' + nLessThan + '.';
    } else if (bGreaterThan) {
      if (v < nGreaterThan) msg = 'The value entered must be greater than or equal to ' + nGreaterThan + '.';
    } else if (bLessThan) {
      if (v > nLessThan) msg = 'The value entered must be less than or equal to ' + nLessThan + '.';
    }
    if (msg) reject(msg);
  };

  return {
    begin: function (name, target, value, willCommit, change, selStart, selEnd) {
      joining.length = 0; // (a script stopped inside join leaves it behind)
      var t = target ? new Field(target) : null;
      G.event = {
        name: name, type: 'Field', value: value, change: change || '', changeEx: '', willCommit: !!willCommit, rc: true,
        target: t, targetName: target, source: t, selStart: selStart | 0, selEnd: selEnd | 0, modifier: false, shift: false,
        commitKey: willCommit ? 2 : 0, fieldFull: false, keyDown: name === 'Keystroke' && !willCommit
      };
    },
    end: function () {
      var e = G.event, v = e ? e.value : '';
      if (typeof v !== 'number' && typeof v !== 'string' && typeof v !== 'boolean') v = v === undefined || v === null ? '' : String(v);
      return [v, !e || e.rc !== false, e ? str(e.change) : '', e ? Number(e.selStart) : 0, e ? Number(e.selEnd) : 0];
    }
  };
})
""";
}
