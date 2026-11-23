/* SymbolPrint × SVG-Edit 桥接层。
   职责：
     1) 用官方 IIFE 包构造 Editor 实例，关掉存储与默认扩展（纯离线、无上传下载）；
     2) ready 之后载入 .NET 侧注入的初始 SVG（window.__SP_INITIAL_SVG__）；
     3) 「完成」时把画布导回 XML，去掉 SVG-Edit 私有 se:* 标记、补齐 width/height，
        通过 WebView2 postMessage 交给 SvgEditorForm。 */

(function () {
  'use strict'

  // .NET 侧在文档创建阶段注入；必须在编辑器渲染前落到 <html> 上，否则会闪一下浅色
  if (window.__SP_DARK__) document.documentElement.setAttribute('data-theme', 'dark')

  var hostStatus = document.getElementById('hostStatus')

  function setStatus (text) {
    if (hostStatus) hostStatus.textContent = text || ''
  }

  function post (obj) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(obj)
    }
  }

  function fail (message) {
    setStatus('')
    post({ type: 'error', message: message })
  }

  // SVG-Edit 内部出错不会弹到界面上（表现为按钮空白），全抓进日志
  window.addEventListener('error', function (ev) {
    post({ type: 'debug', message: 'JSERROR ' + (ev.message || '') + ' @' + (ev.filename || '') + ':' + (ev.lineno || 0) })
  })
  window.addEventListener('unhandledrejection', function (ev) {
    var r = ev && ev.reason
    post({ type: 'debug', message: 'JSREJECT ' + (r && r.message ? r.message : String(r)) })
  })

  var EditorCtor = (window.Editor && window.Editor.default) || window.Editor
  if (typeof EditorCtor !== 'function') {
    fail('SVG-Edit 内核未加载：iife-Editor.js 缺失或损坏。')
    return
  }

  var editor
  try {
    editor = new EditorCtor(document.getElementById('container'))
    // 必须挂到 window.svgEditor：bundle 内部的组件（se-button 等）在构造函数里读这个全局，
    // 拿不到就抛错、自定义元素升级失败 —— 表现就是工具栏 27 个按钮全是空白。
    window.svgEditor = editor
    editor.setConfig({
      // 纯离线：不要默认扩展（storage / opensave 之类会弹窗或碰网络）
      noDefaultExtensions: true,
      extensions: [],
      userExtensions: [],
      showRulers: true,
      // 关键：不让上一次 localStorage 里的内容盖掉我们注入的图案
      noStorageOnLoad: true,
      dimensions: [300, 300],
      gridColor: '#9aa3b0',
      allowInitialUserOverride: false
    }, { overwrite: true })
    editor.init()
  } catch (err) {
    fail('SVG-Edit 初始化失败：' + (err && err.message ? err.message : err))
    return
  }

  // 方便 .NET 侧诊断脚本读取
  window.__spEditor = editor

  var ready = false
  setStatus('正在载入编辑器…')

  editor.ready(function () {
    ready = true
    var initial = window.__SP_INITIAL_SVG__ || ''
    if (!initial.trim()) {
      setStatus('')
      post({ type: 'loaded' })
      return
    }
    return editor.loadFromString(initial, { noAlert: true }).then(function () {
      setStatus('')
      post({ type: 'loaded' })
    }).catch(function (err) {
      setStatus('')
      fail('载入初始图案失败：' + (err && err.message ? err.message : err))
    })
  })

  // 清掉 SVG-Edit 的私有命名空间痕迹，并保证有 width/height（下游 SVG.NET 依赖它）
  function normalizeSvg (text) {
    var doc
    try {
      doc = new DOMParser().parseFromString(text, 'image/svg+xml')
    } catch (e) {
      return text
    }
    if (!doc || !doc.documentElement || doc.getElementsByTagName('parsererror').length) return text

    var root = doc.documentElement
    var all = root.getElementsByTagName('*')
    for (var i = all.length - 1; i >= 0; i--) {
      var el = all[i]
      if (el.nodeName.toLowerCase().indexOf('se:') === 0) {
        if (el.parentNode) el.parentNode.removeChild(el)
      }
    }
    var nodes = [root].concat(Array.prototype.slice.call(root.getElementsByTagName('*')))
    nodes.forEach(function (el) {
      if (!el.attributes) return
      for (var j = el.attributes.length - 1; j >= 0; j--) {
        var a = el.attributes[j]
        if (a.name.toLowerCase().indexOf('se:') === 0) el.removeAttribute(a.name)
      }
    })

    if (!root.getAttribute('xmlns')) root.setAttribute('xmlns', 'http://www.w3.org/2000/svg')

    var vb = (root.getAttribute('viewBox') || '').trim().split(/[\s,]+/).map(parseFloat)
    var w = parseFloat(root.getAttribute('width'))
    var h = parseFloat(root.getAttribute('height'))

    // 没有 viewBox 时 SVG.NET 会按自己的 DPI 规则解释 width/height，导致图案缩放错位
    // （实测：300×300 的图渲到 512px 时圆只占左上角）。viewBox 必须补上。
    if (!(vb.length === 4 && isFinite(vb[2]) && isFinite(vb[3]) && vb[2] > 0 && vb[3] > 0)) {
      if (isFinite(w) && isFinite(h) && w > 0 && h > 0) {
        root.setAttribute('viewBox', '0 0 ' + w + ' ' + h)
      }
    } else if (!isFinite(w) || !isFinite(h)) {
      root.setAttribute('width', String(vb[2]))
      root.setAttribute('height', String(vb[3]))
    }

    return new XMLSerializer().serializeToString(root)
  }

  // 暴露给诊断脚本（DevTools / AutoScript）用，见 ?debug=1
  window.__spNormalizeSvg = normalizeSvg

  function currentSvg () {
    var raw = editor.svgCanvas.getSvgString()
    return normalizeSvg(raw)
  }

  document.getElementById('okBtn').addEventListener('click', function () {
    if (!ready) { fail('编辑器还没就绪，请稍候。'); return }
    var svg
    try {
      svg = currentSvg()
    } catch (err) {
      fail('导出图案失败：' + (err && err.message ? err.message : err))
      return
    }
    if (!svg || svg.indexOf('<svg') < 0) { fail('画布是空的，没有可保存的图案。'); return }
    post({ type: 'ok', svg: svg })
  })

  document.getElementById('cancelBtn').addEventListener('click', function () {
    post({ type: 'cancel' })
  })

  // 诊断用：?debug=1 时把原始导出结果打回 .NET，便于比对清理前后的差异
  if (location.search.indexOf('debug=1') >= 0) {
    editor.ready(function () {
      post({ type: 'debug', message: editor.svgCanvas.getSvgString() })
    })
  }
})()
