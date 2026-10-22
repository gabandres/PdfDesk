namespace PdfDesk.Core;

/// <summary>
/// The Acrobat object model form scripts see, written in JavaScript over a private host object (h). Evaluates to a
/// function taking h and returning { begin, end } for the host's event plumbing; h itself never becomes global.
/// Only what is defined here exists: no launchURL, submitForm, mailDoc, importDataObject, exportDataObject, print,
/// getURL, SOAP or Net.
/// </summary>
internal static class JsPrelude
{
    public const string Source = """
(function (h) {
  'use strict';
  var G = globalThis;

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
  fp.isBoxChecked = function (i) {
    var ev = this.exportValues, v = this.valueAsString;
    return v !== 'Off' && v !== '' && (ev.length === 0 || ev[i | 0] === v);
  };
  fp.isDefaultChecked = function () { return false; };
  fp.checkThisBox = function (i, b) {
    var ev = this.exportValues;
    if (b === false) { if (this.isBoxChecked(i)) this.value = 'Off'; }
    else if (ev.length > (i | 0)) this.value = ev[i | 0];
  };
  var noop = function () {};
  ['setItems', 'clearItems', 'insertItemAt', 'deleteItemAt', 'setFocus', 'setAction', 'setLock', 'buttonSetCaption',
   'buttonSetIcon', 'buttonImportIcon', 'signatureSetSeedValue'].forEach(function (n) { fp[n] = noop; });
  fp.buttonGetCaption = function () { return ''; };

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

  G.app = {
    viewerType: 'Reader', viewerVariation: 'Reader', viewerVersion: 11, formsVersion: 11, platform: 'WIN',
    language: 'ENU', calculate: true, runtimeHighlight: false, focusRect: true, fs: { isFullScreen: false },
    alert: function (m) {
      var msg = m !== null && typeof m === 'object' ? m.cMsg : m;
      h.alert(msg === undefined || msg === null ? '' : String(msg));
      return 1;
    },
    beep: noop,
    response: function () { return null; },
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
    printx: function (f, s) { return String(s); }
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
  G.AFSpecial_Keystroke = function (n) {
    var e = G.event;
    if (!e.willCommit) { if (!/^[0-9\s\-().+]*$/.test(String(e.change))) e.rc = false; return; }
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
    begin: function (name, target, value, willCommit) {
      var t = target ? new Field(target) : null;
      G.event = {
        name: name, type: 'Field', value: value, change: '', changeEx: '', willCommit: !!willCommit, rc: true,
        target: t, targetName: target, source: t, selStart: 0, selEnd: 0, modifier: false, shift: false,
        commitKey: willCommit ? 2 : 0, fieldFull: false, keyDown: false
      };
    },
    end: function () {
      var e = G.event, v = e ? e.value : '';
      if (typeof v !== 'number' && typeof v !== 'string' && typeof v !== 'boolean') v = v === undefined || v === null ? '' : String(v);
      return [v, !e || e.rc !== false];
    }
  };
})
""";
}
